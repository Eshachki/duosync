using System.Text;
using DuoSync.Core.Git;

namespace DuoSync.Core.Ops;

public sealed record MergeOutcome(bool Clean, string? Commit, MergeTreeResult Tree);

/// <summary>
/// Merges outside the working folder with <c>git merge-tree --write-tree</c> (§5.2, decisions B3). Stage 1 accepts only
/// clean merges; everything else is left for the Claude merge stage and nothing is applied.
/// </summary>
public sealed class Merger
{
    readonly Repo _repo;

    public Merger(Repo repo) => _repo = repo;

    public async Task<MergeOutcome> MergeAsync(string ours, string theirs, string message, CancellationToken ct = default)
    {
        var r = await _repo.Git.RunAsync(
            new[] { "--attr-source=" + ours, "merge-tree", "--write-tree", "-z", "--messages", ours, theirs },
            new GitRunOptions { Timeout = TimeSpan.FromMinutes(5) }, ct);
        var tree = GitParse.ParseMergeTree(r);
        if (!tree.Clean) return new MergeOutcome(false, null, tree);

        var body = $"{message.Trim()}\n\nDuoSync: merge\n";
        var commit = (await _repo.Git.RunCheckedAsync(
            new[] { "commit-tree", tree.TreeOid, "-p", ours, "-p", theirs, "-F", "-" },
            new GitRunOptions { StdIn = Encoding.UTF8.GetBytes(body) })).StdOutTrimmed;
        return new MergeOutcome(true, commit, tree);
    }
}
