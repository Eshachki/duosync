using System.Text;
using System.Text.RegularExpressions;
using DuoSync.Core.Git;

namespace DuoSync.Core.Ops;

/// <summary>Snapshot = ordinary commit of the person's work; SaveRef = backup of the whole folder under refs/duosync/save/*.</summary>
public sealed class Snapshots
{
    public const int KeepSaveRefs = 50;
    static readonly string[] UnityRoots = { "Assets", "Packages", "ProjectSettings" };

    readonly Repo _repo;

    public Snapshots(Repo repo) => _repo = repo;

    /// <summary>
    /// Commits everything that belongs to the Unity project plus changes to already tracked files.
    /// New files outside Assets/Packages/ProjectSettings are added only when listed in <paramref name="extraPaths"/>.
    /// Returns the new HEAD (or the old one when there was nothing to commit).
    /// </summary>
    public async Task<string?> SnapshotAsync(string message, string trailer, IReadOnlyList<string>? extraPaths = null)
    {
        var git = _repo.Git;
        var roots = UnityRoots.Where(r => Directory.Exists(Path.Combine(_repo.Root, r))).ToList();
        if (roots.Count > 0)
            await git.RunCheckedAsync(new[] { "add", "-A", "--" }.Concat(roots));
        await git.RunCheckedAsync("add", "-u");
        if (extraPaths is { Count: > 0 })
            await git.RunCheckedAsync(new[] { "add", "--" }.Concat(extraPaths));

        var head = await _repo.HeadAsync();
        if (head != null)
        {
            var quiet = await git.RunAsync("diff", "--cached", "--quiet");
            if (quiet.ExitCode == 0) return head;
            if (quiet.ExitCode != 1) throw new GitException(quiet);
        }
        else
        {
            // Unborn branch: commit only when something is staged.
            var staged = await git.RunCheckedAsync("ls-files", "-z");
            if (staged.StdOutBytes.Length == 0) return null;
        }

        var body = $"{message.Trim()}\n\nDuoSync: {trailer}\n";
        await git.RunCheckedAsync(new[] { "commit", "-q", "--no-verify", "-F", "-" },
            new GitRunOptions { StdIn = Encoding.UTF8.GetBytes(body) });
        return await _repo.HeadAsync();
    }

    /// <summary>
    /// Backup of the whole folder including untracked (not ignored) files, without touching HEAD, the index or the files.
    /// </summary>
    public async Task<string?> SaveRefAsync(string label)
    {
        var git = _repo.Git;
        var duoDir = await _repo.DuoDirAsync();
        var gitDir = await _repo.GitDirAsync();
        var idx = Path.Combine(duoDir, "save.idx");
        var realIndex = Path.Combine(gitDir, "index");
        // Copy the real index so stat info is reused and big LFS files are not re-hashed.
        if (File.Exists(realIndex)) File.Copy(realIndex, idx, overwrite: true);
        else if (File.Exists(idx)) File.Delete(idx);

        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = idx };
        await git.RunCheckedAsync(new[] { "add", "-A" }, new GitRunOptions { Env = env, Timeout = TimeSpan.FromMinutes(10) });
        var tree = (await git.RunCheckedAsync(new[] { "write-tree" }, new GitRunOptions { Env = env })).StdOutTrimmed;

        var head = await _repo.HeadAsync();
        var args = new List<string> { "commit-tree", tree };
        if (head != null) { args.Add("-p"); args.Add(head); }
        args.Add("-m");
        args.Add("save: " + label);
        var commit = (await git.RunCheckedAsync(args)).StdOutTrimmed;

        var safe = Regex.Replace(label.ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
        var refName = $"refs/duosync/save/{DateTime.Now:yyyyMMdd-HHmmss-fff}-{safe}";
        await _repo.UpdateRefAsync(refName, commit);
        await PruneSaveRefsAsync();
        return refName;
    }

    async Task PruneSaveRefsAsync()
    {
        var r = await _repo.Git.RunAsync("for-each-ref", "--format=%(refname)", "--sort=-refname", "refs/duosync/save/");
        if (!r.Ok) return;
        var refs = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\r')).ToList();
        foreach (var old in refs.Skip(KeepSaveRefs))
            await _repo.DeleteRefAsync(old);
    }
}
