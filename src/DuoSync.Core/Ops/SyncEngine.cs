using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DuoSync.Core.Feed;
using DuoSync.Core.Git;
using DuoSync.Core.Merge;
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
    /// <summary>
    /// This computer resolves conflicts (the integrator: Claude Code is here). Elsewhere a conflict goes to GitHub
    /// as a merge request and waits for the integrator (§3.13).
    /// </summary>
    public bool CanResolve { get; set; }
    /// <summary>Claude on the integrator's computer; set once it is found.</summary>
    public IConflictResolver? Resolver { get; set; }
    /// <summary>Where the patched copies of UnityYAMLMerge live (§5.4).</summary>
    public string ToolCacheDir { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuoSync", "uym");
}

/// <summary>The friend's work waiting in <c>refs/heads/duosync/merge/&lt;name&gt;</c> for the integrator to merge.</summary>
public sealed record MergeRequestInfo(string Ref, string Sha, string Author, string Subject, IReadOnlyList<string> Files, DateTimeOffset At);

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
    YamlMergeTool? _yamlTool;
    bool _yamlToolLooked;

    public SyncEngine(Repo repo, SyncOptions options, IUnityBridge? unity = null)
    {
        _repo = repo;
        _opt = options;
        _unity = unity ?? NoUnity.Instance;
        _snapshots = new Snapshots(repo);
        _applier = new Applier(repo, _snapshots, _unity);
    }

    public Repo Repo => _repo;
    public Applier Applier => _applier;
    public Snapshots Snapshots => _snapshots;

    SemaphoreSlim RepoLock => RepoLocks.GetOrAdd(Path.GetFullPath(_repo.Root), _ => new SemaphoreSlim(1, 1));

    /// <summary>Background status for the tray and notifications; skipped (Busy) while an operation runs.</summary>
    public Task<ProjectStatus> CheckStatusAsync(CancellationToken ct = default) => new StatusChecker(_repo, _opt.CanResolve, _opt.MeName).CheckAsync(RepoLock, ct);

    /// <summary>
    /// Publishes this computer's status report to its own status branch (§6а). Called after the person's own
    /// operations and by their button; waits for a running operation.
    /// </summary>
    public async Task<GitResult> PublishStatusAsync(StatusReport report, bool interactive, CancellationToken ct = default)
    {
        await RepoLock.WaitAsync(ct);
        try { return await StatusReports.PublishAsync(_repo, report, interactive, ct); }
        finally { RepoLock.Release(); }
    }

    async Task<OpResult> Locked(Func<Task<OpResult>> body, CancellationToken ct)
    {
        await RepoLock.WaitAsync(ct);
        try
        {
            var result = await body();
            // Every outcome goes to the project feed; successes and requests are written by the operation itself.
            if (!result.Succeeded && result.Status != OpStatus.Requested)
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
        IReadOnlyList<string> notes = Array.Empty<string>();
        if (await _repo.IsAncestorAsync(snapshot, theirs)) target = theirs;
        else
        {
            var merged = await AutoMergeAsync(snapshot, theirs, "Получить", ct);
            if (merged.Commit == null) return merged.Stop!;
            target = merged.Commit;
            notes = merged.Notes;
        }

        var applied = await _applier.ApplyAsync(snapshot, target, "получение", ct);
        if (applied.Status != OpStatus.Done) return applied;

        await _repo.UpdateRefAsync(ReceiveBeforeRef, snapshot);
        await _repo.UpdateRefAsync(ReceiveAfterRef, target);
        await _repo.UpdateRefAsync(ReceiveIncomingRef, theirs);
        var subjects = await _repo.CommitSubjectsAsync($"{snapshot}..{theirs}", 5);
        foreach (var note in notes) await FeedAsync("merge", note);
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
                var merged = await AutoMergeAsync(head, theirs, "Отправить", ct);
                if (merged.Commit == null) return merged.Stop!;
                var applied = await _applier.ApplyAsync(head, merged.Commit, "слияние перед отправкой", ct);
                if (applied.Status != OpStatus.Done) return applied;
                foreach (var note in merged.Notes) await FeedAsync("merge", note);
                head = merged.Commit;
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
        // An empty clone (the friend downloaded the repository before its first send) has nothing to protect:
        // the first «Получить» brings the preparation itself.
        if (!Setup.ProjectSetup.IsPrepared(_repo.Root) && await _repo.HeadAsync() != null)
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

    OpResult ConflictResult(IReadOnlyList<string> paths, string op) =>
        new(OpStatus.Conflict,
            $"«{op}»: то, что прислал {_opt.FriendName}, спорит с твоими правками ({Ru.Files(paths.Count)}: {string.Join(", ", paths.Take(3))}). " +
            "Ничего не применено, твоя работа сохранена. Нажми «Слить с Claude».")
        { ConflictedPaths = paths };

    // ------------------------------------------------------------------ Слияние без Claude (L1–L3, L5)

    YamlMergeTool? YamlTool()
    {
        if (_yamlToolLooked) return _yamlTool;
        _yamlToolLooked = true;
        try { _yamlTool = YamlMergeTool.Find(_repo.Root, _opt.ToolCacheDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _yamlTool = null; }
        return _yamlTool;
    }

    MergePipeline Pipeline() => new(_repo, YamlTool, YamlMergeTool.NeedsNoMappingFlag(_repo.Root));

    async Task<string> CommitMergeAsync(string tree, string mine, string theirs, string? request = null)
    {
        var body = $"Слияние: {_opt.MeName} + {_opt.FriendName}\n\nDuoSync: merge\n" + (request != null ? $"DuoSync-Request: {request}\n" : "");
        return (await _repo.Git.RunCheckedAsync(new[] { "commit-tree", tree, "-p", mine, "-p", theirs, "-F", "-" },
            new GitRunOptions { StdIn = Encoding.UTF8.GetBytes(body) })).StdOutTrimmed;
    }

    /// <summary>
    /// The merge every «Получить» and «Отправить» does: git's text merge, the rules and UnityYAMLMerge, then the tree
    /// check. A commit when everything was decided without Claude; the friend's side also needs that nothing was
    /// thrown away. Otherwise the button «Слить с Claude» (integrator) or a merge request (friend).
    /// </summary>
    async Task<(string? Commit, OpResult? Stop, IReadOnlyList<string> Notes)> AutoMergeAsync(string mine, string theirs, string op, CancellationToken ct)
    {
        var pipeline = await Pipeline().RunAsync(mine, theirs, ct);
        if (pipeline.Stop != null) return (null, OpResult.Blocked(pipeline.Stop), Array.Empty<string>());
        IReadOnlyList<string> disputed = pipeline.Paths;
        if (pipeline.Clean && (_opt.CanResolve || !pipeline.DiscardsASide))
        {
            var commit = await CommitMergeAsync(pipeline.Tree, mine, theirs);
            var problems = await TreeCheck.CheckAsync(_repo, commit, mine, theirs, ct);
            if (problems.Count == 0) return (commit, null, pipeline.Notes);
            await FeedAsync("check", "Проверка итога слияния: " + string.Join("; ", problems.Take(5)));
            disputed = problems.Select(p => p[..p.IndexOf(": ", StringComparison.Ordinal)]).Distinct().ToList();
        }
        else if (pipeline.Clean) disputed = pipeline.Notes.Select(n => n[..Math.Max(0, n.IndexOf(':'))]).ToList();
        return (null, _opt.CanResolve ? ConflictResult(disputed, op) : await SubmitRequestAsync(mine, disputed, ct), Array.Empty<string>());
    }

    /// <summary>Paths the tree check refused, as whole-file choices for Claude (it may take one side of each).</summary>
    async Task<List<ConflictItem>> WholeFileItemsAsync(string mine, string theirs, IEnumerable<string> paths, IReadOnlyList<string> problems)
    {
        var mergeBase = await _repo.MergeBaseAsync(mine, theirs);
        var items = new List<ConflictItem>();
        foreach (var path in paths)
        {
            var (b, m, t) = (mergeBase != null ? await _repo.BlobAsync(mergeBase, path) : null,
                await _repo.BlobAsync(mine, path), await _repo.BlobAsync(theirs, path));
            items.Add(new ConflictItem(path, m == null || t == null ? ConflictKind.ModifyDelete : ConflictKind.Binary, b, m, t, "100644")
            {
                Disputes = problems.Where(p => p.StartsWith(path + ": ", StringComparison.Ordinal)).ToList(),
            });
        }
        return items;
    }

    // ------------------------------------------------------------------ Просьба о слиянии (у друга, §3.13)

    /// <summary>This person's latest merge request (local ref): the notification «слил» comes once it is in main.</summary>
    public const string RequestRef = "refs/duosync/request";

    string RequestBranchRef => $"refs/heads/duosync/merge/{Slug(_opt.MeName)}";

    /// <summary>A branch name from a person's name: ASCII letters and digits, otherwise a short hash.</summary>
    public static string Slug(string name)
    {
        var ascii = new string(name.ToLowerInvariant().Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-').ToArray()).Trim('-');
        return ascii.Length > 0 ? ascii : "u" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
    }

    /// <summary>
    /// No Claude here: the snapshot goes to the person's own service branch on GitHub and waits for the integrator.
    /// Nothing is chosen and nothing applied; the next request of the same person normally fast-forwards the branch.
    /// </summary>
    async Task<OpResult> SubmitRequestAsync(string work, IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (_opt.PushLfs)
        {
            var lfs = await WithRetryAsync(() => _repo.Git.RunAsync(new[] { "lfs", "push", _repo.Remote, work },
                new GitRunOptions { Timeout = TimeSpan.FromMinutes(60) }, ct), "выгрузка файлов LFS", ct);
            if (!lfs.Ok) return FetchFailed(lfs);
        }
        var branch = RequestBranchRef;
        Task<GitResult> Push() => WithRetryAsync(() => _repo.Git.RunAsync(new[] { "push", "--porcelain", _repo.Remote, $"{work}:{branch}" },
            new GitRunOptions { Timeout = TimeSpan.FromMinutes(15) }, ct), "отправка на слияние", ct);
        var push = await Push();
        var mine = GitParse.ParsePushPorcelain(push.StdOut).FirstOrDefault(x => x.Ref == branch);
        if (mine?.Status == PushRefStatus.Rejected || (!push.Ok && GitErrors.Classify(push) == GitErrorKind.NonFastForward))
        {
            // The old request is not an ancestor (after an undo): replace this person's own service branch, never main.
            await _repo.Git.RunAsync(new[] { "push", _repo.Remote, "--delete", branch }, new GitRunOptions { Timeout = TimeSpan.FromMinutes(5) }, ct);
            push = await Push();
            mine = GitParse.ParsePushPorcelain(push.StdOut).FirstOrDefault(x => x.Ref == branch);
        }
        if (!push.Ok || mine is not { Status: PushRefStatus.Ok or PushRefStatus.UpToDate }) return FetchFailed(push);

        await _repo.UpdateRefAsync(RequestRef, work);
        var msg = $"Твои правки пересекаются с тем, что прислал {_opt.FriendName}: {Ru.Files(paths.Count)} ({string.Join(", ", paths.Take(3))}). " +
                  $"Работа сохранена и отправлена на слияние, его сделает {_opt.FriendName}. Когда будет готово, придёт уведомление: тогда нажми «Получить».";
        await FeedAsync("request", msg);
        return new OpResult(OpStatus.Requested, msg) { ConflictedPaths = paths };
    }

    /// <summary>The integrator merged the request: forget it (the notification was shown).</summary>
    public async Task ClearRequestAsync() => await _repo.DeleteRefAsync(RequestRef);

    // ------------------------------------------------------------------ Слить с Claude (у сводящего, §5.5)

    /// <summary>
    /// Merges what spoils the exchange: main (after a conflicting «Получить»/«Отправить») or the friend's request.
    /// Claude resolves the disputed paths in the work folder; the program checks, commits and applies.
    /// Push stays with the person («Отправить»). <paramref name="ask"/> shows Claude's question and returns the answer.
    /// </summary>
    public Task<OpResult> MergeWithClaudeAsync(MergeRequestInfo? request, Func<string, Task<string?>> ask, CancellationToken ct = default)
        => Locked(() => MergeWithClaudeCoreAsync(request, ask, ct), ct);

    async Task<OpResult> MergeWithClaudeCoreAsync(MergeRequestInfo? request, Func<string, Task<string?>> ask, CancellationToken ct)
    {
        if (!_opt.CanResolve || _opt.Resolver == null)
            return OpResult.Blocked("На этом компьютере нет Claude Code: сливать может только тот, у кого он есть.");
        var pre = await PreflightAsync(ct);
        if (pre != null) return pre;
        var fetch = await FetchWithRetryAsync(ct);
        if (!fetch.Ok) return FetchFailed(fetch);
        var main = await _repo.RevParseAsync(_repo.RemoteBranchRef);

        string theirs;
        if (request != null)
        {
            var local = $"refs/remotes/{_repo.Remote}/{request.Ref["refs/heads/".Length..]}";
            var got = await WithRetryAsync(() => _repo.Git.RunAsync(new[] { "fetch", "--no-tags", _repo.Remote, $"+{request.Ref}:{local}" },
                new GitRunOptions { Timeout = TimeSpan.FromMinutes(30) }, ct), "получение просьбы", ct);
            if (!got.Ok) return FetchFailed(got);
            theirs = await _repo.ReadRefAsync(local) ?? request.Sha;
            var current = await _repo.HeadAsync();
            if (main != null && current != null && !await _repo.IsAncestorAsync(main, current))
                return OpResult.Blocked("Сначала нажми «Получить»: на GitHub есть то, чего у тебя ещё нет. Потом «Слить с Claude».");
        }
        else if (main == null) return new OpResult(OpStatus.UpToDate, "Сливать нечего.");
        else theirs = main;

        if (_opt.PushLfs)
            await _repo.Git.RunAsync(new[] { "lfs", "fetch", _repo.Remote, theirs }, new GitRunOptions { Timeout = TimeSpan.FromMinutes(30) }, ct);
        if (_unity.IsOpen)
        {
            var save = await _unity.SaveAsync(ct);
            if (!save.Ok) return OpResult.Blocked(save.Error ?? "Unity не сохранил сцены.");
        }
        var snapshot = await _snapshots.SnapshotAsync($"{_opt.MeName}: незаконченная работа (сохранено перед слиянием)", "snapshot");
        if (snapshot == null || await _repo.IsAncestorAsync(theirs, snapshot)) return new OpResult(OpStatus.UpToDate, "Уже слито.");

        string? target = null;
        MergeResolution? resolution = null;
        IReadOnlyList<string> notes = Array.Empty<string>();
        if (await _repo.IsAncestorAsync(snapshot, theirs)) target = theirs;
        else
        {
            var pipeline = await Pipeline().RunAsync(snapshot, theirs, ct);
            if (pipeline.Stop != null) return OpResult.Blocked(pipeline.Stop);
            notes = pipeline.Notes;
            if (pipeline.Clean)
            {
                var commit = await CommitMergeAsync(pipeline.Tree, snapshot, theirs, request?.Sha);
                var problems = await TreeCheck.CheckAsync(_repo, commit, snapshot, theirs, ct);
                if (problems.Count == 0) target = commit;
                else
                {
                    // The deterministic result broke something: those files go to Claude as whole-file choices.
                    var paths = problems.Select(p => p[..p.IndexOf(": ", StringComparison.Ordinal)]).Distinct();
                    pipeline = pipeline with { Unresolved = await WholeFileItemsAsync(snapshot, theirs, paths, problems) };
                }
            }
            if (target == null)
            {
                var job = await MergeJob.PrepareAsync(_repo, snapshot, theirs, pipeline, _opt.MeName, _opt.FriendName,
                    Path.GetFileName(_repo.Root.TrimEnd('\\', '/')), request?.Sha, YamlTool(), YamlMergeTool.NeedsNoMappingFlag(_repo.Root), ct);
                var (commit, result, failed) = await ResolveWithAsync(job, ask, ct);
                if (commit == null) return failed!;
                target = commit;
                resolution = result;
            }
        }

        var applied = await _applier.ApplyAsync(snapshot, target, "слияние", ct);
        if (applied.Status != OpStatus.Done) return applied;
        await _repo.UpdateRefAsync(ReceiveBeforeRef, snapshot);
        await _repo.UpdateRefAsync(ReceiveAfterRef, target);
        await _repo.UpdateRefAsync(ReceiveIncomingRef, theirs);

        var feed = new ProjectFeed(await _repo.DuoDirAsync());
        foreach (var note in notes) feed.Add("merge", note);
        foreach (var d in resolution?.Files ?? Array.Empty<Decision>())
            feed.Add("claude", $"{d.Path}: {DecisionRu(d.Kind)} — {d.Why}");
        var text = (resolution != null
                       ? $"Claude слил спорное ({Ru.Files(resolution.Files.Count)})" + (resolution.Summary.Length > 0 ? $": {resolution.Summary.TrimEnd('.')}." : ".")
                       : "Слито без споров.") +
                   $" Применено {Ru.Files(applied.Files.Count)}. Проверь и нажми «Отправить»." + UnityNote(applied);
        feed.Add("merge", text);
        return applied with { Message = text };
    }

    /// <summary>Task, Claude's question and the person's answer, a second attempt with the check's findings; then the commit.</summary>
    async Task<(string? Commit, MergeResolution? Result, OpResult? Failed)> ResolveWithAsync(MergeJob job, Func<string, Task<string?>> ask, CancellationToken ct)
    {
        var feed = new ProjectFeed(await _repo.DuoDirAsync());
        feed.Add("claude", $"Claude сливает {Ru.Files(job.Items.Count)}: {string.Join(", ", job.Items.Take(5).Select(i => i.Path))}");
        var session = Guid.NewGuid().ToString();
        var message = job.TaskText;
        var resume = false;
        int corrections = 0, questions = 0;
        while (true)
        {
            job.ClearResult();
            _opt.Progress?.Invoke("Claude сливает…");
            var outcome = await _opt.Resolver!.ResolveAsync(job, message, session, resume, line =>
            {
                feed.Add("claude", line);
                _opt.Progress?.Invoke(line);
            }, ct);
            resume = true;
            if (!outcome.Ok)
                return (null, null, OpResult.Blocked($"{outcome.Error}. Ничего не применено, работа обоих цела. Нажми «Слить с Claude» ещё раз, когда будет можно."));

            var result = job.ReadResult();
            if (result?.Question is { } question && questions++ < 5)
            {
                feed.Add("claude", "Claude спрашивает: " + question);
                var answer = await ask(question);
                if (string.IsNullOrWhiteSpace(answer))
                    return (null, null, OpResult.Blocked("Слияние отложено: Claude ждёт ответа на вопрос. Ничего не применено, нажми «Слить с Claude», когда будешь готов ответить."));
                feed.Add("me", "Ответ: " + answer);
                message = answer;
                continue;
            }
            var problems = result == null ? new[] { "result.json нет или он не разбирается как JSON" } : job.Validate(result);
            if (problems.Count == 0)
            {
                try { return (await job.CommitAsync(_repo, result!, $"Слияние: {_opt.MeName} + {_opt.FriendName}\n\nСпорные файлы ({job.Items.Count}) решил Claude.", ct), result, null); }
                catch (InvalidDataException e) { problems = new[] { e.Message }; }
            }
            feed.Add("claude", "Проверка программы не прошла: " + string.Join("; ", problems.Take(5)));
            if (corrections++ >= 2)
                return (null, null, OpResult.Blocked("Claude не прошёл проверки программы: " + string.Join("; ", problems.Take(3)) + ". Ничего не применено."));
            message = "Проверка программы не прошла, исправь и запиши result.json заново:\n- " + string.Join("\n- ", problems);
        }
    }

    static string DecisionRu(string kind) => kind switch
    {
        "combine" => "объединил обе правки",
        "choice-mine" => "взял мою версию",
        "choice-theirs" => "взял версию друга",
        "keep" => "оставил файл",
        "delete" => "удалил файл",
        _ => kind,
    };

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
