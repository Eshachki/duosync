using System.Collections.Concurrent;
using System.Text;
using DuoSync.Core.Feed;
using DuoSync.Core.Git;
using DuoSync.Core.Unity;

namespace DuoSync.Core.Ops;

public sealed class SyncOptions
{
    public required string MeName { get; init; }
    /// <summary>Settable: the name is learned from the repository when nobody typed it.</summary>
    public required string FriendName { get; set; }
    /// <summary>Run <c>git lfs push</c> before push (off in tests without LFS).</summary>
    public bool PushLfs { get; init; } = true;
    /// <summary>Pauses before repeating a network step after a dropped connection (GitHub from Russia drops often).</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90) };
    /// <summary>Short progress lines for the window while an operation waits ("нет связи, повторю через 30 с").</summary>
    public Action<string>? Progress { get; init; }
}

/// <summary>
/// «Получить», «Отправить» and «Откатить получение» for one project (§3.6–3.9). Stage 1: clean merges only;
/// a real conflict stops the operation with nothing applied.
/// </summary>
public sealed class SyncEngine
{
    public const string LastRemoteRef = "refs/duosync/last-remote";
    public const string ReceiveBeforeRef = "refs/duosync/last-receive/before";
    public const string ReceiveAfterRef = "refs/duosync/last-receive/after";
    public const string ReceiveIncomingRef = "refs/duosync/last-receive/incoming";

    static readonly ConcurrentDictionary<string, SemaphoreSlim> RepoLocks = new(StringComparer.OrdinalIgnoreCase);

    readonly Repo _repo;
    readonly SyncOptions _opt;
    readonly IUnityBridge _unity;
    readonly Snapshots _snapshots;
    readonly Applier _applier;
    readonly Merger _merger;

    public SyncEngine(Repo repo, SyncOptions options, IUnityBridge? unity = null)
    {
        _repo = repo;
        _opt = options;
        _unity = unity ?? NoUnity.Instance;
        _snapshots = new Snapshots(repo);
        _applier = new Applier(repo, _snapshots, _unity);
        _merger = new Merger(repo);
    }

    public Repo Repo => _repo;
    public Applier Applier => _applier;
    public Snapshots Snapshots => _snapshots;

    SemaphoreSlim RepoLock => RepoLocks.GetOrAdd(Path.GetFullPath(_repo.Root), _ => new SemaphoreSlim(1, 1));

    /// <summary>Background status for the tray and notifications; skipped (Busy) while an operation runs.</summary>
    public Task<ProjectStatus> CheckStatusAsync(CancellationToken ct = default) => new StatusChecker(_repo).CheckAsync(RepoLock, ct);

    async Task<OpResult> Locked(Func<Task<OpResult>> body, CancellationToken ct)
    {
        await RepoLock.WaitAsync(ct);
        try
        {
            var result = await body();
            // Every outcome goes to the project feed; successes are written by the operation itself with details.
            if (!result.Succeeded)
                await FeedAsync(result.Status switch
                {
                    OpStatus.Conflict => "conflict",
                    OpStatus.Offline => "offline",
                    _ => "blocked",
                }, result.Message);
            return result;
        }
        finally { RepoLock.Release(); }
    }

    async Task FeedAsync(string kind, string text) => new ProjectFeed(await _repo.DuoDirAsync()).Add(kind, text);

    /// <summary>Repeats a network step after a dropped connection; other errors return at once.</summary>
    async Task<GitResult> WithRetryAsync(Func<Task<GitResult>> step, string what, CancellationToken ct)
    {
        GitResult r = await step();
        for (int i = 0; i < _opt.RetryDelays.Count && !r.Ok && GitErrors.Classify(r) is GitErrorKind.Network or GitErrorKind.Timeout; i++)
        {
            var delay = _opt.RetryDelays[i];
            _opt.Progress?.Invoke($"Нет связи с GitHub ({what}). Повторю через {delay.TotalSeconds:0} с, работа сохранена.");
            await Task.Delay(delay, ct);
            r = await step();
        }
        return r;
    }

    async Task<GitResult> FetchWithRetryAsync(CancellationToken ct)
    {
        var r = await WithRetryAsync(() => _repo.FetchAsync(ct: ct), "получение", ct);
        // A brand-new, empty GitHub repository has no main yet: that is "nothing there", not an error.
        if (!r.Ok && r.StdErr.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase))
        {
            await _repo.DeleteRefAsync(_repo.RemoteBranchRef);
            return r with { ExitCode = 0 };
        }
        return r;
    }

    // ------------------------------------------------------------------ Получить

    public Task<OpResult> ReceiveAsync(CancellationToken ct = default) => Locked(() => ReceiveCoreAsync(ct), ct);

    async Task<OpResult> ReceiveCoreAsync(CancellationToken ct)
    {
        var pre = await PreflightAsync(ct);
        if (pre != null) return pre;

        var fetch = await FetchWithRetryAsync(ct);
        if (!fetch.Ok) return FetchFailed(fetch);
        var theirs = await _repo.RevParseAsync(_repo.RemoteBranchRef);
        if (theirs == null) return new OpResult(OpStatus.UpToDate, "На GitHub пока ничего нет.");

        var rewritten = await CheckRewrittenAsync(theirs);
        if (rewritten != null) return rewritten;

        var head = await _repo.HeadAsync();
        if (head != null && await _repo.IsAncestorAsync(theirs, head))
            return new OpResult(OpStatus.UpToDate, "Уже актуально.");

        if (_unity.IsOpen)
        {
            var save = await _unity.SaveAsync(ct);
            if (!save.Ok) return OpResult.Blocked(save.Error ?? "Unity не сохранил сцены.");
        }

        var snapshot = await _snapshots.SnapshotAsync($"{_opt.MeName}: незаконченная работа (сохранено перед получением)", "snapshot");
        if (snapshot == null)
        {
            // Empty clone: nothing local to protect.
            var r = await _repo.Git.RunAsync(new[] { "checkout", "--no-overwrite-ignore", "-B", _repo.Branch, theirs },
                new GitRunOptions { Timeout = TimeSpan.FromMinutes(30) }, ct);
            return r.Ok ? new OpResult(OpStatus.Done, "Получено: первая загрузка проекта.") { After = theirs }
                        : OpResult.Blocked(GitErrors.Explain(r), r.StdErr);
        }

        string target;
        if (await _repo.IsAncestorAsync(snapshot, theirs)) target = theirs;
        else
        {
            var merge = await _merger.MergeAsync(snapshot, theirs, $"Слияние: {_opt.MeName} + {_opt.FriendName}", ct);
            if (!merge.Clean)
                return ConflictResult(merge, "Получить");
            target = merge.Commit!;
        }

        var applied = await _applier.ApplyAsync(snapshot, target, "получение", ct);
        if (applied.Status != OpStatus.Done) return applied;

        await _repo.UpdateRefAsync(ReceiveBeforeRef, snapshot);
        await _repo.UpdateRefAsync(ReceiveAfterRef, target);
        await _repo.UpdateRefAsync(ReceiveIncomingRef, theirs);
        var subjects = await _repo.CommitSubjectsAsync($"{snapshot}..{theirs}", 5);
        var text = $"Получено {Ru.Files(applied.Files.Count)} — прислал {_opt.FriendName}" +
                   (subjects.Count > 0 ? $" («{string.Join("», «", subjects)}»)" : "") + "." + UnityNote(applied);
        await FeedAsync("receive", text);
        return applied with { Message = text };
    }

    // ------------------------------------------------------------------ Отправить

    public Task<OpResult> SendAsync(string? message, IReadOnlyList<string>? extraPaths = null, CancellationToken ct = default)
        => Locked(() => SendCoreAsync(message, extraPaths, ct), ct);

    async Task<OpResult> SendCoreAsync(string? message, IReadOnlyList<string>? extraPaths, CancellationToken ct)
    {
        var pre = await PreflightAsync(ct);
        if (pre != null) return pre;

        if (_unity.IsOpen)
        {
            var save = await _unity.SaveAsync(ct);
            if (!save.Ok) return OpResult.Blocked(save.Error ?? "Unity не сохранил сцены.");
        }

        var text = string.IsNullOrWhiteSpace(message) ? await AutoSummaryAsync() : message.Trim();
        await _snapshots.SnapshotAsync(text, "send", extraPaths);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var fetch = await FetchWithRetryAsync(ct);
            if (!fetch.Ok) return FetchFailed(fetch);
            var theirs = await _repo.RevParseAsync(_repo.RemoteBranchRef);
            if (theirs != null)
            {
                var rewritten = await CheckRewrittenAsync(theirs);
                if (rewritten != null) return rewritten;
            }

            var head = await _repo.HeadAsync();
            if (head == null) return new OpResult(OpStatus.NothingToSend, "Нечего отправлять.");

            if (theirs != null && !await _repo.IsAncestorAsync(theirs, head))
            {
                var merge = await _merger.MergeAsync(head, theirs, $"Слияние: {_opt.MeName} + {_opt.FriendName}", ct);
                if (!merge.Clean) return ConflictResult(merge, "Отправить");
                var applied = await _applier.ApplyAsync(head, merge.Commit!, "слияние перед отправкой", ct);
                if (applied.Status != OpStatus.Done) return applied;
                head = merge.Commit!;
            }

            if (head == theirs) return new OpResult(OpStatus.NothingToSend, "Нечего отправлять.");

            var files = theirs == null
                ? GitParse.SplitZ((await _repo.Git.RunCheckedAsync("ls-tree", "-r", "-z", "--name-only", head)).StdOutBytes)
                : await _repo.DiffNamesAsync(theirs, head);

            if (_opt.PushLfs)
            {
                var lfsHead = head;
                var lfs = await WithRetryAsync(() => _repo.Git.RunAsync(new[] { "lfs", "push", _repo.Remote, lfsHead },
                    new GitRunOptions { Timeout = TimeSpan.FromMinutes(60) }, ct), "выгрузка файлов LFS", ct);
                if (!lfs.Ok) return FetchFailed(lfs);
            }

            var pushHead = head;
            var push = await WithRetryAsync(() => _repo.Git.RunAsync(new[] { "push", "--porcelain", _repo.Remote, $"{pushHead}:{_repo.LocalBranchRef}" },
                new GitRunOptions { Timeout = TimeSpan.FromMinutes(15) }, ct), "отправка", ct);
            var refs = GitParse.ParsePushPorcelain(push.StdOut);
            var mine = refs.FirstOrDefault(x => x.Ref == _repo.LocalBranchRef);

            if (push.Ok && mine is { Status: PushRefStatus.Ok or PushRefStatus.UpToDate })
                return await SentAsync(head, files, text);

            if (mine?.Status == PushRefStatus.Rejected || GitErrors.Classify(push) == GitErrorKind.NonFastForward)
                continue; // the friend was faster: fetch, merge, try again

            // The server may have accepted the push while the answer was lost.
            var (_, remoteSha) = await _repo.LsRemoteBranchAsync(background: false, ct);
            if (remoteSha == head) return await SentAsync(head, files, text);
            return GitErrors.Classify(push) is GitErrorKind.Network or GitErrorKind.Timeout
                ? new OpResult(OpStatus.Offline, "Нет связи с GitHub. Работа сохранена у тебя. Повтори «Отправить».") { Detail = push.StdErr }
                : OpResult.Blocked(GitErrors.Explain(push), push.StdErr);
        }
        return OpResult.Blocked($"{_opt.FriendName} отправляет одновременно с тобой. Нажми «Отправить» ещё раз.");
    }

    /// <summary>Description of everything that will go out (local commits not sent yet plus working changes) when «Что сделал» is empty.</summary>
    async Task<string> AutoSummaryAsync()
    {
        var changes = new Dictionary<string, char>(StringComparer.Ordinal);
        var head = await _repo.HeadAsync();
        var theirs = await _repo.RevParseAsync(_repo.RemoteBranchRef);
        if (head != null && theirs != null && await _repo.MergeBaseAsync(head, theirs) is { } mergeBase && mergeBase != head)
        {
            var fields = GitParse.SplitZ((await _repo.Git.RunCheckedAsync("diff", "--name-status", "-z", "--no-renames", mergeBase, head)).StdOutBytes);
            for (int i = 0; i + 1 < fields.Count; i += 2) changes[fields[i + 1]] = fields[i][0];
        }
        foreach (var e in await _repo.StatusAsync())
        {
            if (e.Kind == StatusKind.Ignored) continue;
            if (e.Kind == StatusKind.Untracked)
            {
                if (e.Path.StartsWith("Assets/", StringComparison.Ordinal) || e.Path.StartsWith("Packages/", StringComparison.Ordinal) ||
                    e.Path.StartsWith("ProjectSettings/", StringComparison.Ordinal))
                    changes[e.Path] = 'A';
                continue;
            }
            changes[e.Path] = e.XY.Contains('D') ? 'D' : changes.TryGetValue(e.Path, out var was) && was == 'A' ? 'A' : 'M';
        }
        return changes.Count == 0 ? $"{_opt.MeName}: изменения" : ChangeSummary.Describe(changes.Select(kv => (kv.Key, kv.Value)));
    }

    async Task<OpResult> SentAsync(string head, IReadOnlyList<string> files, string text)
    {
        await _repo.UpdateRefAsync(LastRemoteRef, head);
        await _repo.UpdateRefAsync("refs/duosync/last-head", head);
        foreach (var r in new[] { ReceiveBeforeRef, ReceiveAfterRef, ReceiveIncomingRef })
            await _repo.DeleteRefAsync(r);
        var msg = $"Отправлено «{FirstLine(text)}»: {Ru.Files(files.Count)}. {_opt.FriendName} увидит в течение минуты.";
        await FeedAsync("send", msg);
        return new OpResult(OpStatus.Done, msg) { Files = files, After = head };
    }

    // ------------------------------------------------------------------ Откатить получение

    public Task<OpResult> UndoReceiveAsync(CancellationToken ct = default) => Locked(() => UndoReceiveCoreAsync(ct), ct);

    async Task<OpResult> UndoReceiveCoreAsync(CancellationToken ct)
    {
        var before = await _repo.ReadRefAsync(ReceiveBeforeRef);
        var after = await _repo.ReadRefAsync(ReceiveAfterRef);
        if (before == null || after == null)
            return OpResult.Blocked("Откатывать нечего: после последнего получения ты уже отправлял, или получения не было.");

        var pre = await PreflightAsync(ct);
        if (pre != null) return pre;
        if (_unity.IsOpen)
        {
            var save = await _unity.SaveAsync(ct);
            if (!save.Ok) return OpResult.Blocked(save.Error ?? "Unity не сохранил сцены.");
        }

        var current = await _snapshots.SnapshotAsync($"{_opt.MeName}: правки после получения", "snapshot");
        string target;
        if (current == after) target = before;
        else
        {
            // "before + my edits made after the receive": three-way with the receive result as base.
            var r = await _repo.Git.RunAsync("merge-tree", "--write-tree", "-z", "--merge-base=" + after, current!, before);
            var tree = GitParse.ParseMergeTree(r);
            if (!tree.Clean)
                return OpResult.Blocked(
                    "После получения ты правил пришедшие файлы (" + string.Join(", ", tree.ConflictedPaths.Take(5)) +
                    "). Откатить их автоматически нельзя, ничего не изменено.");
            target = (await _repo.Git.RunCheckedAsync(new[] { "commit-tree", tree.TreeOid, "-p", before, "-F", "-" },
                new GitRunOptions { StdIn = Encoding.UTF8.GetBytes("Мои правки после отменённого получения\n\nDuoSync: restore\n") })).StdOutTrimmed;
        }

        var applied = await _applier.ApplyAsync(current!, target, "откат получения", ct);
        if (applied.Status != OpStatus.Done) return applied;
        foreach (var rf in new[] { ReceiveBeforeRef, ReceiveAfterRef, ReceiveIncomingRef })
            await _repo.DeleteRefAsync(rf);
        var msg = $"Получение откачено у тебя: вернул {Ru.Files(applied.Files.Count)}. То, что прислал {_opt.FriendName}, снова считается не полученным.";
        await FeedAsync("undo", msg);
        return applied with { Message = msg };
    }

    // ------------------------------------------------------------------ Проверки

    /// <summary>Returns null when the repository is fine for an operation, otherwise why not (§3.12).</summary>
    public async Task<OpResult?> PreflightAsync(CancellationToken ct = default)
    {
        var gitDir = await _repo.GitDirAsync();
        if (!Setup.ProjectSetup.IsPrepared(_repo.Root))
            return OpResult.Blocked("Проект не подготовлен для DuoSync. Нажми «Подготовить проект».");
        await new Setup.ProjectSetup(_repo, _opt.MeName, _opt.FriendName).EnsureLocalAsync();
        if (!_unity.IsOpen && UnityDetector.IsProjectOpen(_repo.Root))
            return OpResult.Blocked("Проект открыт в Unity. Пока мост DuoSync в проект не установлен, закрой Unity перед «Получить» и «Отправить».");
        var sym = await _repo.SymbolicHeadAsync();
        if (sym != _repo.LocalBranchRef)
            return OpResult.Blocked($"Проект не на ветке {_repo.Branch} (кто-то переключал git вручную). Нажми «Починить».");

        foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply" })
            if (File.Exists(Path.Combine(gitDir, marker)) || Directory.Exists(Path.Combine(gitDir, marker)))
                return OpResult.Blocked("Найдена незавершённая операция git (кто-то запускал git вручную). Нажми «Починить».", marker);

        var indexLock = Path.Combine(gitDir, "index.lock");
        if (File.Exists(indexLock))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(indexLock) > TimeSpan.FromSeconds(60)) File.Delete(indexLock);
            else return OpResult.Blocked("Git сейчас занят другой программой. Повтори через минуту.");
        }

        var remote = await _repo.Git.RunAsync("remote", "get-url", _repo.Remote);
        if (!remote.Ok) return OpResult.Blocked("У проекта нет адреса репозитория на GitHub. Нажми «Починить».");

        var recovered = await _applier.RecoverAsync(ct);
        if (recovered != null)
        {
            await FeedAsync("recover", recovered.Message);
            if (recovered.Status == OpStatus.Blocked && recovered.Detail != null) return recovered;
        }
        return null;
    }

    /// <summary>Stops when the remote branch no longer contains what we saw last time (someone force-pushed).</summary>
    public async Task<OpResult?> CheckRewrittenAsync(string theirs)
    {
        var last = await _repo.ReadRefAsync(LastRemoteRef);
        if (last == null || last == theirs || await _repo.IsAncestorAsync(last, theirs))
        {
            await _repo.UpdateRefAsync(LastRemoteRef, theirs);
            return null;
        }
        return OpResult.Blocked(
            $"История на GitHub переписана (было {last[..7]}, стало {theirs[..7]}). Обмен остановлен, пока не решишь: вернуть как было или принять как на GitHub.");
    }

    OpResult ConflictResult(MergeOutcome merge, string op)
    {
        var paths = merge.Tree.ConflictedPaths;
        return new OpResult(OpStatus.Conflict,
            $"«{op}»: то, что прислал {_opt.FriendName}, спорит с твоими правками ({Ru.Files(paths.Count)}). Ничего не применено, твоя работа сохранена. " +
            "Слияние через Claude появится на этапе 3.")
        { ConflictedPaths = paths };
    }

    static OpResult FetchFailed(GitResult r) => GitErrors.Classify(r) is GitErrorKind.Network or GitErrorKind.Timeout
        ? new OpResult(OpStatus.Offline, "Нет связи с GitHub. Работа сохранена у тебя.") { Detail = r.StdErr }
        : OpResult.Blocked(GitErrors.Explain(r), r.StdErr);

    static string FirstLine(string s) => s.Split('\n')[0].Trim();

    /// <summary>What Unity said after the files were applied: compile errors or a bridge problem.</summary>
    static string UnityNote(OpResult applied)
    {
        if (applied.CompileErrors.Count > 0)
            return $" Внимание: в Unity {applied.CompileErrors.Count} {Ru.Plural(applied.CompileErrors.Count, "ошибка", "ошибки", "ошибок")} компиляции, " +
                   $"например: {applied.CompileErrors[0]}. Сцены друга не сохраняются, пока ошибки не исправлены.";
        return string.IsNullOrEmpty(applied.Detail) ? "" : " Unity: " + applied.Detail;
    }
}
