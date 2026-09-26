namespace DuoSync.Core.Ops;

public enum OpStatus
{
    /// <summary>Operation finished and changed something.</summary>
    Done,
    /// <summary>Nothing to receive: the friend has sent nothing new.</summary>
    UpToDate,
    /// <summary>Nothing to send.</summary>
    NothingToSend,
    /// <summary>Stopped before touching the working folder; the message says why and what to do.</summary>
    Blocked,
    /// <summary>Real conflict; nothing applied, local work is safe in a snapshot commit.</summary>
    Conflict,
    /// <summary>
    /// Real conflict on the computer without Claude: the work went to GitHub as a merge request (§3.13)
    /// and waits for the integrator. Nothing applied, local work is safe.
    /// </summary>
    Requested,
    /// <summary>No connection to GitHub; local work is safe.</summary>
    Offline,
    /// <summary>Something failed after the start; the folder was returned to its previous state.</summary>
    Failed,
}

/// <summary>Outcome of a user-facing operation. <see cref="Message"/> is shown to the person (Russian).</summary>
public sealed record OpResult(OpStatus Status, string Message)
{
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ConflictedPaths { get; init; } = Array.Empty<string>();
    public string? Before { get; init; }
    public string? After { get; init; }
    public string? Detail { get; init; }
    /// <summary>Compilation errors Unity reported right after the files were applied.</summary>
    public IReadOnlyList<string> CompileErrors { get; init; } = Array.Empty<string>();

    public bool Succeeded => Status is OpStatus.Done or OpStatus.UpToDate or OpStatus.NothingToSend;

    public static OpResult Blocked(string message, string? detail = null) => new(OpStatus.Blocked, message) { Detail = detail };
}
