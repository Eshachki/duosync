using System.Text.Json;

namespace DuoSync;

public sealed class ProjectEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Last remote commit already shown in a notification (one notification per commit).</summary>
    public string? NotifiedSha { get; set; }
}

/// <summary>%APPDATA%\DuoSync\settings.json. Losing it is harmless: projects are added again, the rest lives in git.</summary>
public sealed class AppSettings
{
    public string MeName { get; set; } = "";
    public string MeEmail { get; set; } = "";
    public string FriendName { get; set; } = "";
    public int PollSeconds { get; set; } = 60;
    public bool AutoStart { get; set; } = true;
    public List<ProjectEntry> Projects { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>DUOSYNC_HOME overrides the folder: tests and a second instance on one machine.</summary>
    public static string Dir => Environment.GetEnvironmentVariable("DUOSYNC_HOME") is { Length: > 0 } home
        ? home
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DuoSync");
    static string FilePath => System.IO.Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (JsonException) { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
