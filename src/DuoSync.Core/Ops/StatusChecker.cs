using DuoSync.Core.Git;

namespace DuoSync.Core.Ops;

public enum SyncState { InSync, Unsent, Incoming, Both, Offline, AuthFailed, Rewritten, Busy, NotPrepared }

/// <summary>What the tray icon, the window and the notification show (§3.4, §3.8).</summary>
public sealed record ProjectStatus(SyncState State)
{
    public string? RemoteSha { get; init; }
    public IReadOnlyList<string> IncomingSubjects { get; init; } = Array.Empty<string>();
    public int IncomingCommits { get; init; }
    public IReadOnlyList<string> IncomingFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UnsentFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Overlap { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
}

/// <summary>
/// Background check, read-only for the working folder: ls-remote, and a fetch only when the remote moved.
/// Skips the tick when an operation holds the repository.
/// </summary>
public sealed class StatusChecker
{
    static readonly string[] UnityRoots = { "Assets/", "Packages/", "ProjectSettings/" };
    readonly Repo _repo;

    public StatusChecker(Repo repo) => _repo = repo;

    public async Task<ProjectStatus> CheckAsync(SemaphoreSlim repoLock, CancellationToken ct = default)
    {
        // Without the DuoSync .gitattributes, autocrlf checkouts show every text file as changed: do not count anything.
        if (!Setup.ProjectSetup.IsPrepared(_repo.Root)) return new ProjectStatus(SyncState.NotPrepared);
        var (ls, remoteSha) = await _repo.LsRemoteBranchAsync(background: true, ct);
        if (!ls.Ok)
            return GitErrors.Classify(ls) == GitErrorKind.Auth
                ? new ProjectStatus(SyncState.AuthFailed) { Error = ls.StdErr }
                : new ProjectStatus(SyncState.Offline) { Error = ls.StdErr };

        if (!await repoLock.WaitAsync(0, ct)) return new ProjectStatus(SyncState.Busy);
        try
        {
            var known = await _repo.RevParseAsync(_repo.RemoteBranchRef);
            if (remoteSha != null && remoteSha != known)
            {
                var fetch = await _repo.FetchAsync(background: true, ct);
                if (!fetch.Ok) return new ProjectStatus(SyncState.Offline) { Error = fetch.StdErr };
            }
            return await ComputeAsync(remoteSha);
        }
        finally { repoLock.Release(); }
    }

    async Task<ProjectStatus> ComputeAsync(string? remoteSha)
    {
        var head = await _repo.HeadAsync();
        var theirs = await _repo.RevParseAsync(_repo.RemoteBranchRef);

        var last = await _repo.ReadRefAsync(SyncEngine.LastRemoteRef);
        if (theirs != null && last != null && last != theirs && !await _repo.IsAncestorAsync(last, theirs))
            return new ProjectStatus(SyncState.Rewritten) { RemoteSha = remoteSha };

        var unsent = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in await _repo.StatusAsync(background: true))
        {
            if (e.Kind is Git.StatusKind.Ignored) continue;
            if (e.Kind == Git.StatusKind.Untracked && !UnityRoots.Any(r => e.Path.StartsWith(r, StringComparison.Ordinal))) continue;
            unsent.Add(e.Path);
        }

        IReadOnlyList<string> incomingFiles = Array.Empty<string>();
        IReadOnlyList<string> subjects = Array.Empty<string>();
        int incomingCommits = 0;
        if (head != null && theirs != null)
        {
            var mergeBase = await _repo.MergeBaseAsync(head, theirs);
            if (mergeBase != null)
            {
                foreach (var p in await _repo.DiffNamesAsync(mergeBase, head)) unsent.Add(p); // committed, not sent
                if (mergeBase != theirs)
                {
                    incomingFiles = await _repo.DiffNamesAsync(mergeBase, theirs);
                    incomingCommits = int.Parse(await _repo.Git.OutAsync("rev-list", "--count", "--no-merges", $"{head}..{theirs}"));
                    subjects = await _repo.CommitSubjectsAsync($"{head}..{theirs}", 5);
                }
            }
        }

        var overlap = incomingFiles.Where(unsent.Contains).ToList();
        var state = (incomingFiles.Count > 0 || incomingCommits > 0, unsent.Count > 0) switch
        {
            (true, true) => SyncState.Both,
            (true, false) => SyncState.Incoming,
            (false, true) => SyncState.Unsent,
            _ => SyncState.InSync,
        };
        return new ProjectStatus(state)
        {
            RemoteSha = remoteSha,
            IncomingCommits = incomingCommits,
            IncomingSubjects = subjects,
            IncomingFiles = incomingFiles,
            UnsentFiles = unsent.ToList(),
            Overlap = overlap,
        };
    }
}
