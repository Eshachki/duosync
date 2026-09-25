namespace DuoSync.Core.Git;

/// <summary>Common queries over one working copy. Everything goes through <see cref="GitRunner"/>.</summary>
public sealed class Repo
{
    public GitRunner Git { get; }
    public string Root => Git.RepoDir;
    public string Branch { get; }
    public string Remote { get; }

    string? _gitDir;

    public Repo(GitRunner git, string branch = "main", string remote = "origin")
    {
        Git = git;
        Branch = branch;
        Remote = remote;
    }

    public string RemoteBranchRef => $"refs/remotes/{Remote}/{Branch}";
    public string LocalBranchRef => $"refs/heads/{Branch}";

    public async Task<string> GitDirAsync()
    {
        if (_gitDir != null) return _gitDir;
        var dir = await Git.OutAsync("rev-parse", "--absolute-git-dir");
        _gitDir = dir.Replace('/', Path.DirectorySeparatorChar);
        return _gitDir;
    }

    /// <summary>&lt;git-dir&gt;/duosync — journal, feed, temporary indexes.</summary>
    public async Task<string> DuoDirAsync()
    {
        var d = Path.Combine(await GitDirAsync(), "duosync");
        Directory.CreateDirectory(d);
        return d;
    }

    public async Task<string?> RevParseAsync(string rev)
    {
        var r = await Git.RunAsync("rev-parse", "--verify", "-q", rev + "^{commit}");
        return r.Ok ? r.StdOutTrimmed : null;
    }

    public Task<string?> HeadAsync() => RevParseAsync("HEAD");

    public async Task<string?> SymbolicHeadAsync()
    {
        var r = await Git.RunAsync("symbolic-ref", "-q", "HEAD");
        return r.Ok ? r.StdOutTrimmed : null;
    }

    public async Task<bool> IsAncestorAsync(string ancestor, string descendant)
    {
        var r = await Git.RunAsync("merge-base", "--is-ancestor", ancestor, descendant);
        return r.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new GitException(r),
        };
    }

    public async Task<string?> MergeBaseAsync(string a, string b)
    {
        var r = await Git.RunAsync("merge-base", a, b);
        return r.Ok ? r.StdOutTrimmed : null;
    }

    public async Task<List<string>> DiffNamesAsync(string from, string to, string? filter = null)
    {
        var args = new List<string> { "diff", "--name-only", "-z", "--no-renames" };
        if (filter != null) args.Add("--diff-filter=" + filter);
        args.Add(from);
        args.Add(to);
        var r = await Git.RunCheckedAsync(args);
        return GitParse.SplitZ(r.StdOutBytes);
    }

    public async Task<List<StatusEntry>> StatusAsync(bool background = false)
    {
        var r = await Git.RunCheckedAsync(new[] { "status", "--porcelain=v2", "-z", "--untracked-files=all" },
            new GitRunOptions { NoOptionalLocks = background });
        return GitParse.ParseStatusV2(r.StdOutBytes);
    }

    /// <summary>True when tracked files differ from HEAD (index or working tree).</summary>
    public async Task<bool> HasTrackedChangesAsync()
    {
        var r = await Git.RunAsync("diff-index", "--quiet", "HEAD", "--");
        return r.ExitCode switch { 0 => false, 1 => true, _ => throw new GitException(r) };
    }

    public async Task<GitResult> FetchAsync(bool background = false, CancellationToken ct = default)
        => await Git.RunAsync(new[] { "fetch", "--no-tags", Remote, $"+{LocalBranchRef}:{RemoteBranchRef}" },
            new GitRunOptions { Timeout = TimeSpan.FromMinutes(10), NonInteractive = background }, ct);

    public async Task<(GitResult Result, string? Sha)> LsRemoteBranchAsync(bool background = true, CancellationToken ct = default)
    {
        var r = await Git.RunAsync(new[] { "ls-remote", Remote, LocalBranchRef },
            new GitRunOptions { Timeout = TimeSpan.FromSeconds(20), NonInteractive = background }, ct);
        if (!r.Ok) return (r, null);
        GitParse.ParseLsRemote(r.StdOut).TryGetValue(LocalBranchRef, out var sha);
        return (r, sha);
    }

    public Task<GitResult> UpdateRefAsync(string refName, string sha) => Git.RunCheckedAsync("update-ref", refName, sha);

    public Task<GitResult> DeleteRefAsync(string refName) => Git.RunAsync("update-ref", "-d", refName);

    public async Task<string?> ReadRefAsync(string refName)
    {
        var r = await Git.RunAsync("rev-parse", "--verify", "-q", refName);
        return r.Ok ? r.StdOutTrimmed : null;
    }

    /// <summary>Blob id of path in a commit, or null when the path does not exist there.</summary>
    public async Task<string?> BlobAsync(string commit, string path)
    {
        var r = await Git.RunAsync("rev-parse", "--verify", "-q", $"{commit}:{path}");
        return r.Ok ? r.StdOutTrimmed : null;
    }

    /// <summary>Blob id the working file would get (clean filters such as LFS applied).</summary>
    public async Task<string?> HashWorkFileAsync(string path)
    {
        var full = Path.Combine(Root, path);
        if (!File.Exists(full)) return null;
        var r = await Git.RunAsync("hash-object", "--path=" + path, "--", path);
        return r.Ok ? r.StdOutTrimmed : null;
    }

    public async Task<byte[]> CatBlobAsync(string commit, string path)
        => (await Git.RunCheckedAsync("cat-file", "blob", $"{commit}:{path}")).StdOutBytes;

    /// <summary>Subjects of the person's own commits in a range; service snapshots ("DuoSync: snapshot") are skipped.</summary>
    public async Task<IReadOnlyList<string>> CommitSubjectsAsync(string range, int max = 20)
    {
        var r = await Git.RunAsync("log", "--no-merges", $"-n{max * 3}", "--format=%s%x1f%(trailers:key=DuoSync,valueonly,separator=%x2C)%x1e", range);
        if (!r.Ok) return new List<string>();
        return r.StdOut.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Select(rec => rec.Trim('\r', '\n').Split('\x1f'))
            .Where(p => p.Length > 0 && p[0].Length > 0 && !(p.Length > 1 && p[1].Trim() == "snapshot"))
            .Select(p => p[0])
            .Take(max)
            .ToList();
    }
}
