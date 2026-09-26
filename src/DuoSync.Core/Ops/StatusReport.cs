using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DuoSync.Core.Feed;
using DuoSync.Core.Git;

namespace DuoSync.Core.Ops;

/// <summary>The last operation in a status report.</summary>
public sealed record ReportOp(string Status, string Message, DateTime Utc);

/// <summary>
/// What one side's program says about itself (§6а): published by that side into its own service branch
/// <c>duosync/status/&lt;name&gt;</c>, only read by the other side. DuoSync's own state only: no project files,
/// no logs of other programs; tokens, mail addresses and the Windows user name are cleaned out.
/// </summary>
public sealed record StatusReport
{
    public string Name { get; init; } = "";
    public string App { get; init; } = "";
    /// <summary>"сводящий" or "друг".</summary>
    public string Role { get; init; } = "";
    public DateTime Utc { get; init; }
    public string? Head { get; init; }
    /// <summary>What this side last saw of main on GitHub.</summary>
    public string? Remote { get; init; }
    /// <summary>Own merge request waiting (friend's side).</summary>
    public string? Request { get; init; }
    public ReportOp? LastOp { get; init; }
    public int UnsentCount { get; init; }
    public IReadOnlyList<string> Unsent { get; init; } = Array.Empty<string>();
    /// <summary>An Apply was interrupted and not recovered yet (op.json is there).</summary>
    public bool ApplyInterrupted { get; init; }
    public bool UnityOpen { get; init; }
    public string? UnityVersion { get; init; }
    public IReadOnlyList<string> CompileErrors { get; init; } = Array.Empty<string>();
    /// <summary>The project feed, newest last.</summary>
    public IReadOnlyList<string> Feed { get; init; } = Array.Empty<string>();
    /// <summary>The tail of the program's day log, newest last.</summary>
    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();

    /// <summary>A problem worth a look: the last operation did not succeed, or an Apply is left unfinished.</summary>
    public bool HasProblem => ApplyInterrupted || CompileErrors.Count > 0 ||
                              LastOp is { Status: nameof(OpStatus.Blocked) or nameof(OpStatus.Failed) };

    /// <summary>The report as the person reads it (preview before sending, and the other side's view).</summary>
    public string ToText()
    {
        var nl = Environment.NewLine;
        var sb = new StringBuilder();
        sb.Append($"{Name} ({Role}), DuoSync {App}, отчёт от {Utc.ToLocalTime():dd.MM.yyyy HH:mm}").Append(nl);
        if (LastOp != null) sb.Append($"Последняя операция ({LastOp.Utc.ToLocalTime():dd.MM HH:mm}, {LastOp.Status}): {LastOp.Message}").Append(nl);
        if (ApplyInterrupted) sb.Append("Применение файлов было прервано и ещё не восстановлено.").Append(nl);
        sb.Append($"Не отправлено: {UnsentCount}" + (Unsent.Count > 0 ? " — " + string.Join(", ", Unsent) : "")).Append(nl);
        if (Request != null) sb.Append($"Ждёт слияния: {Request[..Math.Min(7, Request.Length)]}").Append(nl);
        sb.Append($"У себя: {Short(Head)}, main на GitHub: {Short(Remote)}").Append(nl);
        sb.Append("Unity: " + (UnityOpen ? "открыт" : "закрыт") + (UnityVersion != null ? $", версия проекта {UnityVersion}" : "")).Append(nl);
        if (CompileErrors.Count > 0) sb.Append("Ошибки компиляции:").Append(nl).Append(string.Join(nl, CompileErrors.Select(e => "  " + e))).Append(nl);
        if (Feed.Count > 0) sb.Append(nl).Append("Лента проекта:").Append(nl).Append(string.Join(nl, Feed.Select(e => "  " + e))).Append(nl);
        if (Log.Count > 0) sb.Append(nl).Append("Журнал программы:").Append(nl).Append(string.Join(nl, Log.Select(e => "  " + e))).Append(nl);
        return sb.ToString();
    }

    static string Short(string? sha) => sha == null ? "—" : sha[..Math.Min(7, sha.Length)];
}

/// <summary>Builds, publishes and reads status reports (§6а).</summary>
public static class StatusReports
{
    public const string BranchPrefix = "refs/heads/duosync/status/";
    public const string FileName = "status.json";
    const int MaxBytes = 256 * 1024;
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Readable Cyrillic: the report can be opened on GitHub as it is.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    public static string BranchRef(string name) => BranchPrefix + SyncEngine.Slug(name);

    static readonly Regex[] Secrets =
    {
        new(@"\beyJ[A-Za-z0-9_-]{8,}(\.[A-Za-z0-9_-]+){0,2}"),                // JWT-like tokens
        new(@"\b(gh[opusr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})"),   // GitHub tokens
        new(@"\bsk-[A-Za-z0-9_-]{20,}"),                                          // API keys
        new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}"),                   // mail addresses
        new(@"(?i)(password|passwd|token|secret)\s*[=:]\s*\S+"),
        new(@"(?i)https?://[^/\s:@]+:[^/\s@]+@"),                                 // credentials in URLs
    };

    /// <summary>Removes secrets, mail addresses and the Windows user name from one line.</summary>
    public static string Clean(string text, string? userName = null)
    {
        foreach (var re in Secrets) text = re.Replace(text, "[скрыто]");
        userName ??= Environment.UserName;
        if (userName.Length >= 3) text = Regex.Replace(text, Regex.Escape(userName), "[пользователь]", RegexOptions.IgnoreCase);
        return text;
    }

    /// <summary>Collects the report of this computer. <paramref name="log"/> is the tail of the program's log.</summary>
    public static async Task<StatusReport> BuildAsync(Repo repo, string name, string app, bool integrator, OpResult? last, DateTime? lastUtc,
        IEnumerable<string> log, int feedLines = 40)
    {
        var duo = await repo.DuoDirAsync();
        var unsent = new List<string>();
        try
        {
            foreach (var e in await repo.StatusAsync(background: true))
                if (e.Kind != StatusKind.Ignored) unsent.Add(e.Path);
        }
        catch (GitException) { }
        var report = new StatusReport
        {
            Name = name,
            App = app,
            Role = integrator ? "сводящий" : "друг",
            Utc = DateTime.UtcNow,
            Head = await repo.HeadAsync(),
            Remote = await repo.RevParseAsync(repo.RemoteBranchRef),
            Request = await repo.ReadRefAsync(SyncEngine.RequestRef),
            LastOp = last == null ? null : new ReportOp(last.Status.ToString(), Clean(last.Message), lastUtc ?? DateTime.UtcNow),
            UnsentCount = unsent.Count,
            Unsent = unsent.Take(20).Select(p => Clean(p)).ToList(),
            ApplyInterrupted = new Journal(duo).Exists,
            UnityOpen = Unity.UnityDetector.IsProjectOpen(repo.Root),
            UnityVersion = Merge.YamlMergeTool.ProjectVersion(repo.Root),
            CompileErrors = (last?.CompileErrors ?? Array.Empty<string>()).Take(20).Select(e => Clean(e)).ToList(),
            Feed = new ProjectFeed(duo).ReadLast(feedLines).Select(e => Clean($"{e.Utc.ToLocalTime():dd.MM HH:mm} {e.Text}")).ToList(),
            Log = log.Select(l => Clean(l)).ToList(),
        };
        return report;
    }

    /// <summary>UTF-8 JSON not larger than 256 KB: the log is cut first, then the feed.</summary>
    public static byte[] Serialize(StatusReport report)
    {
        while (true)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(report, Json);
            if (bytes.Length <= MaxBytes) return bytes;
            if (report.Log.Count > 0) report = report with { Log = report.Log.Skip(Math.Max(1, report.Log.Count / 2)).ToList() };
            else if (report.Feed.Count > 0) report = report with { Feed = report.Feed.Skip(Math.Max(1, report.Feed.Count / 2)).ToList() };
            else return bytes;
        }
    }

    /// <summary>
    /// One parentless commit with status.json, pushed over this person's own status branch (it only ever holds the
    /// latest report). Main and the working folder are not touched.
    /// </summary>
    public static async Task<GitResult> PublishAsync(Repo repo, StatusReport report, bool interactive, CancellationToken ct = default)
    {
        var blob = (await repo.Git.RunCheckedAsync(new[] { "hash-object", "-w", "--stdin" },
            new GitRunOptions { StdIn = Serialize(report) })).StdOutTrimmed;
        var tree = (await repo.Git.RunCheckedAsync(new[] { "mktree", "-z" },
            new GitRunOptions { StdIn = Encoding.UTF8.GetBytes($"100644 blob {blob}\t{FileName}\0") })).StdOutTrimmed;
        var commit = (await repo.Git.RunCheckedAsync(new[] { "commit-tree", tree, "-F", "-" },
            new GitRunOptions { StdIn = Encoding.UTF8.GetBytes($"Отчёт DuoSync: {report.Name}\n\nDuoSync: status\n") })).StdOutTrimmed;
        var branch = BranchRef(report.Name);
        return await repo.Git.RunAsync(new[] { "push", "--porcelain", repo.Remote, $"+{commit}:{branch}" },
            new GitRunOptions { Timeout = TimeSpan.FromMinutes(2), NonInteractive = !interactive }, ct);
    }

    /// <summary>
    /// The other side's latest report, from an ls-remote listing of <c>refs/heads/duosync/*</c>: fetched only when
    /// it changed. Null when the other side has published nothing (or it does not parse).
    /// </summary>
    public static async Task<StatusReport?> ReadPeerAsync(Repo repo, string myName, IReadOnlyDictionary<string, string> remoteRefs, CancellationToken ct = default)
    {
        var mine = BranchRef(myName);
        StatusReport? newest = null;
        foreach (var (name, sha) in remoteRefs)
        {
            if (!name.StartsWith(BranchPrefix, StringComparison.Ordinal) || name == mine) continue;
            var local = $"refs/remotes/{repo.Remote}/{name["refs/heads/".Length..]}";
            if (await repo.ReadRefAsync(local) != sha)
            {
                var fetch = await repo.Git.RunAsync(new[] { "fetch", "--no-tags", repo.Remote, $"+{name}:{local}" },
                    new GitRunOptions { Timeout = TimeSpan.FromMinutes(2), NonInteractive = true }, ct);
                if (!fetch.Ok) continue;
            }
            var blob = await repo.Git.RunAsync("cat-file", "blob", $"{local}:{FileName}");
            if (!blob.Ok) continue;
            try
            {
                var report = JsonSerializer.Deserialize<StatusReport>(blob.StdOutBytes);
                if (report != null && (newest == null || report.Utc > newest.Utc)) newest = report;
            }
            catch (JsonException) { }
        }
        return newest;
    }
}
