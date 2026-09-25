using DuoSync.Core.Ops;
using static DuoSync.Core.Tests.Harness.Sandbox;

namespace DuoSync.Core.Tests;

public class StatusTests
{
    const string Player = "Assets/Scripts/Player.cs";

    [Fact]
    public async Task Incoming_update_and_overlap_with_my_unsent_edit_are_reported()
    {
        await using var sb = await CreateAsync();
        sb.Friend.Write(Player, Lines("class Player", "{", "    int speed = 7;", "    int jump = 2;", "}"));
        Assert.Equal(OpStatus.Done, (await sb.Friend.Engine.SendAsync("Кнопки классов")).Status);
        sb.Owner.Write(Player, Lines("class Player", "{", "    int speed = 5;", "    int jump = 3;", "}"));
        sb.Owner.Write("Assets/Scripts/Shop.cs", "class Shop {}\n");

        var st = await sb.Owner.Engine.CheckStatusAsync();

        Assert.Equal(SyncState.Both, st.State);
        Assert.Contains(Player, st.IncomingFiles);
        Assert.Contains(Player, st.Overlap);
        Assert.Contains("Assets/Scripts/Shop.cs", st.UnsentFiles);
        Assert.Equal(new[] { "Кнопки классов" }, st.IncomingSubjects);
        Assert.Equal(await sb.RemoteHeadAsync(), st.RemoteSha);
    }

    [Fact]
    public async Task In_sync_after_receive()
    {
        await using var sb = await CreateAsync();
        sb.Friend.Write("Assets/Scripts/Coin.cs", "class Coin {}\n");
        Assert.Equal(OpStatus.Done, (await sb.Friend.Engine.SendAsync("монета")).Status);
        Assert.Equal(SyncState.Incoming, (await sb.Owner.Engine.CheckStatusAsync()).State);

        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.ReceiveAsync()).Status);

        Assert.Equal(SyncState.InSync, (await sb.Owner.Engine.CheckStatusAsync()).State);
    }

    [Fact]
    public async Task Unreachable_github_is_offline_not_an_error()
    {
        await using var sb = await CreateAsync();
        await sb.Owner.Git.RunCheckedAsync("remote", "set-url", "origin", Path.Combine(sb.Root, "missing.git"));

        var st = await sb.Owner.Engine.CheckStatusAsync();

        Assert.Equal(SyncState.Offline, st.State);
    }
}
