using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DuoSync.Core.Git;

namespace DuoSync.Core.Merge;

/// <summary>
/// L5 (§5.6, decisions D): a minimal deterministic check of a merge result before it is applied, over the paths that
/// differ from either parent. Only what the merge brought in counts: the same problem already present in one of the
/// parents came in as it was and does not block, or one asset without .meta in a send would lock the exchange forever.
/// </summary>
public static class TreeCheck
{
    static readonly Regex Marker = new(@"^(<{7}|={7}|>{7}|\|{7})( |\r?$)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    static readonly Regex MetaHeader = new(@"^﻿?fileFormatVersion: 2\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    static readonly Regex MetaGuid = new(@"^guid: ([0-9a-f]{32})\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    static readonly string[] Json = { ".json", ".asmdef", ".asmref" };

    public static async Task<IReadOnlyList<string>> CheckAsync(Repo repo, string result, string mine, string theirs, CancellationToken ct = default)
    {
        var changed = (await ChangedAsync(repo, mine, result, ct)).Union(await ChangedAsync(repo, theirs, result, ct), StringComparer.Ordinal).ToList();
        if (changed.Count == 0) return Array.Empty<string>();
        var now = await BlobsAsync(repo, result, changed, ct);
        var inMine = await BlobsAsync(repo, mine, changed, ct);
        var inTheirs = await BlobsAsync(repo, theirs, changed, ct);
        var problems = new List<string>();

        foreach (var path in changed)
        {
            if (now[path] is not { } bytes) continue;
            var found = ProblemsOf(path, bytes).ToList();
            if (found.Count == 0) continue;
            var inherited = ProblemsOf(path, inMine[path]).Concat(ProblemsOf(path, inTheirs[path])).ToHashSet(StringComparer.Ordinal);
            problems.AddRange(found.Where(p => !inherited.Contains(p)).Select(p => $"{path}: {p}"));
        }

        // Asset and .meta travel together in Assets/ and in embedded packages (decisions D.3).
        var files = await PathsAsync(repo, result, ct);
        var folders = files.SelectMany(Parents).ToHashSet(StringComparer.Ordinal);
        var mineFiles = await PathsAsync(repo, mine, ct);
        var theirsFiles = await PathsAsync(repo, theirs, ct);
        foreach (var path in changed.Where(UnityVisible))
        {
            if (path.EndsWith(".meta", StringComparison.Ordinal))
            {
                var asset = path[..^5];
                if (!files.Contains(path) || files.Contains(asset) || folders.Contains(asset)) continue;
                if (now[path] is { } meta && Encoding.UTF8.GetString(meta).Contains("folderAsset: yes", StringComparison.Ordinal)) continue; // empty folder: git keeps no folders
                if (Orphan(mineFiles, path, asset) || Orphan(theirsFiles, path, asset)) continue;
                problems.Add($"{path}: .meta осталась без ассета");
            }
            else if (files.Contains(path) && !files.Contains(path + ".meta"))
            {
                if (Bare(mineFiles, path) || Bare(theirsFiles, path)) continue;
                problems.Add($"{path}: ассет без .meta");
            }
        }

        // A GUID the merge brought in must be unique in the tree.
        var brought = changed.Where(p => p.EndsWith(".meta", StringComparison.Ordinal) && now[p] != null)
            .Select(p => (Path: p, Guid: GuidOf(now[p]!))).Where(x => x.Guid != null).ToList();
        if (brought.Count > 0)
        {
            var counts = await GuidCountsAsync(repo, result, ct);
            if (brought.Any(b => counts.GetValueOrDefault(b.Guid!) > 1))
            {
                var mineCounts = await GuidCountsAsync(repo, mine, ct);
                var theirsCounts = await GuidCountsAsync(repo, theirs, ct);
                foreach (var (path, guid) in brought)
                    if (counts.GetValueOrDefault(guid!) > 1 && mineCounts.GetValueOrDefault(guid!) <= 1 && theirsCounts.GetValueOrDefault(guid!) <= 1)
                        problems.Add($"{path}: GUID {guid} встречается в проекте дважды");
            }
        }
        return problems;
    }

    static bool Orphan(HashSet<string> files, string meta, string asset) => files.Contains(meta) && !files.Contains(asset) && !files.Any(f => f.StartsWith(asset + "/", StringComparison.Ordinal));
    static bool Bare(HashSet<string> files, string asset) => files.Contains(asset) && !files.Contains(asset + ".meta");

    /// <summary>Paths Unity imports: under Assets/ or Packages/&lt;name&gt;/, without hidden segments (".x", "x~", cvs, *.tmp).</summary>
    static bool UnityVisible(string path)
    {
        var parts = path.Split('/');
        if (parts.Length < 2) return false;
        if (parts[0] != "Assets" && !(parts[0] == "Packages" && parts.Length > 2)) return false;
        return !parts.Skip(1).Any(p => p.StartsWith('.') || p.EndsWith('~') || p.Equals("cvs", StringComparison.OrdinalIgnoreCase)
                                        || p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    static IEnumerable<string> Parents(string path)
    {
        for (var i = path.LastIndexOf('/'); i > 0; i = path.LastIndexOf('/', i - 1)) yield return path[..i];
    }

    static IEnumerable<string> ProblemsOf(string path, byte[]? bytes)
    {
        if (bytes == null) yield break;
        var probe = Math.Min(bytes.Length, 8000);
        if (Array.IndexOf(bytes, (byte)0, 0, probe) >= 0) yield break; // binary
        var text = Encoding.UTF8.GetString(bytes);
        if (Marker.IsMatch(text)) yield return "остались маркеры конфликта";
        if (UnityYaml.IsUnityYaml(bytes))
            foreach (var p in UnityYaml.Problems(text)) yield return p;
        if (path.EndsWith(".meta", StringComparison.Ordinal))
        {
            if (!MetaHeader.IsMatch(text)) yield return "нет строки fileFormatVersion: 2";
            if (!MetaGuid.IsMatch(text)) yield return "нет правильного guid";
        }
        if (Json.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)) && (path.StartsWith("Assets/") || path.StartsWith("Packages/") || path.StartsWith("ProjectSettings/")))
        {
            string? error = null;
            try { using var _ = JsonDocument.Parse(text.TrimStart('﻿'), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
            catch (JsonException) { error = "не разбирается как JSON"; }
            if (error != null) yield return error;
        }
    }

    static string? GuidOf(byte[] meta) => MetaGuid.Match(Encoding.UTF8.GetString(meta)) is { Success: true } m ? m.Groups[1].Value : null;

    static async Task<List<string>> ChangedAsync(Repo repo, string from, string to, CancellationToken ct) =>
        GitParse.SplitZ((await repo.Git.RunCheckedAsync(new[] { "diff-tree", "-r", "--name-only", "-z", "--no-renames", from, to }, null, ct)).StdOutBytes)
            .Where(p => p.Length > 0).ToList();

    static async Task<HashSet<string>> PathsAsync(Repo repo, string rev, CancellationToken ct) =>
        GitParse.SplitZ((await repo.Git.RunCheckedAsync(new[] { "ls-tree", "-r", "--name-only", "-z", rev }, null, ct)).StdOutBytes)
            .Where(p => p.Length > 0).ToHashSet(StringComparer.Ordinal);

    static async Task<Dictionary<string, int>> GuidCountsAsync(Repo repo, string rev, CancellationToken ct)
    {
        var r = await repo.Git.RunAsync(new[] { "grep", "-I", "-z", "-P", "^guid: [0-9a-f]{32}", rev }, null, ct);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (r.ExitCode != 0) return counts;
        // With -z every match is "<rev>:<path>\0<line>\n": NUL after the name instead of ':'.
        foreach (var line in r.StdOut.Split('\n'))
        {
            var z = line.IndexOf('\0');
            if (z < 0 || !line[..z].EndsWith(".meta", StringComparison.Ordinal)) continue;
            var value = line[(z + 1)..].Trim();
            if (!value.StartsWith("guid: ", StringComparison.Ordinal)) continue;
            var guid = value["guid: ".Length..];
            counts[guid] = counts.GetValueOrDefault(guid) + 1;
        }
        return counts;
    }

    /// <summary>Contents of <paramref name="paths"/> at <paramref name="rev"/> in one <c>git cat-file --batch</c> (null: no such file).</summary>
    public static async Task<Dictionary<string, byte[]?>> BlobsAsync(Repo repo, string rev, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var input = Encoding.UTF8.GetBytes(string.Concat(paths.Select(p => $"{rev}:{p}\n")));
        var output = (await repo.Git.RunCheckedAsync(new[] { "cat-file", "--batch" }, new GitRunOptions { StdIn = input, Timeout = TimeSpan.FromMinutes(5) }, ct)).StdOutBytes;
        var result = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        int pos = 0;
        foreach (var path in paths)
        {
            var eol = Array.IndexOf(output, (byte)'\n', pos);
            if (eol < 0) { result[path] = null; continue; }
            var header = Encoding.UTF8.GetString(output, pos, eol - pos);
            pos = eol + 1;
            var parts = header.Split(' ');
            if (parts.Length == 3 && parts[1] == "blob" && int.TryParse(parts[2], out var size))
            {
                result[path] = output.AsSpan(pos, size).ToArray();
                pos += size + 1;
            }
            else
            {
                result[path] = null; // "missing", or a tree / submodule
                if (parts.Length == 3 && int.TryParse(parts[2], out var other)) pos += other + 1;
            }
        }
        return result;
    }
}
