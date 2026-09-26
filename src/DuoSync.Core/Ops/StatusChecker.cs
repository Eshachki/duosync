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
    /// <summary>At the integrator: the friend's merge requests not merged yet (§3.13).</summary>
    public IReadOnlyList<MergeRequestInfo> Requests { get; init; } = Array.Empty<MergeRequestInfo>();
    /// <summary>At the friend: own request waiting since then (null: none waiting).</summary>
    public DateTimeOffset? RequestWaitingSince { get; init; }
    /// <summary>At the friend: the integrator merged the request and it is in main.</summary>
    public bool RequestMerged { get; init; }
}

/// <summary>
/// Background check, read-only for the working folder: ls-remote, and a fetch only when the remote moved.
/// Skips the tick when an operation holds the repository.
/// </summary>
public sealed class StatusChecker
{
    static readonly string[] UnityRoots = { "Assets/", "Packages/", "ProjectSettings/" };
    readonly Repo _repo;
    readonly bool _integrator;

    /// <param name="integrator">This computer merges: look for the friend's merge requests too.</param>
    public StatusChecker(Repo repo, bool integrator = false)
    {
        _repo = repo;
        _integrator = integrator;
    }

    public async Task<ProjectStatus> CheckAsync(SemaphoreSlim repoLock, CancellationToken ct = default)
    {
        // Without the DuoSync .gitattributes, autocrlf checkouts show every text file as changed: do not count anything.
        // An empty clone is fine: its first «Получить» brings the preparation.
        if (!Setup.ProjectSetup.IsPrepared(_repo.Root) && await _repo.HeadAsync() != null) return new ProjectStatus(SyncState.NotPrepared);
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
            var status = await ComputeAsync(remoteSha);
            return _integrator ? status with { Requests = await RequestsAsync(ct) } : status;
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
        // Own merge request (the friend's side): waiting, or already in main.
        DateTimeOffset? waiting = null;
        var merged = false;
        if (await _repo.ReadRefAsync(SyncEngine.RequestRef) is { } request)
        {
            if (theirs != null && await _repo.IsAncestorAsync(request, theirs)) merged = true;
            else waiting = await CommitTimeAsync(request);
        }

        return new ProjectStatus(state)
        {
            RemoteSha = remoteSha,
            IncomingCommits = incomingCommits,
            IncomingSubjects = subjects,
            IncomingFiles = incomingFiles,
            UnsentFiles = unsent.ToList(),
            Overlap = overlap,
            RequestWaitingSince = waiting,
            RequestMerged = merged,
        };
    }

    async Task<DateTimeOffset?> CommitTimeAsync(string sha)
    {
        var r = await _repo.Git.RunAsync("log", "-1", "--format=%cI", sha);
        return r.Ok && DateTimeOffset.TryParse(r.StdOutTrimmed, out var at) ? at : null;
    }

    /// <summary>
    /// The integrator's side: the friend's requests (<c>refs/heads/duosync/merge/*</c>) that are neither in main
    /// nor merged here yet. Read only; new request commits are fetched into refs/remotes.
    /// </summary>
    async Task<IReadOnlyList<MergeRequestInfo>> RequestsAsync(CancellationToken ct)
    {
        var ls = await _repo.Git.RunAsync(new[] { "ls-remote", _repo.Remote, "refs/heads/duosync/merge/*" },
            new GitRunOptions { Timeout = TimeSpan.FromSeconds(20), NonInteractive = true }, ct);
        if (!ls.Ok) return Array.Empty<MergeRequestInfo>();
        var main = await _repo.RevParseAsync(_repo.RemoteBranchRef);
        var head = await _repo.HeadAsync();
        var list = new List<MergeRequestInfo>();
        foreach (var (name, sha) in GitParse.ParseLsRemote(ls.StdOut))
        {
            var local = $"refs/remotes/{_repo.Remote}/{name["refs/heads/".Length..]}";
            if (await _repo.ReadRefAsync(local) != sha)
            {
                var fetch = await _repo.Git.RunAsync(new[] { "fetch", "--no-tags", _repo.Remote, $"+{name}:{local}" },
                    new GitRunOptions { Timeout = TimeSpan.FromMinutes(10), NonInteractive = true }, ct);
                if (!fetch.Ok) continue;
            }
            if (main != null && await _repo.IsAncestorAsync(sha, main)) continue; // merged and sent
            if (head != null && await _repo.IsAncestorAsync(sha, head)) continue; // merged here, waits for «Отправить»
            var info = GitParse.SplitZ((await _repo.Git.RunCheckedAsync("log", "-1", "-z", "--format=%an%x00%s%x00%cI", sha)).StdOutBytes);
            var mergeBase = main != null ? await _repo.MergeBaseAsync(main, sha) : null;
            var files = mergeBase != null ? await _repo.DiffNamesAsync(mergeBase, sha) : new List<string>();
            list.Add(new MergeRequestInfo(name, sha, info.ElementAtOrDefault(0) ?? "", info.ElementAtOrDefault(1) ?? "", files,
                DateTimeOffset.TryParse(info.ElementAtOrDefault(2), out var at) ? at : DateTimeOffset.MinValue));
        }
        return list;
    }
}
