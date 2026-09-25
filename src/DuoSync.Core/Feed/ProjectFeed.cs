using System.Text.Json;

namespace DuoSync.Core.Feed;

public sealed record FeedEntry(DateTime Utc, string Kind, string Text);

/// <summary>Project timeline: &lt;git-dir&gt;/duosync/feed.jsonl, one JSON object per line (§2.5 «Лента»).</summary>
public sealed class ProjectFeed
{
    readonly string _path;
    readonly object _gate = new();

    public ProjectFeed(string duoDir) => _path = Path.Combine(duoDir, "feed.jsonl");

    public string FilePath => _path;

    public void Add(string kind, string text)
    {
        var line = JsonSerializer.Serialize(new FeedEntry(DateTime.UtcNow, kind, text));
        lock (_gate) File.AppendAllText(_path, line + "\n");
    }

    public IReadOnlyList<FeedEntry> ReadLast(int max)
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return Array.Empty<FeedEntry>();
            return File.ReadLines(_path).TakeLast(max)
                .Select(l => { try { return JsonSerializer.Deserialize<FeedEntry>(l); } catch (JsonException) { return null; } })
                .Where(e => e != null).Cast<FeedEntry>().ToList();
        }
    }
}
