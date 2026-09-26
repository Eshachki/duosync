using System.Text;
using DuoSync.Core.Merge;

namespace DuoSync.Core.Tests;

/// <summary>Scenes from the stage 0 lab (uym_synthetic.py): small, but every kind of dispute UnityYAMLMerge knows.</summary>
public class UnityYamlTests
{
    static string Go(long id, string name, long transform) =>
        $"--- !u!1 &{id}\nGameObject:\n  m_ObjectHideFlags: 0\n  serializedVersion: 6\n  m_Component:\n  - component: {{fileID: {transform}}}\n" +
        $"  m_Layer: 0\n  m_Name: {name}\n  m_TagString: Untagged\n  m_IsActive: 1\n";

    static string Tr(long id, long go, int x = 0, long[]? children = null, long father = 0) =>
        $"--- !u!4 &{id}\nTransform:\n  m_ObjectHideFlags: 0\n  m_GameObject: {{fileID: {go}}}\n  serializedVersion: 2\n" +
        "  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}\n" + $"  m_LocalPosition: {{x: {x}, y: 0, z: 0}}\n  m_LocalScale: {{x: 1, y: 1, z: 1}}\n" +
        (children is { Length: > 0 } ? "  m_Children:\n" + string.Concat(children.Select(c => $"  - {{fileID: {c}}}\n")) : "  m_Children: []\n") +
        $"  m_Father: {{fileID: {father}}}\n";

    static string Scene(params string[] docs) => UnityYaml.Header + string.Concat(docs);

    static readonly string Base = Scene(Go(100, "A", 101), Tr(101, 100, 0, new[] { 201L }), Go(200, "B", 201), Tr(201, 200, 0, null, 101));

    static YamlMergeTool? Tool() => YamlMergeTool.Find(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "duosync-tests", "uym-cache"));

    [Fact]
    public void Quotes_come_back_only_for_property_paths_with_brackets()
    {
        var text = "      propertyPath: m_Materials.Array.data[0]\n      propertyPath: m_Name\n      propertyPath: 'm_Sprites.Array.data[1]'\n";
        Assert.Equal("      propertyPath: 'm_Materials.Array.data[0]'\n      propertyPath: m_Name\n      propertyPath: 'm_Sprites.Array.data[1]'\n",
            UnityYaml.Requote(text));
    }

    [Fact]
    public void Deleted_on_one_side_and_changed_on_the_other_is_found()
    {
        var mine = Scene(Go(100, "A", 101), Tr(101, 100, 0));                                                   // I deleted B
        var theirs = Scene(Go(100, "A", 101), Tr(101, 100, 0, new[] { 201L }), Go(200, "B2", 201), Tr(201, 200, 0, null, 101)); // they renamed B

        var found = UnityYaml.DeletedVsChanged(Base, mine, theirs);

        Assert.Equal(new[] { (200L, true) }, found);
    }

    [Fact]
    public void Structure_problems_are_reported()
    {
        Assert.Empty(UnityYaml.Problems(Base));
        Assert.Contains(UnityYaml.Problems(Base + Go(100, "Copy", 101)), p => p.Contains("&100"));
        Assert.Contains(UnityYaml.Problems(Base.Replace("%TAG", "%TUG")), p => p.Contains("заголовк"));
        var prefab = Scene("--- !u!1001 &5\nPrefabInstance:\n  m_Modification:\n    m_Modifications:\n" +
                           "    - target: {fileID: 7, guid: abcdef0123456789abcdef0123456789, type: 3}\n      propertyPath: m_Speed\n      value: 1\n" +
                           "    - target: {fileID: 7, guid: abcdef0123456789abcdef0123456789, type: 3}\n      propertyPath: m_Speed\n      value: 2\n");
        Assert.Contains(UnityYaml.Problems(prefab), p => p.Contains("повтор оверрайда"));
    }

    [Fact]
    public void Disputes_are_read_from_the_tools_output()
    {
        var stdout = "Conflicts:\nLeft  101.Transform.m_LocalPosition\n    x change to 2\nRight 101.Transform.m_LocalPosition\n    x change to 1\n" +
                     "Left  300 delete\nConflict handling:\nsomething else\n";
        Assert.Equal(new[] { "Left 101.Transform.m_LocalPosition x change to 2", "Right 101.Transform.m_LocalPosition x change to 1", "Left 300 delete" },
            YamlMergeTool.ParseDisputes(stdout));
    }

    [Fact]
    public async Task Tool_merges_different_objects_and_reports_one_property_changed_by_both()
    {
        var tool = Tool();
        if (tool == null) return; // no Unity 6 on this machine
        Assert.EndsWith("UnityYAMLMerge.exe", tool.Exe);
        Assert.Contains("PrefabInstance.m_Modification", File.ReadAllText(Path.Combine(Path.GetDirectoryName(tool.Exe)!, "mergerules.txt")));

        // Different objects: clean.
        var mine = Base.Replace("m_Name: A", "m_Name: A1");
        var theirs = Base.Replace("m_Name: B", "m_Name: B1");
        var clean = await tool.MergeAsync(Bytes(Base), Bytes(theirs), Bytes(mine), noMappingInOneLine: false);
        Assert.True(clean.Clean);
        var merged = Encoding.UTF8.GetString(clean.Output!);
        Assert.Contains("m_Name: A1", merged);
        Assert.Contains("m_Name: B1", merged);

        // One property changed by both: code 2, LEFT (theirs) wins, the dispute is listed.
        var mineX = Scene(Go(100, "A", 101), Tr(101, 100, 1, new[] { 201L }), Go(200, "B", 201), Tr(201, 200, 0, null, 101));
        var theirsX = Scene(Go(100, "A", 101), Tr(101, 100, 2, new[] { 201L }), Go(200, "B", 201), Tr(201, 200, 0, null, 101));
        var disputed = await tool.MergeAsync(Bytes(Base), Bytes(theirsX), Bytes(mineX), noMappingInOneLine: false);
        Assert.True(disputed.HasDisputes);
        Assert.Contains("m_LocalPosition: {x: 2", Encoding.UTF8.GetString(disputed.Output!));
        Assert.NotEmpty(disputed.Disputes);
        Assert.Empty(UnityYaml.Problems(Encoding.UTF8.GetString(disputed.Output!)));
    }

    static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
}
