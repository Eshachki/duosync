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
    async Task<Repo> UnpreparedCloneAsync(string gitattributes = "")
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
