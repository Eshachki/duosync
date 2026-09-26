using System.Text;
using DuoSync.Core.Git;
using DuoSync.Core.Ops;

namespace DuoSync.Core.Setup;

public sealed record SetupChange(string Path, string Description);

public sealed record SetupPlan(IReadOnlyList<SetupChange> Changes, IReadOnlyList<string> Warnings)
{
    public bool NothingToDo => Changes.Count == 0;
}

/// <summary>
/// «Подготовить проект» (§8.2): managed blocks in .gitattributes/.gitignore, agent rules, .duosync.json, then
/// <c>git add --renormalize .</c> and one local commit. Also the per-machine part (<see cref="EnsureLocalAsync"/>).
/// </summary>
public sealed class ProjectSetup
{
    public const string AppVersion = "0.1.0";
    public const string BridgeVersion = "0.1.0";
    const string LocalStampName = "local-setup.v1";

    /// <summary>Unity's generated folders in the project root: in git by mistake they travel with every exchange.</summary>
    static readonly string[] JunkDirs = { "Library", "Temp", "Obj", "Build", "Builds", "Logs", "UserSettings", "MemoryCaptures", "Recordings", "BuildReports", "ProfilerCaptures" };

    static readonly string[] DisabledPatterns = { "merge=unityyamlmerge", "merge=lfs" };
    /// <summary>
    /// Any clean/smudge filter except LFS: it runs only where it is installed, so the two computers would see
    /// different contents of the same file.
    /// </summary>
    static readonly System.Text.RegularExpressions.Regex ForeignFilter = new(@"(^|\s)filter=(?!lfs(\s|$))\S+");

    readonly Repo _repo;
    readonly string _me;
    readonly string _friend;

    public ProjectSetup(Repo repo, string me, string friend)
    {
        _repo = repo;
        _me = me;
        _friend = friend;
    }

    public static bool IsPrepared(string root)
    {
        var path = Path.Combine(root, ".gitattributes");
        if (!File.Exists(path) || !File.ReadAllText(path).Contains(Templates.BlockStart, StringComparison.Ordinal)) return false;
        // A Unity project also needs the bridge: without it the app cannot save scenes or reload them after receiving.
        return !BridgeInstaller.IsUnityProject(root) || BridgeInstaller.InstalledVersion(root) != null;
    }

    /// <summary>What <see cref="ApplyAsync"/> would change, for the confirmation screen.</summary>
    public async Task<SetupPlan> PlanAsync()
    {
        var (files, warnings) = await DesiredFilesAsync();
        var changes = files
            .Where(f => ReadOrNull(f.Path) != f.Content)
            .Select(f => new SetupChange(f.Path, f.Description))
            .ToList();
        if (NeedsBridge())
            changes.Add(new SetupChange(BridgeInstaller.PackageDir,
                "мост DuoSync для Unity: сохраняет сцены перед обменом и открывает изменённые заново"));
        var junk = await TrackedJunkAsync();
        if (junk.Count > 0)
            changes.Add(new SetupChange(string.Join(", ", junk.Select(j => j.Dir + "/")),
                $"убрать из git {Ru.Files(junk.Sum(j => j.Files))}, попавших туда по ошибке. С диска ничего не удаляется"));
        return new SetupPlan(changes, warnings);
    }

    public async Task<OpResult> ApplyAsync()
    {
        var (files, warnings) = await DesiredFilesAsync();
        var written = new List<string>();
        foreach (var f in files)
        {
            if (ReadOrNull(f.Path) == f.Content) continue;
            var full = Path.Combine(_repo.Root, f.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, f.Content, new UTF8Encoding(false));
            written.Add(f.Path);
        }
        if (NeedsBridge())
        {
            foreach (var (rel, content) in BridgeInstaller.FilesToInstall())
            {
                var full = Path.Combine(_repo.Root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllBytesAsync(full, content);
                written.Add(rel);
            }
            if (BridgeInstaller.AddToPackagesLock(_repo.Root)) written.Add("Packages/packages-lock.json");
        }
        await EnsureLocalAsync(force: true);

        var git = _repo.Git;
        if (written.Count > 0) await git.RunCheckedAsync(new[] { "add", "--" }.Concat(written));
        // Re-apply the clean filters with the new attributes: line endings to LF, binary files to LFS.
        await git.RunCheckedAsync(new[] { "add", "--renormalize", "." }, new GitRunOptions { Timeout = TimeSpan.FromMinutes(30) });
        var untracked = await UntrackIgnoredAsync();

        var quiet = await git.RunAsync("diff", "--cached", "--quiet");
        if (quiet.ExitCode == 0)
            return new OpResult(OpStatus.UpToDate, "Проект уже подготовлен.");
        await git.RunCheckedAsync(new[] { "commit", "-q", "--no-verify", "-F", "-" },
            new GitRunOptions { StdIn = Encoding.UTF8.GetBytes("DuoSync: подготовка проекта\n\nDuoSync: setup\n") });
        var shown = written.Where(w => !w.StartsWith(BridgeInstaller.PackageDir, StringComparison.Ordinal)).ToList();
        if (written.Count > shown.Count) shown.Add("мост Unity");
        var msg = "Проект подготовлен: " + (shown.Count > 0 ? string.Join(", ", shown) : "нормализованы концы строк") +
                  (untracked > 0 ? $"; из git убрано {Ru.Files(untracked)}, которые не должны туда попадать (на диске они остались)" : "") +
                  ". Чтобы изменения ушли на GitHub, нажми «Отправить»." +
                  (warnings.Count > 0 ? " Обрати внимание: " + string.Join(" ", warnings) : "");
        return new OpResult(OpStatus.Done, msg) { Files = written };
    }

    /// <summary>
    /// Per-machine settings that never go to git: .git/info/attributes (turns off foreign merge drivers such as Unity
    /// Smart Merge, decisions B4) and network settings for LFS. Cheap when nothing changed.
    /// </summary>
    public async Task EnsureLocalAsync(bool force = false)
    {
        var gitDir = await _repo.GitDirAsync();
        var duoDir = await _repo.DuoDirAsync();
        var stamp = Path.Combine(duoDir, LocalStampName);
        var infoPath = Path.Combine(gitDir, "info", "attributes");
        var desired = UpsertBlock(ReadFileOrNull(infoPath), Templates.InfoAttributesBlock(await BinaryAssetsAsync()));
        if (!force && File.Exists(stamp) && ReadFileOrNull(infoPath) == desired) return;

        Directory.CreateDirectory(Path.GetDirectoryName(infoPath)!);
        await File.WriteAllTextAsync(infoPath, desired, new UTF8Encoding(false));
        foreach (var (key, value) in new[]
                 {
                     ("lfs.concurrenttransfers", "3"), ("lfs.transfer.maxretries", "8"),
                     ("http.lowSpeedLimit", "1000"), ("http.lowSpeedTime", "60"),
                 })
            await _repo.Git.RunCheckedAsync("config", "--local", key, value);
        await File.WriteAllTextAsync(stamp, DateTime.UtcNow.ToString("O"));
    }

    sealed record DesiredFile(string Path, string Content, string Description);

    async Task<(List<DesiredFile> Files, List<string> Warnings)> DesiredFilesAsync()
    {
        var files = new List<DesiredFile>();
        var warnings = new List<string>();
        var binaryAssets = await BinaryAssetsAsync();

        var attrs = DisableForeign(UpsertBlock(ReadOrNull(".gitattributes"), Templates.GitAttributesBlock(binaryAssets)));
        files.Add(new DesiredFile(".gitattributes", attrs, "концы строк LF, картинки/модели/звук в LFS, без чужих merge-драйверов"));
        var ignore = ReadOrNull(".gitignore");
        files.Add(new DesiredFile(".gitignore", UpsertBlock(string.IsNullOrEmpty(ignore) ? Templates.BaseGitIgnore : ignore, Templates.GitIgnoreBlock()),
            "не отправлять Library, Temp, сборки, IGNORE_FOLDER, TRASH, web, локальные настройки MCP и нейросетей"));
        files.Add(new DesiredFile("AGENTS.md", UpsertMd(ReadOrNull("AGENTS.md"), Templates.AgentsSection(_me, _friend)),
            "правила для нейросетей: ассеты только через Unity, git не трогать"));
        files.Add(new DesiredFile("CLAUDE.md", UpsertMd(ReadOrNull("CLAUDE.md"), Templates.ClaudeSection), "Claude Code читает AGENTS.md"));
        if (ReadOrNull(".duosync.json") == null)
            files.Add(new DesiredFile(".duosync.json", Templates.DuoSyncJson(_me, _friend, AppVersion, BridgeVersion), "файл проекта DuoSync"));

        var editor = ReadOrNull("ProjectSettings/EditorSettings.asset");
        if (editor != null && !editor.Contains("m_SerializationMode: 2"))
            warnings.Add("В Unity включи Project Settings → Editor → Asset Serialization: Force Text.");
        var vcs = ReadOrNull("ProjectSettings/VersionControlSettings.asset");
        if (vcs != null && !vcs.Contains("Visible Meta Files"))
            warnings.Add("В Unity включи Project Settings → Version Control → Mode: Visible Meta Files.");
        return (files, warnings);
    }

    /// <summary>Unity's generated folders that git tracks, with the number of files in each.</summary>
    async Task<List<(string Dir, int Files)>> TrackedJunkAsync()
    {
        var r = await _repo.Git.RunAsync("ls-files", "-z");
        if (!r.Ok) return new List<(string, int)>();
        return GitParse.SplitZ(r.StdOutBytes)
            .Select(p => p.Split('/')[0])
            .Select(top => JunkDirs.FirstOrDefault(d => string.Equals(d, top, StringComparison.OrdinalIgnoreCase)))
            .Where(d => d != null)
            .GroupBy(d => d!)
            .Select(g => (g.Key, g.Count()))
            .OrderBy(x => Array.IndexOf(JunkDirs, x.Key))
            .ToList();
    }

    /// <summary>
    /// Files git tracks although .gitignore (just written) excludes them: Library, builds, logs. They leave the index
    /// in the setup commit and stay on disk. Returns how many.
    /// </summary>
    async Task<int> UntrackIgnoredAsync()
    {
        var r = await _repo.Git.RunCheckedAsync("ls-files", "-z", "--cached", "--ignored", "--exclude-standard");
        var paths = GitParse.SplitZ(r.StdOutBytes);
        if (paths.Count == 0) return 0;
        await _repo.Git.RunCheckedAsync(new[] { "rm", "-r", "--cached", "--quiet", "--pathspec-from-file=-", "--pathspec-file-nul" },
            new GitRunOptions
            {
                StdIn = Encoding.UTF8.GetBytes(string.Join('\0', paths) + "\0"),
                Timeout = TimeSpan.FromMinutes(10),
            });
        return paths.Count;
    }

    /// <summary>Tracked or new .asset files that are not YAML (TerrainData, NavMesh, …) must live in LFS as binaries.</summary>
    async Task<List<string>> BinaryAssetsAsync()
    {
        var r = await _repo.Git.RunAsync("ls-files", "-z", "--cached", "--others", "--exclude-standard");
        if (!r.Ok) return new List<string>();
        var result = new List<string>();
        foreach (var p in GitParse.SplitZ(r.StdOutBytes))
        {
            if (!p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) || p.EndsWith("LightingData.asset", StringComparison.Ordinal)) continue;
            var full = Path.Combine(_repo.Root, p);
            if (!File.Exists(full)) continue;
            var head = new byte[5];
            int n;
            using (var fs = File.OpenRead(full)) n = fs.Read(head, 0, head.Length);
            var text = Encoding.ASCII.GetString(head, 0, n);
            // LFS pointers start with "versi"; YAML with "%YAML" (a BOM is allowed before it).
            if (n >= 5 && !text.StartsWith("%YAML", StringComparison.Ordinal) && !text.StartsWith("versi", StringComparison.Ordinal)
                && !(head[0] == 0xEF && head[1] == 0xBB))
                result.Add(p);
        }
        return result;
    }

    bool NeedsBridge() => BridgeInstaller.IsUnityProject(_repo.Root) && BridgeInstaller.InstalledVersion(_repo.Root) == null;

    string? ReadOrNull(string rel) => ReadFileOrNull(Path.Combine(_repo.Root, rel));

    static string? ReadFileOrNull(string path) => File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : null;

    /// <summary>Replaces the DuoSync block (between markers) or appends it; the person's own lines stay.</summary>
    public static string UpsertBlock(string? existing, string block)
    {
        if (string.IsNullOrEmpty(existing)) return block;
        var start = existing.IndexOf(Templates.BlockStart, StringComparison.Ordinal);
        var end = existing.IndexOf(Templates.BlockEnd, StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            var afterEnd = existing.IndexOf('\n', end);
            var tail = afterEnd < 0 ? "" : existing[(afterEnd + 1)..];
            return existing[..start] + block + tail;
        }
        return existing.TrimEnd('\n') + "\n\n" + block;
    }

    static string UpsertMd(string? existing, string section)
    {
        if (string.IsNullOrEmpty(existing)) return section;
        var start = existing.IndexOf(Templates.MdStart, StringComparison.Ordinal);
        var end = existing.IndexOf(Templates.MdEnd, StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            var afterEnd = existing.IndexOf('\n', end);
            var tail = afterEnd < 0 ? "" : existing[(afterEnd + 1)..];
            return existing[..start] + section + tail;
        }
        return existing.TrimEnd('\n') + "\n\n" + section;
    }

    /// <summary>Comments out lines outside the block that would fight DuoSync (Smart Merge driver, foreign filters, merge=lfs).</summary>
    static string DisableForeign(string text)
    {
        var lines = text.Split('\n');
        bool inBlock = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.StartsWith(Templates.BlockStart, StringComparison.Ordinal)) inBlock = true;
            else if (l.StartsWith(Templates.BlockEnd, StringComparison.Ordinal)) inBlock = false;
            else if (!inBlock && !l.TrimStart().StartsWith('#') &&
                     (DisabledPatterns.Any(p => l.Contains(p, StringComparison.Ordinal)) || ForeignFilter.IsMatch(l)))
                lines[i] = "# DuoSync: отключено: " + l;
        }
        return string.Join("\n", lines);
    }
}
