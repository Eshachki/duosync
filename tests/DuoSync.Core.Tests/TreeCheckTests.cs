using System.Text;
using DuoSync.Core.Git;
using DuoSync.Core.Merge;
using static DuoSync.Core.Tests.Harness.Sandbox;

namespace DuoSync.Core.Tests;

/// <summary>L5: only what the merge brought in blocks; a problem that came from a parent passes as it was.</summary>
public class TreeCheckTests
{
    /// <summary>A commit on top of HEAD with some files replaced (null: removed), built without touching the folder.</summary>
    static async Task<string> CommitAsync(Repo repo, string parent, params (string Path, string? Content)[] files)
    {
        var index = Path.Combine(await repo.DuoDirAsync(), "test.idx");
        if (File.Exists(index)) File.Delete(index);
        var env = new GitRunOptions { Env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index } };
        await repo.Git.RunCheckedAsync(new[] { "read-tree", parent }, env);
        foreach (var (path, content) in files)
        {
            if (content == null) { await repo.Git.RunCheckedAsync(new[] { "update-index", "--force-remove", "--", path }, env); continue; }
            var oid = (await repo.Git.RunCheckedAsync(new[] { "hash-object", "-w", "--stdin" }, new GitRunOptions { StdIn = Encoding.UTF8.GetBytes(content) })).StdOutTrimmed;
            await repo.Git.RunCheckedAsync(new[] { "update-index", "--add", "--cacheinfo", "100644", oid, path }, env);
        }
        var tree = (await repo.Git.RunCheckedAsync(new[] { "write-tree" }, env)).StdOutTrimmed;
        return (await repo.Git.RunCheckedAsync(new[] { "commit-tree", tree, "-p", parent, "-F", "-" }, new GitRunOptions { StdIn = "t\n"u8.ToArray() })).StdOutTrimmed;
    }

    [Fact]
    public async Task Problems_the_merge_brought_in_block_and_inherited_ones_do_not()
    {
        await using var sb = await CreateAsync();
        var repo = sb.Owner.Repo;
        var head = (await repo.HeadAsync())!;
        var mine = await CommitAsync(repo, head, ("Assets/Art/Sky.png", "png"), ("Assets/Art/Sky.png.meta", "fileFormatVersion: 2\nguid: 44444444444444444444444444444444\n"));
        var theirs = await CommitAsync(repo, head, ("Assets/Docs/readme.asset", "text"));   // no .meta: came like this from the friend

        // The result lost Sky.png.meta (brought in by the merge) and kept readme.asset without .meta (inherited).
        var broken = await CommitAsync(repo, mine, ("Assets/Art/Sky.png.meta", null), ("Assets/Docs/readme.asset", "text"));
        var problems = await TreeCheck.CheckAsync(repo, broken, mine, theirs);

        Assert.Contains(problems, p => p.StartsWith("Assets/Art/Sky.png: ассет без .meta"));
        Assert.DoesNotContain(problems, p => p.StartsWith("Assets/Docs/readme.asset"));
    }

    [Fact]
    public async Task Markers_duplicate_guids_and_broken_scenes_are_found()
    {
        await using var sb = await CreateAsync();
        var repo = sb.Owner.Repo;
        var head = (await repo.HeadAsync())!;
        var result = await CommitAsync(repo, head,
            ("Assets/Scripts/Player.cs", "class Player\n{\n<<<<<<< MINE\n    int a;\n=======\n    int b;\n>>>>>>> THEIRS\n}\n"),
            ("Assets/Scripts/Copy.cs", "class Copy {}\n"),
            ("Assets/Scripts/Copy.cs.meta", "fileFormatVersion: 2\nguid: 11111111111111111111111111111111\n"),   // Player.cs.meta has this GUID
            ("Assets/Scenes/Main.unity", "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1 &100\nGameObject:\n--- !u!1 &100\nGameObject:\n"));

        var problems = await TreeCheck.CheckAsync(repo, result, head, head);

        Assert.Contains(problems, p => p.StartsWith("Assets/Scripts/Player.cs: остались маркеры"));
        Assert.Contains(problems, p => p.StartsWith("Assets/Scripts/Copy.cs.meta: GUID"));
        Assert.Contains(problems, p => p.StartsWith("Assets/Scenes/Main.unity: якорь &100"));
    }

    [Fact]
    public async Task A_clean_result_has_no_problems()
    {
        await using var sb = await CreateAsync();
        var repo = sb.Owner.Repo;
        var head = (await repo.HeadAsync())!;
        var mine = await CommitAsync(repo, head, ("Assets/Scripts/Coin.cs", "class Coin {}\n"), ("Assets/Scripts/Coin.cs.meta", "fileFormatVersion: 2\nguid: 55555555555555555555555555555555\n"));
        Assert.Empty(await TreeCheck.CheckAsync(repo, mine, head, head));
    }
}
