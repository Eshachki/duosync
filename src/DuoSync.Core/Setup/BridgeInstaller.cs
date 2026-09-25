using System.Reflection;
using System.Text.Json;

namespace DuoSync.Core.Setup;

/// <summary>
/// Writes the embedded Unity bridge into Packages/com.duosync.bridge (§4.1). The package lives in the repository, so
/// both people get the same version; the app never overwrites a newer bridge and never downgrades one.
/// </summary>
public static class BridgeInstaller
{
    public const string PackageDir = "Packages/com.duosync.bridge";
    const string Prefix = "bridge/";

    static Assembly Asm => typeof(BridgeInstaller).Assembly;

    public static IReadOnlyList<string> EmbeddedFiles() => Asm.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
        .Select(n => n[Prefix.Length..].Replace('\\', '/'))
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToList();

    public static string EmbeddedVersion => VersionOf(ReadEmbedded("package.json")) ?? "0.0.0";

    /// <summary>Version of the bridge in the project, or null when it is not installed.</summary>
    public static string? InstalledVersion(string projectRoot)
    {
        var path = Path.Combine(projectRoot, PackageDir, "package.json");
        return File.Exists(path) ? VersionOf(File.ReadAllText(path)) : null;
    }

    public static bool IsUnityProject(string projectRoot)
        => Directory.Exists(Path.Combine(projectRoot, "Assets")) && Directory.Exists(Path.Combine(projectRoot, "Packages"));

    /// <summary>Files (relative to the project) and contents to write when the bridge is missing.</summary>
    public static IEnumerable<(string Path, byte[] Content)> FilesToInstall()
    {
        foreach (var rel in EmbeddedFiles())
            yield return ($"{PackageDir}/{rel}", ReadEmbeddedBytes(rel));
    }

    /// <summary>
    /// Adds the embedded bridge to Packages/packages-lock.json exactly as Unity writes it (alphabetical, two-space
    /// indent), so the first editor start after setup does not show up as a change. Returns false when nothing changed.
    /// </summary>
    public static bool AddToPackagesLock(string projectRoot)
    {
        var path = Path.Combine(projectRoot, "Packages", "packages-lock.json");
        if (!File.Exists(path)) return false;
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        const string name = "com.duosync.bridge";
        if (text.Contains($"\"{name}\"", StringComparison.Ordinal)) return false;

        var lines = text.Split('\n').ToList();
        int start = lines.IndexOf("  \"dependencies\": {");
        if (start < 0) return false;
        var entry = new[]
        {
            $"    \"{name}\": {{",
            $"      \"version\": \"file:{name}\",",
            "      \"depth\": 0,",
            "      \"source\": \"embedded\",",
            "      \"dependencies\": {}",
            "    }",
        };
        for (int i = start + 1; i < lines.Count; i++)
        {
            if (lines[i] == "  }" || lines[i] == "  },")
            {
                // Last in order: the previous entry gets a comma.
                if (lines[i - 1] == "    }") lines[i - 1] = "    },";
                lines.InsertRange(i, entry);
                break;
            }
            var m = System.Text.RegularExpressions.Regex.Match(lines[i], "^    \"([^\"]+)\": \\{$");
            if (m.Success && string.CompareOrdinal(m.Groups[1].Value, name) > 0)
            {
                lines.InsertRange(i, entry.Take(entry.Length - 1).Append("    },"));
                break;
            }
        }
        File.WriteAllText(path, string.Join("\n", lines));
        return true;
    }

    static string ReadEmbedded(string rel) => System.Text.Encoding.UTF8.GetString(ReadEmbeddedBytes(rel));

    static byte[] ReadEmbeddedBytes(string rel)
    {
        var name = Asm.GetManifestResourceNames().First(n => n[Prefix.Length..].Replace('\\', '/') == rel && n.StartsWith(Prefix, StringComparison.Ordinal));
        using var s = Asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    static string? VersionOf(string packageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(packageJson);
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
