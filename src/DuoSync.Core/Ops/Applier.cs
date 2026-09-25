using System.Text;
using System.Text.RegularExpressions;
using DuoSync.Core.Git;
using DuoSync.Core.Unity;

namespace DuoSync.Core.Ops;

/// <summary>
/// Apply is the only path that rewrites the working folder (§3.6, decisions B1). It moves main from
/// <c>from</c> to <c>to</c> with <c>git checkout --no-overwrite-ignore -B</c>. If anything fails half way the
/// folder is returned to <c>from</c> right away; a crash is repaired on the next start from op.json.
/// </summary>
public sealed class Applier
{
    static readonly Regex GuidLine = new(@"^guid: ([0-9a-f]{32})\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    readonly Repo _repo;
    readonly Snapshots _snapshots;
    readonly IUnityBridge _unity;
    public TimeSpan LockProbeDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Tests switch the probe off to reproduce git failing half way through.</summary>
    public bool LockProbeEnabled { get; set; } = true;

    public Applier(Repo repo, Snapshots snapshots, IUnityBridge unity)
    {
        _repo = repo;
        _snapshots = snapshots;
        _unity = unity;
    }

    async Task<Journal> JournalAsync() => new(await _repo.DuoDirAsync());

    public async Task<OpResult> ApplyAsync(string from, string to, string label, CancellationToken ct = default)
    {
        var head = await _repo.HeadAsync();
        if (head != from) throw new InvalidOperationException($"Apply: HEAD {head} != from {from}");
        if (await _repo.HasTrackedChangesAsync()) throw new InvalidOperationException("Apply: tracked changes present; snapshot first");

        var paths = await _repo.DiffNamesAsync(from, to);
        if (paths.Count == 0 && from == to) return new OpResult(OpStatus.UpToDate, "Нечего применять.");

        var added = await _repo.DiffNamesAsync(from, to, "A");
        var occupied = added.Where(IsOccupied).ToList();
        if (occupied.Count > 0)
            return OpResult.Blocked(
                $"У тебя есть локальный неотправляемый файл «{occupied[0]}», а пришёл файл с тем же путём. Переименуй или убери свой файл и повтори.",
                string.Join("\n", occupied));

        var journal = await JournalAsync();
        journal.Write(new ApplyJournal("apply", label, from, to, Environment.ProcessId, DateTime.UtcNow, paths));

        if (_unity.IsOpen)
        {
            var park = await _unity.ParkAsync(paths, ct);
            if (!park.Ok)
            {
                journal.Delete();
                return OpResult.Blocked(park.Error ?? "Unity не дал закрыть сцены на время получения.");
            }
        }

        var locked = LockProbeEnabled ? await LockProbeAsync(paths) : null;
        if (locked != null)
        {
            await FinishUnityAsync(paths, ct);
            journal.Delete();
            return OpResult.Blocked($"Файл «{locked}» занят (Unity или антивирус). Повтори через минуту или закрой Unity и повтори.");
        }

        var r = await _repo.Git.RunAsync(new[] { "checkout", "--no-overwrite-ignore", "-B", _repo.Branch, to },
            new GitRunOptions { Timeout = TimeSpan.FromMinutes(30) }, ct);
        if (!r.Ok)
        {
            if (await _repo.HeadAsync() == from && !await IsPartiallyAppliedAsync(from, to, paths))
            {
                await FinishUnityAsync(paths, ct);
                journal.Delete();
                return OpResult.Blocked(GitErrors.Explain(r), r.StdErr);
            }
            var kept = await RevertPartialAsync(from, to, paths);
            await FinishUnityAsync(paths, ct);
            journal.Delete();
            return new OpResult(OpStatus.Failed,
                "Не получилось применить (" + GitErrors.Explain(r) + "). Всё возвращено как было." +
                (kept.Count > 0 ? $" Оставлены файлы, которые менялись во время операции: {string.Join(", ", kept)}." : ""))
            { Detail = r.StdErr };
        }

        // git can exit 0 after "unable to unlink old '<file>'" (file held by Unity or antivirus): HEAD moved,
        // the old content stayed and would look like the person's own edit. Verify every path.
        var mismatched = await MismatchedAsync(to, paths, await _repo.DiffNamesAsync(from, to, "D"));
        if (mismatched.Count > 0)
        {
            var kept = await RevertPartialAsync(from, to, paths);
            await FinishUnityAsync(paths, ct);
            journal.Delete();
            return new OpResult(OpStatus.Failed,
                $"Файл «{mismatched[0]}» был занят (Unity или антивирус), git не смог его заменить. Всё возвращено как было, повтори через минуту." +
                (kept.Count > 0 ? $" Оставлены: {string.Join(", ", kept)}." : ""))
            { Detail = r.StdErr, ConflictedPaths = mismatched };
        }

        var finish = await FinishUnityAsync(paths, ct);
        await MetaSentinelAsync(paths, to);
        await _repo.UpdateRefAsync("refs/duosync/last-head", to);
        journal.Delete();
        return new OpResult(OpStatus.Done, label)
        {
            Files = paths, Before = from, After = to, Detail = finish.Ok ? null : finish.Error, CompileErrors = finish.CompileErrors,
        };
    }

    async Task<BridgeResult> FinishUnityAsync(IReadOnlyList<string> paths, CancellationToken ct)
        => _unity.IsOpen ? await _unity.FinishAsync(paths, ct) : BridgeResult.Success;

    bool IsOccupied(string relPath)
    {
        var full = Path.Combine(_repo.Root, relPath);
        if (File.Exists(full) || Directory.Exists(full)) return true;
        // A file where a directory of the incoming path is needed.
        var parts = relPath.Split('/');
        var prefix = _repo.Root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            prefix = Path.Combine(prefix, parts[i]);
            if (File.Exists(prefix)) return true;
        }
        return false;
    }

    async Task<string?> LockProbeAsync(IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
        {
            var full = Path.Combine(_repo.Root, p);
            if (!File.Exists(full)) continue;
            bool ok = false;
            for (int attempt = 0; attempt < 3 && !ok; attempt++)
            {
                try
                {
                    using var fs = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    ok = true;
                }
                catch (IOException) { await Task.Delay(LockProbeDelay); }
                catch (UnauthorizedAccessException) { await Task.Delay(LockProbeDelay); }
            }
            if (!ok) return p;
        }
        return null;
    }

    /// <summary>
    /// Paths whose working file does not match <paramref name="commit"/>: content differs, a file of the commit is
    /// missing, or a path listed in <paramref name="absentInCommit"/> still exists on disk.
    /// </summary>
    public async Task<List<string>> MismatchedAsync(string commit, IReadOnlyList<string> paths, IReadOnlyCollection<string> absentInCommit)
    {
        var result = new List<string>();
        if (paths.Count == 0) return result;
        // git diff has no --pathspec-from-file; tracked changes are absent during Apply, so compare the whole tree.
        var r = await _repo.Git.RunAsync(new[] { "diff", "--name-only", "-z", "--no-renames", commit },
            new GitRunOptions { Timeout = TimeSpan.FromMinutes(10) });
        if (!r.Ok) throw new GitException(r);
        var wanted = new HashSet<string>(paths, StringComparer.Ordinal);
        result.AddRange(GitParse.SplitZ(r.StdOutBytes).Where(wanted.Contains));
        foreach (var p in absentInCommit)
            if (!result.Contains(p) && File.Exists(Path.Combine(_repo.Root, p))) result.Add(p);
        return result;
    }

    /// <summary>True when any path already differs from <paramref name="from"/> (git wrote something before failing).</summary>
    async Task<bool> IsPartiallyAppliedAsync(string from, string to, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return false;
        var added = await _repo.DiffNamesAsync(from, to, "A");
        return (await MismatchedAsync(from, paths, added)).Count > 0;
    }

    /// <summary>Returns the folder to <paramref name="from"/>; files that match neither side are kept and returned.</summary>
    public async Task<List<string>> RevertPartialAsync(string from, string to, IReadOnlyList<string> paths)
    {
        var git = _repo.Git;
        await _snapshots.SaveRefAsync("apply-failed");
        if (await _repo.HeadAsync() != from)
            await _repo.UpdateRefAsync(_repo.LocalBranchRef, from);
        await git.RunCheckedAsync("read-tree", from);

        var added = new HashSet<string>(await _repo.DiffNamesAsync(from, to, "A"), StringComparer.Ordinal);
        var inFrom = paths.Where(p => !added.Contains(p)).ToList();
        var notInFrom = paths.Where(added.Contains).ToList();

        // A still-locked file makes git print an error but the others get restored; the check below reports it.
        if (inFrom.Count > 0)
            await git.RunAsync(new[] { "checkout", from, "--pathspec-from-file=-", "--pathspec-file-nul" },
                new GitRunOptions { StdIn = Encoding.UTF8.GetBytes(string.Join('\0', inFrom)), Timeout = TimeSpan.FromMinutes(10) });

        var kept = new List<string>();
        foreach (var p in notInFrom)
        {
            var full = Path.Combine(_repo.Root, p);
            if (!File.Exists(full)) continue;
            if (await _repo.HashWorkFileAsync(p) == await _repo.BlobAsync(to, p))
            {
                try { File.Delete(full); } catch (IOException) { kept.Add(p); } catch (UnauthorizedAccessException) { kept.Add(p); }
            }
            else kept.Add(p);
        }
        foreach (var p in await MismatchedAsync(from, inFrom, Array.Empty<string>()))
            if (!kept.Contains(p)) kept.Add(p);
        return kept;
    }

    /// <summary>Unity may regenerate a .meta while git writes it (seen on a real project). Restore GUIDs from <paramref name="to"/>.</summary>
    public async Task<int> MetaSentinelAsync(IReadOnlyList<string> paths, string to)
    {
        int fixedCount = 0;
        foreach (var p in paths.Where(p => p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
        {
            var full = Path.Combine(_repo.Root, p);
            if (!File.Exists(full) || await _repo.BlobAsync(to, p) == null) continue;
            var onDisk = GuidLine.Match(await File.ReadAllTextAsync(full));
            var expected = GuidLine.Match(Encoding.UTF8.GetString(await _repo.CatBlobAsync(to, p)));
            if (onDisk.Success && expected.Success && onDisk.Groups[1].Value != expected.Groups[1].Value)
            {
                await _repo.Git.RunCheckedAsync("checkout", to, "--", p);
                fixedCount++;
            }
        }
        return fixedCount;
    }

    /// <summary>Called on start and before any operation: finishes or undoes an Apply that was interrupted.</summary>
    public async Task<OpResult?> RecoverAsync(CancellationToken ct = default)
    {
        var journal = await JournalAsync();
        var j = journal.Read(out var corrupt);
        if (corrupt)
        {
            journal.Delete();
            return OpResult.Blocked("Журнал прерванной операции повреждён. Проверь состояние и нажми «Починить».");
        }
        if (j == null) return null;

        var head = await _repo.HeadAsync();
        if (head == j.To)
        {
            await FinishUnityAsync(j.Paths, ct);
            await MetaSentinelAsync(j.Paths, j.To);
            journal.Delete();
            return new OpResult(OpStatus.Done, $"Операция «{j.Label}» была прервана, но успела завершиться.") { Files = j.Paths };
        }
        if (head == j.From)
        {
            if (!await IsPartiallyAppliedAsync(j.From, j.To, j.Paths))
            {
                await FinishUnityAsync(j.Paths, ct);
                journal.Delete();
                return new OpResult(OpStatus.Blocked, $"Операция «{j.Label}» была прервана, ничего не изменено. Можно повторить.");
            }
            var kept = await RevertPartialAsync(j.From, j.To, j.Paths);
            await FinishUnityAsync(j.Paths, ct);
            journal.Delete();
            return new OpResult(OpStatus.Failed, $"Операция «{j.Label}» была прервана. Всё возвращено как было." +
                (kept.Count > 0 ? $" Оставлены: {string.Join(", ", kept)}." : ""));
        }
        return OpResult.Blocked("После прерванной операции проект в неожиданном состоянии. Нажми «Починить».",
            $"op.json from={j.From} to={j.To}, HEAD={head}");
    }
}
