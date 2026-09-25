using System.Text.Json;

namespace DuoSync.Core.Ops;

/// <summary>Journal entry of the only operation that rewrites the working folder (Apply).</summary>
public sealed record ApplyJournal(string Op, string Label, string From, string To, int Pid, DateTime StartedUtc, IReadOnlyList<string> Paths);

/// <summary>
/// &lt;git-dir&gt;/duosync/op.json. Written atomically (tmp + replace + flush) before the folder is touched and
/// deleted after. The source of truth is always git; this file only says an Apply was in flight.
/// </summary>
public sealed class Journal
{
    readonly string _path;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public Journal(string duoDir) => _path = Path.Combine(duoDir, "op.json");

    public string FilePath => _path;
    public bool Exists => File.Exists(_path);

    public void Write(ApplyJournal entry)
    {
        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, entry, Json);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Returns the entry, or null when there is none or it is unreadable (<paramref name="corrupt"/> tells which).</summary>
    public ApplyJournal? Read(out bool corrupt)
    {
        corrupt = false;
        if (!File.Exists(_path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ApplyJournal>(File.ReadAllText(_path));
        }
        catch (JsonException)
        {
            corrupt = true;
            return null;
        }
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
