using DuoSync.Core.Ops;
using static DuoSync.Core.Tests.Harness.Sandbox;

namespace DuoSync.Core.Tests;

public class ChangeSummaryTests
{
    [Fact]
    public void Groups_files_by_kind_and_skips_meta()
    {
        var text = ChangeSummary.Describe(new[]
        {
            ("Assets/Scenes/Main.unity", 'M'), ("Assets/Scenes/Main.unity.meta", 'M'),
            ("Assets/Scripts/Player.cs", 'M'), ("Assets/Scripts/Shop.cs", 'A'),
            ("Assets/Art/a.png", 'A'), ("Assets/Art/b.png", 'A'), ("Assets/Art/c.png", 'A'), ("Assets/Art/d.png", 'A'),
            ("Assets/Old.cs", 'D'),
            ("ProjectSettings/TagManager.asset", 'M'),
        });
        Assert.Equal("Сцены: Main; скрипты: Player, Shop; картинки: 4 шт.; настройки проекта; удалено: 1", text);
    }

    [Fact]
    public void Long_lists_are_shortened()
    {
        var text = ChangeSummary.Describe(Enumerable.Range(1, 6).Select(i => ($"Assets/Scripts/S{i}.cs", 'M')));
        Assert.Equal("Скрипты: S1, S2, S3 и ещё 3", text);
    }

    [Fact]
    public async Task Empty_message_becomes_a_summary_of_the_files()
    {
        await using var sb = await CreateAsync();
        sb.Friend.Write("Assets/Scripts/Coin.cs", "class Coin {}\n");
        sb.Friend.Write("Assets/Scenes/Main.unity", Lines("%YAML 1.1", "%TAG !u! tag:unity3d.com,2011:", "--- !u!1 &100", "GameObject:", "  m_Name: Knight"));

        var sent = await sb.Friend.Engine.SendAsync(null);

        Assert.Equal(OpStatus.Done, sent.Status);
        Assert.Equal("Сцены: Main; скрипты: Coin", await sb.Friend.OutAsync("log", "-1", "--format=%s"));
        Assert.Equal(new[] { "Сцены: Main; скрипты: Coin" }, (await sb.Owner.Engine.CheckStatusAsync()).IncomingSubjects);
    }
}
