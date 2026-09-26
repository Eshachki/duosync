using System.Text;
using System.Text.RegularExpressions;

namespace DuoSync.Core.Merge;

/// <summary>One object of a scene or prefab: <c>--- !u!&lt;class&gt; &amp;&lt;fileID&gt;[ stripped]</c> and its fields.</summary>
public sealed record YamlDocument(int ClassId, long Anchor, bool Stripped, string Text);

/// <summary>Unity's YAML (scenes, prefabs, assets) as far as merging needs it (§5.4, §5.6, decisions C and D).</summary>
public static class UnityYaml
{
    public const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";
    static readonly Regex DocumentLine = new(@"^--- !u!(\d+) &(-?\d+)( stripped)?\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    static readonly Regex PropertyPath = new(@"^(\s*propertyPath: )([^'\s\r\n][^\r\n]*[\[\]][^\r\n]*?)(\r?)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>By content, not extension: <c>ProjectSettings/XRSettings.asset</c> is JSON (decisions D.2).</summary>
    public static bool IsUnityYaml(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return bytes.Length - start >= 5 && Encoding.ASCII.GetString(bytes, start, 5) == "%YAML";
    }

    public static List<YamlDocument> Documents(string text)
    {
        var list = new List<YamlDocument>();
        var matches = DocumentLine.Matches(text);
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            list.Add(new YamlDocument(int.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value), m.Groups[3].Success, text[m.Index..end]));
        }
        return list;
    }

    /// <summary>
    /// Objects that one side deleted while the other changed them. UnityYAMLMerge always keeps the deletion and says
    /// so only when LEFT deleted (decisions C.5): the program finds them itself, and they go to Claude.
    /// </summary>
    public static IReadOnlyList<(long Anchor, bool DeletedByMine)> DeletedVsChanged(string baseText, string mine, string theirs)
    {
        static Dictionary<long, string> Map(string t) =>
            Documents(t).GroupBy(d => d.Anchor).ToDictionary(g => g.Key, g => g.First().Text.ReplaceLineEndings("\n"));
        var b = Map(baseText);
        var m = Map(mine);
        var t = Map(theirs);
        var result = new List<(long, bool)>();
        foreach (var (anchor, text) in b)
        {
            var inMine = m.TryGetValue(anchor, out var mt);
            var inTheirs = t.TryGetValue(anchor, out var tt);
            if (!inMine && inTheirs && tt != text) result.Add((anchor, true));
            else if (inMine && !inTheirs && mt != text) result.Add((anchor, false));
        }
        return result;
    }

    /// <summary>
    /// Unity 6000 writes <c>propertyPath: 'm_Materials.Array.data[0]'</c>; UnityYAMLMerge drops the quotes. With them
    /// back its output equals the text merge byte for byte (decisions C.7).
    /// </summary>
    public static string Requote(string text) =>
        PropertyPath.Replace(text, m => m.Groups[1].Value + "'" + m.Groups[2].Value.Replace("'", "''") + "'" + m.Groups[3].Value);

    /// <summary>What is structurally wrong with a merged YAML file (L5): headers, document lines, anchors, prefab overrides.</summary>
    public static IEnumerable<string> Problems(string text)
    {
        var normalized = text.TrimStart('﻿').ReplaceLineEndings("\n");
        if (!normalized.StartsWith("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n", StringComparison.Ordinal))
            yield return "нет заголовков %YAML 1.1 / %TAG";
        foreach (var line in normalized.Split('\n'))
            if (line.StartsWith("---", StringComparison.Ordinal) && !DocumentLine.IsMatch(line))
            {
                yield return "неправильная строка документа: " + (line.Length > 60 ? line[..60] : line);
                break;
            }
        var docs = Documents(normalized);
        var duplicate = docs.GroupBy(d => d.Anchor).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) yield return $"якорь &{duplicate.Key} встречается {duplicate.Count()} раза";
        foreach (var doc in docs.Where(d => d.ClassId == 1001))
            if (DuplicateOverride(doc.Text) is { } dup) yield return $"повтор оверрайда префаба &{doc.Anchor}: {dup}";
    }

    static readonly Regex Modification = new(
        @"^\s*- target: \{fileID: (-?\d+), guid: ([0-9a-f]+), type: \d+\}\s*\n\s*propertyPath: ([^\n]+)$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>(target.fileID, target.guid, propertyPath) must be unique in m_Modifications (decisions D.4).</summary>
    static string? DuplicateOverride(string prefabInstance)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Modification.Matches(prefabInstance))
        {
            var key = $"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value.Trim().Trim('\'')}";
            if (!seen.Add(key)) return key;
        }
        return null;
    }
}
