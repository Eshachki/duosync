using System.Text;
using DuoSync.Core.Git;

namespace DuoSync.Core.Merge;

/// <summary>
/// What the deterministic layers decided: git's text merge (L1), the rules (L2) and UnityYAMLMerge (L3), §5.1–5.4.
/// <see cref="Tree"/> is git's merged tree with the decided paths replaced; <see cref="Unresolved"/> goes to Claude
/// at the integrator and turns into a request elsewhere.
/// </summary>
public sealed record PipelineResult(string Tree, IReadOnlyList<ConflictItem> Unresolved, IReadOnlyList<string> Notes, bool DiscardsASide, string? Stop)
{
    public bool Clean => Stop == null && Unresolved.Count == 0;
    public IReadOnlyList<string> Paths => Unresolved.Select(i => i.Path).ToList();
}

public sealed class MergePipeline
{
    readonly Repo _repo;
    readonly Func<YamlMergeTool?> _yamlTool;
    readonly bool _noMappingInOneLine;

    public MergePipeline(Repo repo, Func<YamlMergeTool?> yamlTool, bool noMappingInOneLine)
    {
        _repo = repo;
        _yamlTool = yamlTool;
        _noMappingInOneLine = noMappingInOneLine;
    }

    public async Task<PipelineResult> RunAsync(string mine, string theirs, CancellationToken ct = default)
    {
        var r = await _repo.Git.RunAsync(new[] { "--attr-source=" + mine, "merge-tree", "--write-tree", "-z", "--messages", mine, theirs },
            new GitRunOptions { Timeout = TimeSpan.FromMinutes(5) }, ct);
        var tree = GitParse.ParseMergeTree(r);
        if (tree.Clean) return new PipelineResult(tree.TreeOid, Array.Empty<ConflictItem>(), Array.Empty<string>(), false, null);

        var mergeBase = await _repo.MergeBaseAsync(mine, theirs);
        var decided = new List<(string Path, string Mode, string? Oid)>();
        var unresolved = new List<ConflictItem>();
        var notes = new List<string>();
        var discards = false;
        string? stop = null;

        foreach (var group in tree.Conflicts.GroupBy(c => c.Path, StringComparer.Ordinal))
        {
            var path = group.Key;
            string? Stage(int n) => group.FirstOrDefault(c => c.Stage == n)?.Oid;
            var (b, m, t) = (Stage(1), Stage(2), Stage(3));
            var mode = group.FirstOrDefault(c => c.Stage == 2)?.Mode ?? group.First().Mode;
            var name = Path.GetFileName(path);

            if (path == "ProjectSettings/ProjectVersion.txt")
            {
                stop = $"У вас разные версии Unity: у тебя {await VersionAsync(m)}, у друга {await VersionAsync(t)}. " +
                       "Договоритесь об одной и откройте проект в ней, потом повторите. Ничего не применено, работа цела.";
                continue;
            }
            if (m == null || t == null)
            {
                unresolved.Add(new ConflictItem(path, ConflictKind.ModifyDelete, b, m, t, mode));
                continue;
            }
            if (path == "Packages/packages-lock.json")
            {
                // Unity rebuilds the lock from manifest.json; the friend's copy is as good as any (§5.3).
                decided.Add((path, mode, t));
                discards = true;
                notes.Add("packages-lock.json: взят у друга, Unity пересоберёт его сама");
                continue;
            }
            if (path.EndsWith(".meta", StringComparison.Ordinal) && mergeBase != null)
            {
                // One field set differently: the side that changed the asset itself wins; both or none — the friend (§5.3).
                var asset = path[..^5];
                var assetBase = await _repo.BlobAsync(mergeBase, asset);
                var mineChanged = await _repo.BlobAsync(mine, asset) != assetBase;
                var theirsChanged = await _repo.BlobAsync(theirs, asset) != assetBase;
                var takeMine = mineChanged && !theirsChanged;
                decided.Add((path, mode, takeMine ? m : t));
                discards = true;
                notes.Add($"{name}: взят {(takeMine ? "свой" : "друга")} — {(takeMine ? "ассет менял ты" : theirsChanged && !mineChanged ? "ассет менял друг" : "настройки импорта спорили")}");
                continue;
            }

            var mineBytes = await BlobAsync(m, ct);
            var theirsBytes = await BlobAsync(t, ct);
            var baseBytes = b != null ? await BlobAsync(b, ct) : Encoding.UTF8.GetBytes(UnityYaml.Header);
            if (UnityYaml.IsUnityYaml(mineBytes) && UnityYaml.IsUnityYaml(theirsBytes) && UnityYaml.IsUnityYaml(baseBytes))
            {
                var yaml = await YamlAsync(path, mode, b, m, t, baseBytes, mineBytes, theirsBytes, ct);
                if (yaml.Oid != null)
                {
                    decided.Add((path, mode, yaml.Oid));
                    discards |= yaml.Discards;
                    notes.Add(yaml.Note!);
                }
                else unresolved.Add(yaml.Item!);
                continue;
            }
            var text = await MergeJob.IsTextAsync(_repo, m, path) && await MergeJob.IsTextAsync(_repo, t, path);
            unresolved.Add(new ConflictItem(path, text ? ConflictKind.Text : ConflictKind.Binary, b, m, t, mode));
        }

        // An asset that goes to Claude takes its .meta along: the pair is decided together (§5.2, «единица»).
        foreach (var meta in decided.Where(d => d.Path.EndsWith(".meta", StringComparison.Ordinal) && unresolved.Any(u => u.Path == d.Path[..^5])).ToList())
        {
            decided.Remove(meta);
            notes.RemoveAll(n => n.StartsWith(Path.GetFileName(meta.Path) + ":", StringComparison.Ordinal));
            var g = tree.Conflicts.Where(c => c.Path == meta.Path).ToList();
            unresolved.Add(new ConflictItem(meta.Path, ConflictKind.Text, g.FirstOrDefault(c => c.Stage == 1)?.Oid,
                g.FirstOrDefault(c => c.Stage == 2)?.Oid, g.FirstOrDefault(c => c.Stage == 3)?.Oid, meta.Mode));
        }

        var merged = decided.Count == 0 ? tree.TreeOid : await ReplaceAsync(tree.TreeOid, decided, ct);
        return new PipelineResult(merged, unresolved, notes, discards, stop);
    }

    async Task<(string? Oid, ConflictItem? Item, bool Discards, string? Note)> YamlAsync(string path, string mode, string? b, string m, string t,
        byte[] baseBytes, byte[] mineBytes, byte[] theirsBytes, CancellationToken ct)
    {
        var name = Path.GetFileName(path);
        var (baseText, mineText, theirsText) = (Encoding.UTF8.GetString(baseBytes), Encoding.UTF8.GetString(mineBytes), Encoding.UTF8.GetString(theirsBytes));

        // A TMP font with a dynamic atlas: glyphs of one side would point into the other side's atlas pixels (§5.3).
        if (mineText.Contains("m_AtlasPopulationMode: 1", StringComparison.Ordinal) && theirsText.Contains("m_AtlasPopulationMode: 1", StringComparison.Ordinal))
            return (t, null, true, $"{name}: шрифт с динамическим атласом взят у друга целиком");

        var deleted = UnityYaml.DeletedVsChanged(baseText, mineText, theirsText);
        var tool = _yamlTool();
        if (tool == null)
            return (null, new ConflictItem(path, ConflictKind.Yaml, b, m, t, mode) { Disputes = new[] { "UnityYAMLMerge не найден: Unity 6 на этом компьютере не установлена" } }, false, null);

        // LEFT = the friend: with -p the friend's value wins disputed properties (§5.4).
        var result = await tool.MergeAsync(baseBytes, theirsBytes, mineBytes, _noMappingInOneLine, ct);
        var problems = result.Output != null ? UnityYaml.Problems(Encoding.UTF8.GetString(result.Output)).ToList() : new List<string>();
        if (result.Clean && deleted.Count == 0 && problems.Count == 0)
            return (await HashAsync(path, result.Output!, ct), null, false, $"{name}: слил UnityYAMLMerge");

        var disputes = result.Disputes.ToList();
        disputes.AddRange(deleted.Select(d => $"объект &{d.Anchor}: {(d.DeletedByMine ? "MINE удалил, THEIRS изменил" : "THEIRS удалил, MINE изменил")}"));
        if (result.Output == null) disputes.Add("UnityYAMLMerge не справился с файлом: нужна одна сторона целиком");
        disputes.AddRange(problems.Select(p => "итог UnityYAMLMerge не прошёл проверку: " + p));
        var theirsWins = result.Output != null && problems.Count == 0 ? await HashAsync(path, result.Output, ct) : null;
        return (null, new ConflictItem(path, ConflictKind.Yaml, b, m, t, mode) { YamlTheirs = theirsWins, Disputes = disputes }, false, null);
    }

    async Task<byte[]> BlobAsync(string oid, CancellationToken ct) => (await _repo.Git.RunCheckedAsync(new[] { "cat-file", "blob", oid }, null, ct)).StdOutBytes;

    async Task<string> HashAsync(string path, byte[] content, CancellationToken ct) =>
        (await _repo.Git.RunCheckedAsync(new[] { "hash-object", "-w", "--stdin", "--path=" + path }, new GitRunOptions { StdIn = content }, ct)).StdOutTrimmed;

    async Task<string> VersionAsync(string? oid)
    {
        if (oid == null) return "нет";
        var text = Encoding.UTF8.GetString((await _repo.Git.RunCheckedAsync("cat-file", "blob", oid)).StdOutBytes);
        var line = text.Split('\n').FirstOrDefault(l => l.StartsWith("m_EditorVersion:", StringComparison.Ordinal));
        return line?["m_EditorVersion:".Length..].Trim() ?? "?";
    }

    /// <summary>The tree with some paths replaced (null oid: removed), built in a separate index.</summary>
    async Task<string> ReplaceAsync(string tree, List<(string Path, string Mode, string? Oid)> paths, CancellationToken ct)
    {
        var index = Path.Combine(await _repo.DuoDirAsync(), "pipeline.idx");
        if (File.Exists(index)) File.Delete(index);
        var env = new GitRunOptions { Env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index } };
        await _repo.Git.RunCheckedAsync(new[] { "read-tree", tree }, env, ct);
        foreach (var (path, mode, oid) in paths)
        {
            if (oid == null) await _repo.Git.RunCheckedAsync(new[] { "update-index", "--force-remove", "--", path }, env, ct);
            else await _repo.Git.RunCheckedAsync(new[] { "update-index", "--add", "--cacheinfo", mode, oid, path }, env, ct);
        }
        var result = (await _repo.Git.RunCheckedAsync(new[] { "write-tree" }, env, ct)).StdOutTrimmed;
        File.Delete(index);
        return result;
    }
}
