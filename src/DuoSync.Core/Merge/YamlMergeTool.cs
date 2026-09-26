using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DuoSync.Core.Merge;

/// <summary>UnityYAMLMerge's answer: 0 — merged; 2 — merged with LEFT's values in disputed properties; anything else — failed.</summary>
public sealed record YamlMergeResult(int Code, byte[]? Output, IReadOnlyList<string> Disputes)
{
    public bool Clean => Code == 0 && Output != null;
    public bool HasDisputes => Code == 2 && Output != null;
}

/// <summary>
/// UnityYAMLMerge (L3, §5.4). Always a copy of the exe in %LOCALAPPDATA%\DuoSync\uym\&lt;hash&gt; with its rules and one
/// added line: the stock rule is written for the pre-2018.3 class name, and without the fix both sides' edits of one
/// prefab override silently become two entries with exit code 0 (decisions C.2). UnityYAMLMerge reads its rules only
/// next to its exe. Never started without arguments: it hangs.
/// </summary>
public sealed class YamlMergeTool
{
    const string StockRule = "set *.Prefab.m_Modification.m_Modifications target.fileID target.guid propertyPath";
    const string FixedRule = "set *.PrefabInstance.m_Modification.m_Modifications target.fileID target.guid propertyPath";
    static readonly string[] RuleFiles = { "mergerules.txt", "mergespecfile.txt", "mergeresolving.txt" };
    /// <summary>A 34 MB scene takes up to 1.9 GB and 16 s: one merge at a time (decisions C.6).</summary>
    static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public string Exe { get; }

    YamlMergeTool(string exe) => Exe = exe;

    /// <summary>The tool for this project's Unity version, or null when no Unity 6 is installed.</summary>
    public static YamlMergeTool? Find(string projectRoot, string cacheDir)
    {
        var source = FindSource(projectRoot);
        return source == null ? null : new YamlMergeTool(PrepareCopy(source, cacheDir));
    }

    static string? FindSource(string root)
    {
        const string Relative = "Tools/UnityYAMLMerge.exe";
        // 1. The Unity that has this project open.
        try
        {
            var instance = Path.Combine(root, "Library", "EditorInstance.json");
            if (File.Exists(instance))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(instance));
                if (doc.RootElement.TryGetProperty("app_contents_path", out var p) && p.GetString() is { } contents
                    && File.Exists(Path.Combine(contents, Relative)))
                    return Path.GetFullPath(Path.Combine(contents, Relative));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }

        // 2. Unity Hub's default folder for the project's version; 3. any Unity 6 there (the rules are the same in 6000.2–6000.5).
        var hub = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor");
        var version = ProjectVersion(root);
        if (version != null && File.Exists(Path.Combine(hub, version, "Editor", "Data", "Tools", "UnityYAMLMerge.exe")))
            return Path.Combine(hub, version, "Editor", "Data", "Tools", "UnityYAMLMerge.exe");
        if (!Directory.Exists(hub)) return null;
        return Directory.EnumerateDirectories(hub, "6000.*").OrderByDescending(d => d, StringComparer.Ordinal)
            .Select(d => Path.Combine(d, "Editor", "Data", "Tools", "UnityYAMLMerge.exe")).FirstOrDefault(File.Exists);
    }

    public static string? ProjectVersion(string root)
    {
        try
        {
            var file = Path.Combine(root, "ProjectSettings", "ProjectVersion.txt");
            if (!File.Exists(file)) return null;
            foreach (var line in File.ReadLines(file))
                if (line.StartsWith("m_EditorVersion:", StringComparison.Ordinal)) return line["m_EditorVersion:".Length..].Trim();
        }
        catch (IOException) { }
        return null;
    }

    /// <summary>Only when the project writes inline mappings on several lines (decisions C.8); otherwise the flag adds thousands of lines.</summary>
    public static bool NeedsNoMappingFlag(string root)
    {
        try
        {
            var file = Path.Combine(root, "ProjectSettings", "EditorSettings.asset");
            return File.Exists(file) && File.ReadAllText(file).Contains("m_SerializeInlineMappingsOnOneLine: 0", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }

    static string PrepareCopy(string source, string cacheDir)
    {
        string hash;
        using (var s = File.OpenRead(source)) hash = Convert.ToHexString(SHA256.HashData(s))[..12];
        var dir = Path.Combine(cacheDir, hash);
        var exe = Path.Combine(dir, "UnityYAMLMerge.exe");
        var rules = Path.Combine(dir, "mergerules.txt");
        if (File.Exists(exe) && File.Exists(rules) && File.ReadAllText(rules).Contains(FixedRule, StringComparison.Ordinal)) return exe;

        Directory.CreateDirectory(dir);
        var from = Path.GetDirectoryName(source)!;
        File.Copy(source, exe, overwrite: true);
        foreach (var name in RuleFiles)
            if (File.Exists(Path.Combine(from, name))) File.Copy(Path.Combine(from, name), Path.Combine(dir, name), overwrite: true);
        var text = File.Exists(rules) ? File.ReadAllText(rules) : "[arrays]\n";
        if (!text.Contains(FixedRule, StringComparison.Ordinal))
        {
            text = text.Contains(StockRule, StringComparison.Ordinal)
                ? text.Replace(StockRule, StockRule + "\n" + FixedRule)
                : text.Replace("[arrays]", "[arrays]\n" + FixedRule);
            File.WriteAllText(rules, text, new UTF8Encoding(false));
        }
        return exe;
    }

    /// <summary>
    /// <c>merge -h -p --force --fallback none BASE LEFT RIGHT OUT</c>: LEFT wins disputed properties. The output is
    /// requoted; stdout is read while the tool runs (a full pipe once looked like a hang, decisions C.6).
    /// </summary>
    public async Task<YamlMergeResult> MergeAsync(byte[] baseBytes, byte[] left, byte[] right, bool noMappingInOneLine, CancellationToken ct = default)
    {
        await OneAtATime.WaitAsync(ct);
        var dir = Path.Combine(Path.GetTempPath(), "duosync-uym", Guid.NewGuid().ToString("N")[..10]);
        try
        {
            Directory.CreateDirectory(dir);
            string Put(string name, byte[] data)
            {
                var path = Path.Combine(dir, name);
                File.WriteAllBytes(path, data);
                return path;
            }
            var (b, l, r) = (Put("base", baseBytes), Put("left", left), Put("right", right));
            var output = Path.Combine(dir, "out");
            var psi = new ProcessStartInfo(Exe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
            };
            foreach (var a in new[] { "merge", "-h", "-p", "--force", "--fallback", "none" }) psi.ArgumentList.Add(a);
            if (noMappingInOneLine) psi.ArgumentList.Add("--nomappinginoneline");
            foreach (var a in new[] { b, l, r, output }) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi)!;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            int code;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                code = process.ExitCode is 0 or 2 ? process.ExitCode : 1; // 0xC0000005 and the rest count as 1 (decisions C.4)
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                code = 1;
            }
            await Task.WhenAll(stdout, stderr);
            byte[]? merged = code is 0 or 2 && File.Exists(output)
                ? Encoding.UTF8.GetBytes(UnityYaml.Requote(Encoding.UTF8.GetString(File.ReadAllBytes(output))))
                : null;
            return new YamlMergeResult(merged == null ? 1 : code, merged, ParseDisputes(stdout.Result));
        }
        finally
        {
            OneAtATime.Release();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Disputed properties from stdout: a record starts with "Left " or "Right " at the line start, indented lines
    /// belong to it, "Conflict handling:" ends the list (decisions C.9).
    /// </summary>
    public static IReadOnlyList<string> ParseDisputes(string stdout)
    {
        var records = new List<string>();
        StringBuilder? current = null;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Conflict handling:", StringComparison.Ordinal)) break;
            if (line.StartsWith("Left ", StringComparison.Ordinal) || line.StartsWith("Right ", StringComparison.Ordinal))
            {
                if (current != null) records.Add(Squeeze(current.ToString()));
                current = new StringBuilder(line.Trim());
            }
            else if (current != null && line.Length > 0 && char.IsWhiteSpace(line[0])) current.Append(' ').Append(line.Trim());
        }
        if (current != null) records.Add(Squeeze(current.ToString()));
        return records;

        static string Squeeze(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
    }
}
