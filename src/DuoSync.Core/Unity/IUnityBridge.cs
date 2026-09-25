namespace DuoSync.Core.Unity;

public sealed record BridgeResult(bool Ok, string? Error = null)
{
    public IReadOnlyList<string> CompileErrors { get; init; } = Array.Empty<string>();
    public static readonly BridgeResult Success = new(true);
}

/// <summary>
/// What the operations need from the open Unity Editor (§4.3, decisions E). When Unity is closed the
/// operations work without it: <see cref="NoUnity"/>.
/// </summary>
public interface IUnityBridge
{
    bool IsOpen { get; }
    /// <summary>Saves scenes and assets; refuses in Play Mode, dirty Prefab Mode, untitled dirty scene, unsaved tool windows.</summary>
    Task<BridgeResult> SaveAsync(CancellationToken ct);
    /// <summary>Closes open scenes among <paramref name="paths"/>, disallows auto refresh, releases file handles.</summary>
    Task<BridgeResult> ParkAsync(IReadOnlyList<string> paths, CancellationToken ct);
    /// <summary>Allows refresh, imports, waits for compilation, reopens the closed scenes by GUID.</summary>
    Task<BridgeResult> FinishAsync(IReadOnlyList<string> paths, CancellationToken ct);
}

public sealed class NoUnity : IUnityBridge
{
    public static readonly NoUnity Instance = new();
    public bool IsOpen => false;
    public Task<BridgeResult> SaveAsync(CancellationToken ct) => Task.FromResult(BridgeResult.Success);
    public Task<BridgeResult> ParkAsync(IReadOnlyList<string> paths, CancellationToken ct) => Task.FromResult(BridgeResult.Success);
    public Task<BridgeResult> FinishAsync(IReadOnlyList<string> paths, CancellationToken ct) => Task.FromResult(BridgeResult.Success);
}
