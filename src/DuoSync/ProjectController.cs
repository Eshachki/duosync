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
    /// <summary>Line shown while an operation waits (e.g. repeating after a dropped connection).</summary>
    public string? Progress { get; private set; }
    /// <summary>Unfinished tasks in «Черновик», when known; the button shows the number.</summary>
    public int? TodoOpen { get; private set; }
    /// <summary>ETag of the last background count of «Черновик»: an unchanged list costs nothing to ask again.</summary>
    public string? TodoETag { get; set; }
    public event Action? Changed;

    public ProjectController(ProjectEntry entry, AppSettings settings)
    {
        Entry = entry;
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
        AppLog.Write($"{Entry.Name}: {LastResult.Status}: {LastResult.Message}" + (LastResult.Detail is { Length: > 0 } d ? " | " + d : ""));
        await RefreshAsync();
        return LastResult;
    }

    public async Task<IReadOnlyList<FeedEntry>> FeedAsync(int max) => new ProjectFeed(await Engine.Repo.DuoDirAsync()).ReadLast(max);
}
