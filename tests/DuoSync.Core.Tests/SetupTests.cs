using DuoSync.Core.Git;
using DuoSync.Core.Ops;
using DuoSync.Core.Setup;

namespace DuoSync.Core.Tests;

public class SetupTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "duosync-tests", "setup-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>A project that was cloned with core.autocrlf=true and has no DuoSync attributes yet.</summary>
    async Task<Repo> UnpreparedCloneAsync(string gitattributes = "", string gitignore = "")
    {
        Directory.CreateDirectory(_root);
        var raw = new GitRunner(_root, "Seed", "s@example.invalid");
        await raw.RunCheckedAsync("init", "--bare", "-b", "main", Path.Combine(_root, "remote.git"));
        var seed = Path.Combine(_root, "seed");
        await raw.RunCheckedAsync("clone", "-q", Path.Combine(_root, "remote.git"), seed);
        var s = new GitRunner(seed, "Seed", "s@example.invalid");
        await s.RunCheckedAsync("checkout", "-q", "-B", "main");
        Directory.CreateDirectory(Path.Combine(seed, "Assets"));
        File.WriteAllText(Path.Combine(seed, "Assets", "A.cs"), "class A\n{\n}\n");
        File.WriteAllText(Path.Combine(seed, "Assets", "B.unity"), "%YAML 1.1\n--- !u!1 &1\n");
        if (gitattributes.Length > 0) File.WriteAllText(Path.Combine(seed, ".gitattributes"), gitattributes);
        if (gitignore.Length > 0) File.WriteAllText(Path.Combine(seed, ".gitignore"), gitignore);
        await s.RunCheckedAsync("add", "-A");
        await s.RunCheckedAsync("commit", "-q", "-m", "seed");
        await s.RunCheckedAsync("push", "-q", "origin", "main");

        var work = Path.Combine(_root, "work");
        // Plain git with the user's typical system setting: text files get CRLF in the working tree.
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git",
            new[] { "-c", "core.autocrlf=true", "clone", "-q", Path.Combine(_root, "remote.git"), work }) { CreateNoWindow = true })!;
        await p.WaitForExitAsync();
        return new Repo(new GitRunner(work, "Аня", "owner@example.invalid"));
    }

    [Fact]
    public async Task Library_and_builds_already_in_git_leave_the_index_but_stay_on_disk()
    {
        var repo = await UnpreparedCloneAsync();
        foreach (var rel in new[] { "Library/ArtifactDB", "Library/sub/cache.bin", "Temp/x.tmp", "Builds/game.exe", "Game.sln", "Assets/Art/.DS_Store", "Assets/Plugins/Native.dll", "Assets/Plugins/Native.pdb" })
        {
            var full = Path.Combine(repo.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }
        await repo.Git.RunCheckedAsync("add", "-f", "-A");
        await repo.Git.RunCheckedAsync("commit", "-q", "-m", "junk by mistake");

        var setup = new ProjectSetup(repo, "Аня", "Боря");
        var plan = await setup.PlanAsync();
        Assert.Contains(plan.Changes, c => c.Path.Contains("Library/") && c.Path.Contains("Temp/") && c.Description.Contains("убрать из git 6"));

        var result = await setup.ApplyAsync();
        Assert.Equal(OpStatus.Done, result.Status);
        Assert.Contains("из git убрано 6", result.Message);
        var tracked = await repo.Git.OutAsync("ls-files");
        Assert.DoesNotContain("Library/", tracked);
        Assert.DoesNotContain("Temp/", tracked);
        Assert.DoesNotContain("Builds/", tracked);
        Assert.DoesNotContain("Game.sln", tracked);
        Assert.DoesNotContain(".DS_Store", tracked);
        Assert.Contains("Assets/Plugins/Native.dll", tracked); // plugins inside Assets are legitimate
        Assert.Contains("Assets/Plugins/Native.pdb", tracked);
        Assert.True(File.Exists(Path.Combine(repo.Root, "Library", "ArtifactDB")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "Builds", "game.exe")));
        Assert.False(await repo.HasTrackedChangesAsync());
    }

    [Fact]
    public async Task Crlf_checkout_shows_false_changes_until_the_project_is_prepared()
    {
        var repo = await UnpreparedCloneAsync();
        Assert.Contains("\r\n", File.ReadAllText(Path.Combine(repo.Root, "Assets", "A.cs")));
        Assert.True(await repo.HasTrackedChangesAsync()); // the false "changes" that must never be committed

        var setup = new ProjectSetup(repo, "Аня", "Боря");
        var plan = await setup.PlanAsync();
        Assert.Contains(plan.Changes, c => c.Path == ".gitattributes");
        var r = await setup.ApplyAsync();

        Assert.Equal(OpStatus.Done, r.Status);
        Assert.True(ProjectSetup.IsPrepared(repo.Root));
        Assert.False(await repo.HasTrackedChangesAsync());
        Assert.Contains("DuoSync: подготовка проекта", await repo.Git.OutAsync("log", "-1", "--format=%s"));
        // Only the setup files were committed: no CRLF rewrite of A.cs.
        var changed = await repo.DiffNamesAsync("HEAD~1", "HEAD");
        Assert.DoesNotContain("Assets/A.cs", changed);
        Assert.Equal(OpStatus.UpToDate, (await setup.ApplyAsync()).Status);
    }

    [Fact]
    public async Task Project_without_gitignore_gets_the_full_template_with_the_peoples_rules()
    {
        var repo = await UnpreparedCloneAsync();

        await new ProjectSetup(repo, "Аня", "Боря").ApplyAsync();

        var text = File.ReadAllText(Path.Combine(repo.Root, ".gitignore")).Replace("\r\n", "\n");
        Assert.StartsWith("# Свои правила", text);
        Assert.Contains(Templates.BlockStart, text);
        foreach (var ignored in new[] { "Assets/IGNORE_FOLDER/Pack/Rock.fbx", "Assets/IGNORE_FOLDER.meta", "TRASH/old.png", "web/index.html",
                                        "Game.apk", "Library/x.asset", "Assets/_Local/Test.cs",
                                        "Game.sln", "Assets/Art/.DS_Store", "Assets/Art/Thumbs.db", "Assets/Models/Door.blend1", ".vscode/settings.json",
                                        "Game_BurstDebugInformation_DoNotShip/lib.pdb", "Temp/x", "UserSettings/Layouts.dwlt" })
            Assert.True(await IgnoredAsync(repo, ignored), ignored);
        foreach (var kept in new[] { "Assets/Plugins/DOTween.dll", "Assets/Plugins/DOTween.dll.mdb", "Assets/Plugins/DOTween.dll.meta",
                                     "Assets/Plugins/DOTween.pdb", "Assets/Models/Door.obj", "Assets/Scripts/A.cs.meta", ".vsconfig" })
            Assert.False(await IgnoredAsync(repo, kept), kept);
    }

    [Fact]
    public async Task Project_prepared_by_an_older_version_is_offered_the_new_rules()
    {
        var old = Templates.BlockStart + "\n/[Ll]ibrary/\n" + Templates.BlockEnd + "\n";
        var repo = await UnpreparedCloneAsync(gitignore: "/MyStuff/\n\n" + old);
        Assert.True(ProjectSetup.IgnoreRulesOutdated(repo.Root));

        await new ProjectSetup(repo, "Аня", "Боря").ApplyAsync();

        Assert.False(ProjectSetup.IgnoreRulesOutdated(repo.Root));
        Assert.StartsWith("/MyStuff/\n", File.ReadAllText(Path.Combine(repo.Root, ".gitignore")).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Own_gitignore_is_kept_and_gets_the_block()
    {
        var repo = await UnpreparedCloneAsync(gitignore: "/MyStuff/\n");

        await new ProjectSetup(repo, "Аня", "Боря").ApplyAsync();

        var text = File.ReadAllText(Path.Combine(repo.Root, ".gitignore")).Replace("\r\n", "\n");
        Assert.StartsWith("/MyStuff/\n", text);
        Assert.DoesNotContain("# Unity (по шаблону", text);
        Assert.True(await IgnoredAsync(repo, "Assets/IGNORE_FOLDER/Pack/Rock.fbx"));
        Assert.True(await IgnoredAsync(repo, "MyStuff/notes.txt"));
        // The whole Unity list comes with the block, whatever the project's own file had.
        foreach (var ignored in new[] { "Temp/x", "Game.sln", "Assets/Plugins/Editor/JetBrains/x.dll", "crash.dmp" })
            Assert.True(await IgnoredAsync(repo, ignored), ignored);
    }

    static async Task<bool> IgnoredAsync(Repo repo, string path)
    {
        // check-ignore refuses literal pathspecs, which GitRunner turns on for everything else.
        var r = await repo.Git.RunAsync(new[] { "check-ignore", "-q", "--no-index", "--", path },
            new GitRunOptions { Env = new Dictionary<string, string> { ["GIT_LITERAL_PATHSPECS"] = "0" } });
        return r.ExitCode switch { 0 => true, 1 => false, _ => throw new GitException(r) };
    }

    [Fact]
    public async Task Foreign_merge_driver_is_disabled_and_own_lines_stay()
    {
        var repo = await UnpreparedCloneAsync("*.unity merge=unityyamlmerge\n*.custom binary\n*.asset filter=tidy\n*.bin filter=lfs -text\n");

        await new ProjectSetup(repo, "Аня", "Боря").ApplyAsync();

        var text = File.ReadAllText(Path.Combine(repo.Root, ".gitattributes")).Replace("\r\n", "\n");
        Assert.Contains("# DuoSync: отключено: *.unity merge=unityyamlmerge", text);
        Assert.Contains("# DuoSync: отключено: *.asset filter=tidy", text);
        Assert.Contains("\n*.bin filter=lfs -text\n", "\n" + text);
        Assert.Contains("\n*.custom binary\n", "\n" + text);
        Assert.Contains(Templates.BlockStart, text);
        var info = File.ReadAllText(Path.Combine(await repo.GitDirAsync(), "info", "attributes"));
        Assert.Contains("*.unity merge=text", info);
        var check = await repo.Git.OutAsync("check-attr", "merge", "--", "Assets/B.unity");
        Assert.EndsWith("merge: text", check);
    }

    [Fact]
    public async Task Exchange_is_blocked_until_the_project_is_prepared()
    {
        var repo = await UnpreparedCloneAsync();
        var engine = new SyncEngine(repo, new SyncOptions { MeName = "Аня", FriendName = "Боря", PushLfs = false });

        var blocked = await engine.ReceiveAsync();
        Assert.Equal(OpStatus.Blocked, blocked.Status);
        Assert.Contains("Подготовить проект", blocked.Message);
        Assert.Equal(SyncState.NotPrepared, (await engine.CheckStatusAsync()).State);

        await new ProjectSetup(repo, "Аня", "Боря").ApplyAsync();
        Assert.Equal(OpStatus.Done, (await engine.SendAsync("подготовка")).Status);
    }
}
