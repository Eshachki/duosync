using DuoSync.Core.Feed;
using DuoSync.Core.Git;
using DuoSync.Core.Ops;

namespace DuoSync;

/// <summary>One project in the app: its engine, the last status and whether an operation is running.</summary>
sealed class ProjectController
{
    public ProjectEntry Entry { get; }
    public SyncEngine Engine { get; }
    public SyncOptions Options { get; }
    public ProjectStatus? Status { get; private set; }
    public bool Busy { get; private set; }
    public OpResult? LastResult { get; private set; }
    public DateTime? LastResultUtc { get; private set; }
    /// <summary>Line shown while an operation waits (e.g. repeating after a dropped connection).</summary>
    public string? Progress { get; private set; }
    /// <summary>Unfinished tasks in «Черновик», when known; the button shows the number.</summary>
    public int? TodoOpen { get; private set; }
    /// <summary>ETag of the last background count of «Черновик»: an unchanged list costs nothing to ask again.</summary>
    public string? TodoETag { get; set; }
    public event Action? Changed;

    readonly AppSettings _settings;

    public ProjectController(ProjectEntry entry, AppSettings settings)
    {
        Entry = entry;
        _settings = settings;
        var git = new GitRunner(entry.Path, settings.MeName, settings.MeEmail);
        Options = new SyncOptions
        {
            MeName = settings.MeName,
            FriendName = settings.FriendDisplay,
            Progress = line => { Progress = line; Changed?.Invoke(); },
        };
        Engine = new SyncEngine(new Repo(git), Options, new DuoSync.Core.Unity.UnityBridgeClient(entry.Path));
    }

    /// <summary>Role and resolver: the integrator merges with Claude, the other side sends merge requests.</summary>
    public void ApplyClaude(string? claudeExe, bool integrator)
    {
        Options.CanResolve = integrator;
        Options.Resolver = claudeExe != null ? new DuoSync.Core.Merge.ClaudeResolver(claudeExe, Entry.Path) : null;
    }

    public void SetTodoOpen(int count)
    {
        if (TodoOpen == count) return;
        TodoOpen = count;
        Changed?.Invoke();
    }

    public async Task RefreshAsync()
    {
        if (Busy) return;
        try { Status = await Engine.CheckStatusAsync(); }
        catch (Exception ex) { Status = new ProjectStatus(SyncState.Offline) { Error = ex.Message }; }
        Changed?.Invoke();
    }

    public async Task<OpResult> RunAsync(Func<SyncEngine, Task<OpResult>> operation)
    {
        Busy = true;
        Progress = null;
        Changed?.Invoke();
        try
        {
            LastResult = await operation(Engine);
        }
        catch (Exception ex)
        {
            LastResult = new OpResult(OpStatus.Failed, "Ошибка: " + ex.Message) { Detail = ex.ToString() };
        }
        finally
        {
            Busy = false;
            Progress = null;
        }
        LastResultUtc = DateTime.UtcNow;
        AppLog.Write($"{Entry.Name}: {LastResult.Status}: {LastResult.Message}" + (LastResult.Detail is { Length: > 0 } d ? " | " + d : ""));
        // Something went wrong: the other side sees why without asking (§6а). Offline: GitHub is out of reach anyway.
        if (_settings.ShareStatus && !LastResult.Succeeded && LastResult.Status is not (OpStatus.Offline or OpStatus.Requested))
            _ = Task.Run(() => PublishReportAsync(interactive: false));
        await RefreshAsync();
        return LastResult;
    }

    /// <summary>This computer's status report (§6а): what the other side will see.</summary>
    public Task<StatusReport> BuildReportAsync() => StatusReports.BuildAsync(Engine.Repo, _settings.MeName, UpdateGuard.Current.ToString(3),
        Options.CanResolve, LastResult, LastResultUtc, AppLog.Tail(200));

    public async Task<GitResult?> PublishReportAsync(bool interactive, StatusReport? report = null)
    {
        try
        {
            var r = await Engine.PublishStatusAsync(report ?? await BuildReportAsync(), interactive);
            AppLog.Write($"{Entry.Name}: status report " + (r.Ok ? "published" : "not published: " + r.StdErr.Trim()));
            return r;
        }
        catch (Exception e)
        {
            AppLog.Error($"{Entry.Name}: status report", e);
            return null;
        }
    }

    public async Task<IReadOnlyList<FeedEntry>> FeedAsync(int max) => new ProjectFeed(await Engine.Repo.DuoDirAsync()).ReadLast(max);
}
