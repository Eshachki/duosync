using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DuoSync.Core.Git;

/// <summary>
/// Turns git's progress lines on stderr (with <c>--progress</c>) and git-lfs's progress file (GIT_LFS_PROGRESS)
/// into short Russian lines for the window: what is happening, how far, how fast. Updates are thinned out.
/// </summary>
public sealed class GitProgress
{
    // "Writing objects:  45% (123/456), 10.00 MiB | 1.20 MiB/s"
    static readonly Regex GitLine = new(@"^(?:remote: )?(?<what>[A-Za-z ]+?):\s+(?<pct>\d+)% \((?<cur>\d+)/(?<tot>\d+)\)(?:, (?<size>[\d.]+ [KMG]?i?B)(?: \| (?<speed>[\d.]+ [KMG]?i?B/s))?)?");
    // "upload 450/1019 2048/4096 Assets/Art/a.png": file number of all files, bytes of this file, its name.
    static readonly Regex LfsLine = new(@"^(?<dir>\w+) (?<cur>\d+)/(?<tot>\d+) \d+/\d+ ");

    readonly Action<string> _report;
    readonly Stopwatch _since = Stopwatch.StartNew();
    readonly object _gate = new();
    string? _last;
    long _lastAt = -10_000;

    public GitProgress(Action<string> report) => _report = report;

    /// <summary>Reads all of stderr, reporting progress lines on the way; the other lines are returned for error messages.</summary>
    public async Task<string> ReadStdErrAsync(StreamReader stderr)
    {
        var rest = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[4096];
        int n;
        while ((n = await stderr.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n')
                {
                    Flush(line, rest);
                    if (c == '\n' && rest.Length > 0 && rest[^1] != '\n') rest.Append('\n');
                }
                else line.Append(c);
            }
        }
        Flush(line, rest);
        return rest.ToString();
    }

    void Flush(StringBuilder line, StringBuilder rest)
    {
        if (line.Length == 0) return;
        var text = line.ToString();
        line.Clear();
        if (!OnGitLine(text)) rest.Append(text);
    }

    /// <summary>Reads git-lfs's progress file once a second until cancelled.</summary>
    public async Task WatchLfsAsync(string path, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            var last = LastLine(path);
            if (last != null && DescribeLfs(last) is { } text) Report(text, final: false);
        }
    }

    static string? LastLine(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(Math.Max(0, fs.Length - 2048), SeekOrigin.Begin);
            var text = new StreamReader(fs, Encoding.UTF8).ReadToEnd();
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.TrimEnd('\r');
        }
        catch (IOException) { return null; }
    }

    public static string? DescribeLfs(string line)
    {
        var m = LfsLine.Match(line);
        if (!m.Success) return null;
        var what = m.Groups["dir"].Value switch
        {
            "upload" => "Выгружаю файлы (картинки, модели, звуки)",
            "download" or "checkout" => "Скачиваю файлы (картинки, модели, звуки)",
            _ => null,
        };
        return what == null ? null : $"{what}: {m.Groups["cur"].Value} из {m.Groups["tot"].Value}";
    }

    /// <summary>The window's line for one git progress line, or null when it is not one.</summary>
    public static string? DescribeGit(string line, out bool final)
    {
        final = false;
        var m = GitLine.Match(line);
        if (!m.Success) return null;
        var what = m.Groups["what"].Value.Trim() switch
        {
            "Enumerating objects" or "Counting objects" or "Compressing objects" => "Готовлю отправку",
            "Writing objects" => "Отправляю проект",
            "Receiving objects" => "Скачиваю проект",
            "Resolving deltas" => "Распаковываю",
            "Updating files" or "Checking out files" => "Раскладываю файлы",
            "Filtering content" => "Скачиваю файлы (картинки, модели, звуки)",
            _ => "",
        };
        if (what.Length == 0) return "";
        var text = $"{what}: {m.Groups["pct"].Value}%";
        if (m.Groups["size"].Success) text += ", " + Ru(m.Groups["size"].Value);
        if (m.Groups["speed"].Success) text += ", " + Ru(m.Groups["speed"].Value);
        final = m.Groups["pct"].Value == "100";
        return text;
    }

    /// <summary>True when the line was a progress line: it stays out of the error text.</summary>
    bool OnGitLine(string line)
    {
        var text = DescribeGit(line, out var final);
        if (text == null) return false;
        if (text.Length > 0) Report(text, final);
        return true;
    }

    static string Ru(string size) => size.Replace("GiB", "ГБ").Replace("MiB", "МБ").Replace("KiB", "КБ").Replace("bytes", "Б").Replace("/s", "/с");

    void Report(string text, bool final)
    {
        lock (_gate)
        {
            var now = _since.ElapsedMilliseconds;
            if (text == _last || (!final && now - _lastAt < 500)) return;
            _last = text;
            _lastAt = now;
        }
        _report(text);
    }
}
