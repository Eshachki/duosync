using DuoSync.Core.Merge;
using DuoSync.Core.Ops;
using static DuoSync.Core.Tests.Harness.Sandbox;

namespace DuoSync.Core.Tests;

/// <summary>Scenes, prefabs and .meta through the pipeline (L1 → L2 → L3 UnityYAMLMerge → L5), both roles.</summary>
public class SceneMergeTests
{
    const string Scene = "Assets/Scenes/Level.unity";

    static string Level(string position = "{x: 0, y: 0, z: 0}", string scale = "{x: 1, y: 1, z: 1}") =>
        "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n" +
        "--- !u!1 &100\nGameObject:\n  m_ObjectHideFlags: 0\n  serializedVersion: 6\n  m_Component:\n  - component: {fileID: 101}\n" +
        "  m_Layer: 0\n  m_Name: Hero\n  m_TagString: Untagged\n  m_IsActive: 1\n" +
        "--- !u!4 &101\nTransform:\n  m_ObjectHideFlags: 0\n  m_GameObject: {fileID: 100}\n  serializedVersion: 2\n" +
        "  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}\n" +
        $"  m_LocalPosition: {position}\n  m_LocalScale: {scale}\n  m_Children: []\n  m_Father: {{fileID: 0}}\n";

    static bool HaveUnity => YamlMergeTool.Find(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "duosync-tests", "uym-cache")) != null;

    static async Task<Harness.Sandbox> WithSceneAsync()
    {
        var sb = await CreateAsync();
        sb.Owner.Write(Scene, Level());
        sb.Owner.Write(Scene + ".meta", "fileFormatVersion: 2\nguid: 33333333333333333333333333333333\n");
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.SendAsync("Уровень")).Status);
        Assert.Equal(OpStatus.Done, (await sb.Friend.Engine.ReceiveAsync()).Status);
        return sb;
    }

    [Fact]
    public async Task Different_properties_of_one_object_merge_without_Claude_even_at_the_friends()
    {
        if (!HaveUnity) return;
        await using var sb = await WithSceneAsync();
        sb.Owner.Write(Scene, Level(position: "{x: 3, y: 0, z: 0}"));
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.SendAsync("Герой правее")).Status);
        sb.Friend.Write(Scene, Level(scale: "{x: 2, y: 2, z: 2}")); // the next line: git alone sees a conflict

        var sent = await sb.Friend.Engine.SendAsync("Герой больше");

        Assert.Equal(OpStatus.Done, sent.Status); // no request: UnityYAMLMerge decided it, nothing thrown away
        var merged = sb.Friend.Read(Scene);
        Assert.Contains("m_LocalPosition: {x: 3, y: 0, z: 0}", merged);
        Assert.Contains("m_LocalScale: {x: 2, y: 2, z: 2}", merged);
        var feed = new DuoSync.Core.Feed.ProjectFeed(await sb.Friend.Engine.Repo.DuoDirAsync()).ReadLast(20);
        Assert.Contains(feed, e => e.Text.Contains("слил UnityYAMLMerge"));
    }

    [Theory]
    [InlineData("uym-mine", "{x: 1, y: 0, z: 0}")]
    [InlineData("uym-theirs", "{x: 2, y: 0, z: 0}")]
    public async Task One_property_changed_by_both_is_decided_by_Claude_for_the_whole_file(string kind, string expected)
    {
        if (!HaveUnity) return;
        await using var sb = await WithSceneAsync();
        var resolver = new ScriptedResolver(job =>
        {
            var item = Assert.Single(job.Items);
            Assert.Equal(ConflictKind.Yaml, item.Kind);
            Assert.NotEmpty(item.Disputes);
            Assert.True(File.Exists(Path.Combine(job.WorkDir, "uym", "Assets", "Scenes", "Level.unity.txt")));
            ScriptedResolver.Answer(job, Scene, null, kind);
        });
        sb.Owner.Options.CanResolve = true;
        sb.Owner.Options.Resolver = resolver;
        sb.Owner.Write(Scene, Level(position: "{x: 1, y: 0, z: 0}"));
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.SendAsync("Герой на 1")).Status);
        sb.Friend.Write(Scene, Level(position: "{x: 2, y: 0, z: 0}"));
        Assert.Equal(OpStatus.Requested, (await sb.Friend.Engine.SendAsync("Герой на 2")).Status);
        var pending = Assert.Single((await sb.Owner.Engine.CheckStatusAsync()).Requests);

        var merged = await sb.Owner.Engine.MergeWithClaudeAsync(pending, _ => Task.FromResult<string?>(null));

        Assert.Equal(OpStatus.Done, merged.Status);
        Assert.Contains($"m_LocalPosition: {expected}", sb.Owner.Read(Scene));
    }

    [Fact]
    public async Task Different_Unity_versions_stop_the_exchange_with_nothing_applied()
    {
        await using var sb = await CreateAsync();
        sb.Owner.Write("ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.5.10f1\n");
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.SendAsync("Unity 6000.5.10")).Status);
        sb.Friend.Write("ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.6.0f1\n");

        var got = await sb.Friend.Engine.ReceiveAsync();

        Assert.Equal(OpStatus.Blocked, got.Status);
        Assert.Contains("разные версии Unity", got.Message);
        Assert.Contains("6000.6.0f1", sb.Friend.Read("ProjectSettings/ProjectVersion.txt"));
    }

    [Fact]
    public async Task Meta_argument_is_won_by_the_side_that_changed_the_asset_at_the_integrator_and_asked_at_the_friends()
    {
        await using var sb = await CreateAsync();
        const string Meta = "Assets/Scripts/Player.cs.meta";
        sb.Owner.Write(Meta, "fileFormatVersion: 2\nguid: 11111111111111111111111111111111\nuserData: \n");
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.SendAsync("meta")).Status);
        Assert.Equal(OpStatus.Done, (await sb.Friend.Engine.ReceiveAsync()).Status);

        // The owner changes the script and its import settings; the friend only the import settings.
        sb.Owner.Write("Assets/Scripts/Player.cs", Lines("class Player", "{", "    int speed = 5;", "    int jump = 2;", "    int lives = 3;", "}"));
        sb.Owner.Write(Meta, "fileFormatVersion: 2\nguid: 11111111111111111111111111111111\nuserData: owner\n");
        Assert.Equal(OpStatus.Done, (await sb.Owner.Engine.SendAsync("Жизни")).Status);
        sb.Friend.Write(Meta, "fileFormatVersion: 2\nguid: 11111111111111111111111111111111\nuserData: friend\n");

        // The friend's program would have to throw one side away: it asks instead.
        Assert.Equal(OpStatus.Requested, (await sb.Friend.Engine.SendAsync("Импорт")).Status);

        // The integrator's own receive of the same situation decides by the rule: the owner changed the asset.
        sb.Owner.Options.CanResolve = true;
        sb.Owner.Options.Resolver = new ScriptedResolver();
        var pending = Assert.Single((await sb.Owner.Engine.CheckStatusAsync()).Requests);
        var merged = await sb.Owner.Engine.MergeWithClaudeAsync(pending, _ => Task.FromResult<string?>(null));
        Assert.Equal(OpStatus.Done, merged.Status);
        Assert.Contains("userData: owner", sb.Owner.Read(Meta));
    }
}

static class LetExtensions
{
    public static TResult Let<T, TResult>(this T value, Func<T, TResult> f) => f(value);
}
