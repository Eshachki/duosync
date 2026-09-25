namespace DuoSync.Core.Ops;

/// <summary>
/// Automatic description of a send when the person leaves «Что сделал» empty (§3.7 AutoSummary):
/// "Сцены: Main; скрипты: Player, Shop и ещё 2; картинки: 3 шт.; удалено: 1".
/// </summary>
public static class ChangeSummary
{
    static readonly (string Title, string[] Exts)[] Groups =
    {
        ("сцены", new[] { ".unity" }),
        ("префабы", new[] { ".prefab" }),
        ("скрипты", new[] { ".cs", ".shader", ".hlsl", ".cginc", ".compute", ".asmdef", ".jslib" }),
        ("материалы", new[] { ".mat" }),
        ("анимации", new[] { ".anim", ".controller", ".overrideController", ".playable" }),
        ("картинки", new[] { ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".exr", ".hdr", ".bmp", ".gif", ".webp", ".spriteatlas", ".spriteatlasv2" }),
        ("модели", new[] { ".fbx", ".obj", ".blend", ".glb", ".dae" }),
        ("звук", new[] { ".wav", ".mp3", ".ogg", ".aif", ".aiff", ".flac", ".m4a", ".mixer" }),
        ("шрифты", new[] { ".ttf", ".otf" }),
    };

    /// <param name="changes">Path and git status letter (A, M, D, R…) for every changed file, .meta files included.</param>
    public static string Describe(IEnumerable<(string Path, char Status)> changes)
    {
        var list = changes.Where(c => !c.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Count == 0) return "Мелкие правки (только .meta)";

        var deleted = list.Count(c => c.Status == 'D');
        var alive = list.Where(c => c.Status != 'D').ToList();
        var parts = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (title, exts) in Groups)
        {
            var items = alive.Where(c => exts.Contains(Path.GetExtension(c.Path), StringComparer.OrdinalIgnoreCase)).ToList();
            if (items.Count == 0) continue;
            foreach (var i in items) used.Add(i.Path);
            parts.Add(title is "картинки" or "звук" or "шрифты" or "модели" && items.Count > 3
                ? $"{title}: {items.Count} шт."
                : $"{title}: {Names(items.Select(i => i.Path))}");
        }
        var settings = alive.Where(c => !used.Contains(c.Path) && c.Path.StartsWith("ProjectSettings/", StringComparison.Ordinal)).ToList();
        if (settings.Count > 0) { parts.Add("настройки проекта"); foreach (var s in settings) used.Add(s.Path); }
        var packages = alive.Where(c => !used.Contains(c.Path) && c.Path.StartsWith("Packages/", StringComparison.Ordinal)).ToList();
        if (packages.Count > 0) { parts.Add("пакеты"); foreach (var p in packages) used.Add(p.Path); }
        var other = alive.Count(c => !used.Contains(c.Path));
        if (other > 0) parts.Add($"прочее: {other}");
        if (deleted > 0) parts.Add($"удалено: {deleted}");

        var text = string.Join("; ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    static string Names(IEnumerable<string> paths)
    {
        var names = paths.Select(Path.GetFileNameWithoutExtension).Distinct().ToList();
        return names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} и ещё {names.Count - 3}";
    }
}
