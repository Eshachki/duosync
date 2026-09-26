namespace DuoSync.Core.Setup;

/// <summary>Managed blocks and files DuoSync puts into a project (§8.2, §9в, §11.3–11.4).</summary>
public static class Templates
{
    public const string BlockStart = "# >>> DuoSync";
    public const string BlockEnd = "# <<< DuoSync";
    public const string MdStart = "<!-- duosync:begin -->";
    public const string MdEnd = "<!-- duosync:end -->";

    public static readonly string[] LfsExtensions =
    {
        "png", "jpg", "jpeg", "tga", "psd", "tif", "tiff", "exr", "hdr", "bmp", "gif", "webp",
        "fbx", "obj", "blend", "glb", "dae",
        "wav", "mp3", "ogg", "aif", "aiff", "flac", "m4a",
        "mp4", "mov", "webm",
        "ttf", "otf", "zip", "7z", "dll", "so", "a", "bytes",
    };

    /// <summary>Unity YAML types whose merge driver must be plain text inside DuoSync (decisions B4).</summary>
    public static readonly string[] UnityYamlExtensions =
    {
        "unity", "prefab", "asset", "mat", "anim", "controller", "overrideController", "mask", "playable", "physicMaterial",
        "physicsMaterial2D", "lighting", "preset", "signal", "mixer", "spriteatlas", "spriteatlasv2", "terrainlayer",
        "renderTexture", "fontsettings", "guiskin", "brush", "shadervariants", "scenetemplate", "meta",
    };

    public static string GitAttributesBlock(IEnumerable<string> binaryAssetPaths)
    {
        var lines = new List<string>
        {
            BlockStart + " (управляется программой; свои строки пишите вне блока)",
            "* text=auto eol=lf",
            "*.bat text eol=crlf",
            "*.cmd text eol=crlf",
            "*.ps1 text eol=crlf",
            "*.cs text eol=lf diff=csharp",
            "*.meta text eol=lf",
            "*.md text eol=lf merge=union",
            "/.gitattributes merge=union",
            "/.gitignore merge=union",
            "[attr]lfs filter=lfs diff=lfs merge=binary -text",
        };
        lines.AddRange(LfsExtensions.Select(e => $"*.{e} lfs"));
        lines.Add("**/LightingData.asset lfs");
        lines.Add("/Packages/packages-lock.json merge=binary");
        lines.Add("/ProjectSettings/ProjectVersion.txt merge=binary");
        lines.Add("# бинарные .asset без %YAML (дописывает программа):");
        lines.AddRange(binaryAssetPaths.Select(p => "/" + EscapeAttrPath(p) + " lfs"));
        lines.Add(BlockEnd);
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// Everything a Unity project must not share, in one managed block that applies whatever the project's own
    /// .gitignore says (the github/gitignore Unity template, IDE, builds, OS junk, AI tools, the people's conventions).
    /// Order matters: the Assets exceptions come after the global debug-file rules and before the Assets dumps.
    /// </summary>
    public static string GitIgnoreBlock() => string.Join("\n", new[]
    {
        BlockStart,
        "# Папки, которые Unity создаёт сам",
        "/[Ll]ibrary/", "/[Tt]emp/", "/[Oo]bj/", "/[Ll]ogs/", "/[Uu]ser[Ss]ettings/", "/[Mm]emoryCaptures/", "/[Rr]ecordings/",
        "/[Pp]rofiler[Cc]aptures/", "/[Bb]uild[Rr]eports/", "/[Cc]ode[Cc]overage/", ".utmp/",
        "# Сборки",
        "/[Bb]uild/", "/[Bb]uilds/", "*.apk", "*.aab", "*.app", "*.ipa", "*.apks", "*.symbols.zip",
        "*_BurstDebugInformation_DoNotShip/", "*_BackUpThisFolder_ButDontShipItWithYourGame/",
        "crashlytics-build.properties", "/ServerData/",
        "/[Aa]ssets/[Ss]treaming[Aa]ssets/aa/", "/[Aa]ssets/[Ss]treaming[Aa]ssets/aa.meta",
        "/[Aa]ssets/AddressableAssetsData/link.xml*", "/[Aa]ssets/Addressables_Temp*", "/[Aa]ssets/AddressableAssetsData/*/*.bin*",
        "/[Aa]ssets/Unity.VisualScripting.Generated/VisualScripting.Flow/UnitOptions.db*",
        "/[Aa]ssets/Unity.VisualScripting.Generated/VisualScripting.Core/Property Providers*",
        "/[Aa]ssets/[Ii]nit[Tt]est[Ss]cene*.unity*",
        "# Редакторы кода: файлы проекта у каждого свои",
        "*.csproj", "*.unityproj", "*.sln", "*.slnx", "*.suo", "*.user", "*.userprefs", "*.pidb", "*.booproj", "*.svd",
        "*.opendb", "*.VC.db", "*.DotSettings.user", ".vs/", ".vscode/", ".idea/", ".gradle/", ".consulo/", "ExportedObj/",
        "/[Aa]ssets/Plugins/Editor/JetBrains*",
        "# Отладочные файлы; у плагинов внутри Assets они законные",
        "*.pdb", "*.mdb", "*.pidb.meta", "*.pdb.meta", "*.mdb.meta",
        "!/[Aa]ssets/**/*.pdb", "!/[Aa]ssets/**/*.pdb.meta", "!/[Aa]ssets/**/*.mdb", "!/[Aa]ssets/**/*.mdb.meta",
        "# Мусор системы, git и программ",
        "*.log", "*.tmp", "*.blend1", "*.blend1.meta", "*.stackdump", "*.dmp", "sysinfo.txt", "mono_crash.*",
        ".DS_Store", "._*", "Thumbs.db", "ehthumbs.db", "[Dd]esktop.ini", "$RECYCLE.BIN/",
        "*.orig", "*.BACKUP.*", "*.BASE.*", "*.LOCAL.*", "*.REMOTE.*", ".env",
        "/.plastic/", "/ignore.conf", "/.collabignore",
        "# Покупные пакеты и свалки людей: IGNORE_FOLDER вместе с .meta, иначе Unity создаст его заново и он уедет один",
        "*.unitypackage", "*.unitypackage.meta",
        "/[Aa]ssets/IGNORE_FOLDER/", "/[Aa]ssets/IGNORE_FOLDER.meta", "/TRASH/", "/web/",
        "/Assets/_Recovery/", "/Assets/_Recovery.meta", "/Assets/_Local/", "/Assets/_Local.meta",
        "# Локальные настройки нейросетей и MCP",
        "/.duosync/", "/.mcp.json", "/opencode.json", "/.opencode/", "/.cline/", "/.claude/settings.local.json",
        "/.claude/skills/*", "!/.claude/skills/project-*/", "/.agents/skills/*",
        BlockEnd,
    }) + "\n";

    /// <summary>
    /// Start of .gitignore for a project that has none; everything that matters is in <see cref="GitIgnoreBlock"/>.
    /// </summary>
    public const string BaseGitIgnore = @"# Свои правила пишите здесь, над блоком DuoSync.

# Покупные паки, которые импортируются целиком ради пары вещей, в истории не держим:
# /[Aa]ssets/ИмяПака/
# /[Aa]ssets/ИмяПака.meta
";

    /// <summary>.git/info/attributes (local, highest priority): plain-text merge for Unity YAML, binary merge for LFS.</summary>
    public static string InfoAttributesBlock(IEnumerable<string> binaryAssetPaths)
    {
        var lines = new List<string> { BlockStart + " (локально; отключает чужие merge-драйверы вроде Unity Smart Merge)" };
        lines.AddRange(UnityYamlExtensions.Select(e => $"*.{e} merge=text"));
        lines.AddRange(LfsExtensions.Select(e => $"*.{e} merge=binary"));
        lines.Add("**/LightingData.asset merge=binary");
        lines.AddRange(binaryAssetPaths.Select(p => "/" + EscapeAttrPath(p) + " merge=binary"));
        lines.Add("/Packages/packages-lock.json merge=binary");
        lines.Add("/ProjectSettings/ProjectVersion.txt merge=binary");
        lines.Add(BlockEnd);
        return string.Join("\n", lines) + "\n";
    }

    public static string AgentsSection(string me, string friend) => $@"{MdStart}
# Правила для нейросетей в этом проекте

Проект делают двое: {me} и {friend}, каждый на своём компьютере. Изменениями обменивается программа DuoSync, и только когда человек нажимает «Получить» или «Отправить». Ты в обмен не вмешиваешься.

## Перед началом работы
- Прочитай `.duosync/status.md`. Если там есть входящие обновы, попроси человека нажать «Получить» и подожди, прежде чем править перечисленные файлы.

## Git — только смотреть
- Можно: `git status`, `git diff`, `git log`, `git show`.
- Нельзя ни в какой форме: commit, push, pull, fetch, merge, rebase, reset, checkout, switch, restore, stash, clean, revert, rm, mv, lfs, создавать ветки. Неудачную правку исправь заново или попроси человека.
- Не трогай `.gitattributes`, `.gitignore`, `.duosync.json` и `Packages/com.duosync.bridge/`.

## Ассеты Unity
- Создавай, переименовывай, перемещай и удаляй ассеты только через Unity (инструменты Unity-MCP или AssetDatabase), не через файловую систему. Не копируй папки мимо Unity — будут дубли GUID.
- Никогда не создавай, не правь и не удаляй `.meta`-файлы и не меняй GUID.
- Новый `.cs` можно записать файлом, но после этого обнови ассеты в Unity, чтобы появился `.meta`.
- После «Получить» файлы из твоего контекста могли устареть: перечитай файл перед тем, как писать его целиком.
- Не переименовывай поля, которые Unity сериализует (public и [SerializeField]). Если очень нужно — добавь [FormerlySerializedAs(""старое"")].
- Меняй точечно: не переформатируй файлы целиком, не пересохраняй всё подряд, не делай массовый Reimport.
- Без прямой просьбы не меняй ProjectSettings, Packages/manifest.json и версию Unity, не удаляй ассеты.
- Опыты и временные ассеты — только в `Assets/_Local/` (эта папка не уходит другу).

## Зоны (заполняют люди)
| Сцена / папка | Хозяин |
|---|---|
| | {me} |
| | {friend} |

Чужую зону — только по прямой просьбе своего человека.

## Перед словом «готово»
- Сохрани сцены и ассеты.
- В консоли Unity 0 ошибок компиляции, нет новых Missing Script.
- Напиши 1–3 строки «что сделано и зачем» — человек вставит их в поле «Что сделал» и нажмёт «Отправить».
{MdEnd}
";

    public const string ClaudeSection = MdStart + "\n@AGENTS.md\n" + MdEnd + "\n";

    public static string DuoSyncJson(string me, string friend, string appVersion, string bridgeVersion) => $@"{{
  ""schema"": 1,
  ""branch"": ""main"",
  ""minApp"": ""{appVersion}"",
  ""bridge"": ""{bridgeVersion}"",
  ""members"": [""{me}"", ""{friend}""]
}}
";

    /// <summary>gitattributes path pattern: spaces must be written as [[:space:]].</summary>
    public static string EscapeAttrPath(string path) => path.Replace(" ", "[[:space:]]");
}
