using DuoSync.Core.Ops;
using DuoSync.Core.Tests.Harness;
using static DuoSync.Core.Tests.Harness.Sandbox;

namespace DuoSync.Core.Tests;

public class ExchangeTests
{
    const string Player = "Assets/Scripts/Player.cs";
    const string Scene = "Assets/Scenes/Main.unity";

    [Fact]
    public async Task Receive_fast_forward_brings_friend_changes()
    {
        await using var sb = await CreateAsync();
        sb.Friend.Write(Player, Lines("class Player", "{", "    int speed = 7;", "    int jump = 2;", "}"));
        sb.Friend.Write("Assets/Scripts/Coin.cs", "class Coin {}\n");
        var sent = await sb.Friend.Engine.SendAsync("Скорость и монета");
        Assert.Equal(OpStatus.Done, sent.Status);

        var got = await sb.Owner.Engine.ReceiveAsync();

        Assert.Equal(OpStatus.Done, got.Status);
        Assert.Contains("speed = 7", sb.Owner.Read(Player));
        Assert.True(sb.Owner.Exists("Assets/Scripts/Coin.cs"));
        Assert.Equal(await sb.RemoteHeadAsync(), await sb.Owner.HeadAsync());
        Assert.Equal(OpStatus.UpToDate, (await sb.Owner.Engine.ReceiveAsync()).Status);
    }

    [Fact]
    public async Task Receive_keeps_my_uncommitted_edits_in_other_files()
    {
        await using var sb = await CreateAsync();
        sb.Friend.Write(Player, Lines("class Player", "{", "    int speed = 9;", "    int jump = 2;", "}"));
        Assert.Equal(OpStatus.Done, (await sb.Friend.Engine.SendAsync("speed 9")).Status);
        sb.Owner.Write(Scene, Lines("%YAML 1.1", "%TAG !u! tag:unity3d.com,2011:", "--- !u!1 &100", "GameObject:", "  m_Name: Knight"));

        var got = await sb.Owner.Engine.ReceiveAsync();

        Assert.Equal(OpStatus.Done, got.Status);
        Assert.Contains("speed = 9", sb.Owner.Read(Player));
        Assert.Contains("Knight", sb.Owner.Read(Scene));
        Assert.False(await sb.Owner.Repo.HasTrackedChangesAsync());
        var parents = await sb.Owner.OutAsync("rev-list", "--parents", "-n1", "HEAD");
        Assert.Equal(3, parents.Split(' ').Length); // merge commit: snapshot + friend's commit
    }

    [Fact]
    public async Task Second_sender_merges_and_both_changes_reach_github()
    {
        await using var sb = await CreateAsync();
        sb.Owner.Write(Scene, Lines("%YAML 1.1", "%TAG !u! tag:unity3d.com,2011:", "--- !u!1 &100", "GameObject:", "  m_Name: Knight"));
        sb.Friend.Write("Assets/Scripts/Enemy.cs", "class Enemy {}\n");
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.SendAsync("Рыцарь")).Status);

        var sent = await sb.Friend.Engine.SendAsync("Враг");

        Assert.Equal(OpStatus.Done, sent.Status);
        Assert.Contains("Knight", sb.Friend.Read(Scene));
        Assert.Equal(await sb.RemoteHeadAsync(), await sb.Friend.HeadAsync());
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.ReceiveAsync()).Status);
        Assert.True(sb.Owner.Exists("Assets/Scripts/Enemy.cs"));
    }

    [Fact]
    public async Task Conflict_applies_nothing_and_keeps_my_work()
    {
        await using var sb = await CreateAsync();
        sb.Owner.Write(Player, Lines("class Player", "{", "    int speed = 10;", "    int jump = 2;", "}"));
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.SendAsync("speed 10")).Status);
        sb.Friend.Write(Player, Lines("class Player", "{", "    int speed = 3;", "    int jump = 2;", "}"));
        var remoteBefore = await sb.RemoteHeadAsync();

        var got = await sb.Friend.Engine.ReceiveAsync();

        // Without Claude on this computer the conflict becomes a merge request (MergeTests follows it further).
        Assert.Equal(OpStatus.Requested, got.Status);
        Assert.Contains(Player, got.ConflictedPaths);
        Assert.Contains("speed = 3", sb.Friend.Read(Player));
        Assert.False(await sb.Friend.Repo.HasTrackedChangesAsync()); // his edit is safe in the snapshot commit
        Assert.Contains("speed = 3", await sb.Friend.OutAsync("show", "HEAD:" + Player));
        Assert.Equal(remoteBefore, await sb.RemoteHeadAsync());
    }

    [Fact]
    public async Task Incoming_file_never_overwrites_my_ignored_local_file()
    {
        await using var sb = await CreateAsync();
        sb.Friend.Write("Assets/notes.log", "from friend\n");
        await sb.Friend.Git.RunCheckedAsync("add", "-f", "Assets/notes.log");
        Assert.Equal(OpStatus.Done, (await sb.Friend.Engine.SendAsync("лог")).Status);
        sb.Owner.Write("Assets/notes.log", "my private notes\n");

        var got = await sb.Owner.Engine.ReceiveAsync();

        Assert.Equal(OpStatus.Blocked, got.Status);
        Assert.Equal("my private notes\n", sb.Owner.Read("Assets/notes.log"));
    }

    [Fact]
    public async Task Undo_receive_returns_files_and_next_receive_brings_them_again()
    {
        await using var sb = await CreateAsync();
        sb.Friend.Write(Player, Lines("class Player", "{", "    int speed = 8;", "    int jump = 2;", "}"));
        Assert.Equal(OpStatus.Done, (await sb.Friend.Engine.SendAsync("speed 8")).Status);
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.ReceiveAsync()).Status);
        sb.Owner.Write("Assets/Scripts/Mine.cs", "class Mine {}\n"); // my edit after the receive

        var undo = await sb.Owner.Engine.UndoReceiveAsync();

        Assert.Equal(OpStatus.Done, undo.Status);
        Assert.Contains("speed = 5", sb.Owner.Read(Player));
        Assert.True(sb.Owner.Exists("Assets/Scripts/Mine.cs"));
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.ReceiveAsync()).Status);
        Assert.Contains("speed = 8", sb.Owner.Read(Player));
        Assert.True(sb.Owner.Exists("Assets/Scripts/Mine.cs"));
    }

    [Fact]
    public async Task Interrupted_apply_is_undone_on_next_start()
    {
        await using var sb = await CreateAsync();
        sb.Friend.Write(Player, Lines("class Player", "{", "    int speed = 6;", "    int jump = 2;", "}"));
        sb.Friend.Write("Assets/Scripts/New.cs", "class New {}\n");
        Assert.Equal(OpStatus.Done, (await sb.Friend.Engine.SendAsync("шесть")).Status);

        var owner = sb.Owner;
        Assert.True((await owner.Repo.FetchAsync()).Ok);
        var from = await owner.HeadAsync();
        var to = (await owner.Repo.RevParseAsync(owner.Repo.RemoteBranchRef))!;
        // Simulate a crash in the middle of Apply: journal written, one file already replaced, one new file written.
        new Journal(await owner.Repo.DuoDirAsync()).Write(new ApplyJournal("apply", "получение", from, to, 1, DateTime.UtcNow,
            new[] { Player, "Assets/Scripts/New.cs" }));
        owner.Write(Player, sb.Friend.Read(Player));
        owner.Write("Assets/Scripts/New.cs", "class New {}\n");

        var pre = await owner.Engine.PreflightAsync();

        Assert.Null(pre);
        Assert.Contains("speed = 5", owner.Read(Player));
        Assert.False(owner.Exists("Assets/Scripts/New.cs"));
        Assert.False(new Journal(await owner.Repo.DuoDirAsync()).Exists);
        Assert.Equal(from, await owner.HeadAsync());
        Assert.NotNull(await owner.Repo.ReadRefAsync(
            (await owner.OutAsync("for-each-ref", "--format=%(refname)", "refs/duosync/save/")).Split('\n')[0]));
    }

    [Fact]
    public async Task Interrupted_apply_that_already_finished_is_just_closed()
    {
        await using var sb = await CreateAsync();
        var owner = sb.Owner;
        var from = await owner.HeadAsync();
        owner.Write("Assets/Scripts/A.cs", "class A {}\n");
        var to = (await owner.Engine.Snapshots.SnapshotAsync("A", "snapshot"))!;
        new Journal(await owner.Repo.DuoDirAsync()).Write(new ApplyJournal("apply", "получение", from, to, 1, DateTime.UtcNow,
            new[] { "Assets/Scripts/A.cs" }));

        Assert.Null(await owner.Engine.PreflightAsync());
        Assert.False(new Journal(await owner.Repo.DuoDirAsync()).Exists);
        Assert.True(owner.Exists("Assets/Scripts/A.cs"));
    }

    [Fact]
    public async Task Unfinished_manual_git_merge_blocks_operations()
    {
        await using var sb = await CreateAsync();
        File.WriteAllText(Path.Combine(await sb.Owner.Repo.GitDirAsync(), "MERGE_HEAD"), await sb.Owner.HeadAsync() + "\n");

        var got = await sb.Owner.Engine.ReceiveAsync();

        Assert.Equal(OpStatus.Blocked, got.Status);
        Assert.Contains("Починить", got.Message);
    }

    [Fact]
    public async Task Rewritten_history_on_github_stops_the_exchange()
    {
        await using var sb = await CreateAsync();
        sb.Friend.Write("Assets/Scripts/B.cs", "class B {}\n");
        Assert.Equal(OpStatus.Done, (await sb.Friend.Engine.SendAsync("B")).Status);
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.ReceiveAsync()).Status);
        // Someone force-pushes main back to the seed commit.
        await sb.Friend.Git.RunCheckedAsync("push", "-q", "--force", "origin", "HEAD~1:refs/heads/main");

        var got = await sb.Owner.Engine.ReceiveAsync();

        Assert.Equal(OpStatus.Blocked, got.Status);
        Assert.Contains("переписана", got.Message);
        Assert.True(sb.Owner.Exists("Assets/Scripts/B.cs"));
    }

    [Fact]
    public async Task Meta_sentinel_restores_guid_rewritten_by_unity()
    {
        await using var sb = await CreateAsync();
        var owner = sb.Owner;
        var head = await owner.HeadAsync();
        owner.Write("Assets/Scripts/Player.cs.meta", "fileFormatVersion: 2\nguid: 99999999999999999999999999999999\n");

        var fixedCount = await owner.Engine.Applier.MetaSentinelAsync(new[] { "Assets/Scripts/Player.cs.meta" }, head);

        Assert.Equal(1, fixedCount);
        Assert.Contains("11111111111111111111111111111111", owner.Read("Assets/Scripts/Player.cs.meta"));
    }

    [Fact]
    public async Task Open_unity_without_bridge_blocks_file_changes()
    {
        await using var sb = await CreateAsync();
        var lockFile = Path.Combine(sb.Owner.Dir, "Temp", "UnityLockfile");
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        OpResult got;
        using (new FileStream(lockFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            got = await sb.Owner.Engine.ReceiveAsync();

        Assert.Equal(OpStatus.Blocked, got.Status);
        Assert.Contains("Unity", got.Message);
        // A stale, unlocked lock file (Unity crashed) does not block.
        Assert.NotEqual(OpStatus.Blocked, (await sb.Owner.Engine.ReceiveAsync()).Status);
    }

    [Fact]
    public async Task Dropped_connection_is_retried_then_reported_as_offline_with_work_kept()
    {
        await using var sb = await CreateAsync();
        await sb.Owner.Git.RunCheckedAsync("remote", "set-url", "origin", "https://127.0.0.1:9/nowhere.git");
        sb.Owner.Write("Assets/Scripts/Mine.cs", "class Mine {}\n");

        var sent = await sb.Owner.Engine.SendAsync("моё");

        Assert.Equal(OpStatus.Offline, sent.Status);
        Assert.Equal(2, sb.Owner.ProgressLines.Count); // two repeats before giving up
        Assert.Contains("Mine.cs", await sb.Owner.OutAsync("show", "--name-only", "--format=", "HEAD")); // work is safe in a commit
    }

    [Fact]
    public async Task First_send_into_an_empty_github_repository()
    {
        await using var sb = await CreateAsync();
        var empty = Path.Combine(sb.Root, "empty.git");
        await sb.Owner.Git.RunCheckedAsync("init", "--bare", "-b", "main", empty);
        await sb.Owner.Git.RunCheckedAsync("remote", "set-url", "origin", empty);
        await sb.Owner.Repo.DeleteRefAsync(sb.Owner.Repo.RemoteBranchRef);
        await sb.Owner.Repo.DeleteRefAsync(SyncEngine.LastRemoteRef);

        Assert.Equal(OpStatus.UpToDate, (await sb.Owner.Engine.ReceiveAsync()).Status);
        var sent = await sb.Owner.Engine.SendAsync("Первая заливка");

        Assert.Equal(OpStatus.Done, sent.Status);
        Assert.Equal(await sb.Owner.HeadAsync(), await new DuoSync.Core.Git.GitRunner(empty, "t", "t@x").OutAsync("rev-parse", "main"));
    }

    [Fact]
    public async Task Friend_who_downloaded_the_empty_repository_gets_the_first_send()
    {
        await using var sb = await CreateAsync();
        var empty = Path.Combine(sb.Root, "empty.git");
        await sb.Owner.Git.RunCheckedAsync("init", "--bare", "-b", "main", empty);
        await sb.Owner.Git.RunCheckedAsync("remote", "set-url", "origin", empty);
        await sb.Owner.Repo.DeleteRefAsync(sb.Owner.Repo.RemoteBranchRef);
        await sb.Owner.Repo.DeleteRefAsync(SyncEngine.LastRemoteRef);
        // The friend downloads the project in the seconds between repository creation and the first send.
        var friendDir = Path.Combine(sb.Root, "early");
        await sb.Owner.Git.RunCheckedAsync("clone", "-q", empty, friendDir);
        var friend = new SyncEngine(new DuoSync.Core.Git.Repo(new DuoSync.Core.Git.GitRunner(friendDir, "Боря", "borya@example.invalid")),
            new SyncOptions { MeName = "Боря", FriendName = "Аня", PushLfs = false, RetryDelays = new[] { TimeSpan.Zero } });
        Assert.NotEqual(SyncState.NotPrepared, (await friend.CheckStatusAsync()).State);

        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.SendAsync("Первая заливка")).Status);
        var got = await friend.ReceiveAsync();

        Assert.Equal(OpStatus.Done, got.Status);
        Assert.True(DuoSync.Core.Setup.ProjectSetup.IsPrepared(friendDir));
        Assert.Equal(await sb.Owner.HeadAsync(), await friend.Repo.HeadAsync());
    }

    [Fact]
    public async Task Nothing_to_send_when_nothing_changed()
    {
        await using var sb = await CreateAsync();
        Assert.Equal(OpStatus.NothingToSend, (await sb.Owner.Engine.SendAsync("пусто")).Status);
    }
}
