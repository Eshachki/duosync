using DuoSync.Core.Ops;
using DuoSync.Core.Tests.Harness;
using static DuoSync.Core.Tests.Harness.Sandbox;

namespace DuoSync.Core.Tests;

public class ApplyFailureTests
{
    [Fact]
    public async Task Busy_file_is_detected_before_anything_is_written()
    {
        await using var sb = await CreateAsync();
        sb.Friend.Write("Assets/Scripts/Player.cs", Lines("class Player", "{", "    int speed = 4;", "    int jump = 2;", "}"));
        Assert.Equal(OpStatus.Done, (await sb.Friend.Engine.SendAsync("four")).Status);
        var ownerFile = Path.Combine(sb.Owner.Dir, "Assets/Scripts/Player.cs");
        var before = await sb.Owner.HeadAsync();

        OpResult got;
        using (new FileStream(ownerFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            got = await sb.Owner.Engine.ReceiveAsync();

        Assert.Equal(OpStatus.Blocked, got.Status);
        Assert.Contains("занят", got.Message);
        Assert.Contains("speed = 5", sb.Owner.Read("Assets/Scripts/Player.cs"));
        Assert.Equal(before, await sb.Owner.HeadAsync());
        Assert.False(new Journal(await sb.Owner.Repo.DuoDirAsync()).Exists);
    }

    [Fact]
    public async Task Git_failing_half_way_returns_the_folder_to_its_previous_state()
    {
        await using var sb = await CreateAsync();
        // Two incoming changes; the second file (in git's order) stays locked, so checkout fails after writing the first.
        sb.Friend.Write("Assets/A_first.cs", "class A { int v = 2; }\n");
        sb.Friend.Write("Assets/Scripts/Player.cs", Lines("class Player", "{", "    int speed = 4;", "    int jump = 2;", "}"));
        Assert.Equal(OpStatus.Done, (await sb.Friend.Engine.SendAsync("two files")).Status);
        sb.Owner.Engine.Applier.LockProbeEnabled = false;
        var before = await sb.Owner.HeadAsync();

        OpResult got;
        var locked = Path.Combine(sb.Owner.Dir, "Assets/Scripts/Player.cs");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            got = await sb.Owner.Engine.ReceiveAsync();

        Assert.True(got.Status is OpStatus.Failed or OpStatus.Blocked, got.Message);
        Assert.Equal(before, await sb.Owner.HeadAsync());
        Assert.False(sb.Owner.Exists("Assets/A_first.cs"));
        Assert.Contains("speed = 5", sb.Owner.Read("Assets/Scripts/Player.cs"));
        Assert.False(await sb.Owner.Repo.HasTrackedChangesAsync());
        Assert.False(new Journal(await sb.Owner.Repo.DuoDirAsync()).Exists);

        // With the file free again the same receive succeeds.
        sb.Owner.Engine.Applier.LockProbeEnabled = true;
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.ReceiveAsync()).Status);
        Assert.True(sb.Owner.Exists("Assets/A_first.cs"));
        Assert.Contains("speed = 4", sb.Owner.Read("Assets/Scripts/Player.cs"));
    }
}
