using System.Text.Json;

namespace DuoSync.Core.Unity;

/// <summary>Mirror of the bridge's JsonUtility classes (field names must match).</summary>
public sealed class BridgeStateDto
{
    public string? helper;
    public int pid;
    public string? unity;
    public long bgMs;
    public long mainMs;
    public string? phase;
    public bool playing;
    public bool compiling;
    public bool updating;
    public bool compileFailed;
    public string[]? errors;
    public bool parked;
    public string[]? guard;
    public string? currentOp;
}

public sealed class BridgeResponseDto
{
    public string? id;
    public bool ok;
    public string? error;
    public string[]? compileErrors;
    public int missingScripts;
    public string[]? reopened;
}

/// <summary>
/// Talks to the bridge package through files in Library/DuoSync (§4.2): cmd-&lt;id&gt;.json → res-&lt;id&gt;.json.
/// While scenes are parked a lease is kept fresh so the bridge's watchdog knows the app is alive.
/// </summary>
public sealed class UnityBridgeClient : IUnityBridge
{
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true, PropertyNameCaseInsensitive = true };

    readonly string _root;
    Timer? _leaseTimer;

    public UnityBridgeClient(string projectRoot) => _root = projectRoot;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>How long to wait for a heartbeat when Unity is open (start-up, domain reload).</summary>
    public TimeSpan HeartbeatWait { get; init; } = TimeSpan.FromSeconds(20);

    string Dir => Path.Combine(_root, "Library", "DuoSync");

    public bool IsOpen => UnityDetector.IsProjectOpen(_root);

    public BridgeStateDto? ReadState()
    {
        var path = Path.Combine(Dir, "state.json");
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<BridgeStateDto>(fs, Json);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    bool HeartbeatFresh(out BridgeStateDto? state, long maxAgeMs = 5000)
    {
        state = ReadState();
        return state != null && NowMs - state.bgMs < maxAgeMs;
    }

    public Task<BridgeResult> SaveAsync(CancellationToken ct) => SendAsync("save", null, TimeSpan.FromMinutes(5), ct);

    public async Task<BridgeResult> ParkAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        StartLease();
        var r = await SendAsync("park", paths, TimeSpan.FromMinutes(2), ct);
        if (!r.Ok) StopLease();
        return r;
    }

    public async Task<BridgeResult> FinishAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        try { return await SendAsync("finish", paths, TimeSpan.FromMinutes(15), ct); }
        finally { StopLease(); }
    }

    public Task<BridgeResult> StopPlayAsync(CancellationToken ct) => SendAsync("stopPlay", null, TimeSpan.FromMinutes(1), ct);

    async Task<BridgeResult> SendAsync(string op, IReadOnlyList<string>? paths, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + HeartbeatWait;
        while (!HeartbeatFresh(out _))
        {
            if (DateTime.UtcNow > deadline)
                return new BridgeResult(false,
                    "Unity открыт, но мост DuoSync не отвечает. Если мост ещё не установлен — нажми «Подготовить проект»; " +
                    "если Unity завис или открыт в Safe Mode — перезапусти Unity.");
            await Task.Delay(500, ct);
        }

        Directory.CreateDirectory(Dir);
        var id = Guid.NewGuid().ToString("N");
        var cmd = JsonSerializer.Serialize(new { id, op, paths = paths ?? Array.Empty<string>(), createdUtcMs = NowMs });
        var cmdPath = Path.Combine(Dir, $"cmd-{id}.json");
        await File.WriteAllTextAsync(cmdPath + ".tmp", cmd, ct);
        File.Move(cmdPath + ".tmp", cmdPath, overwrite: true);

        var resPath = Path.Combine(Dir, $"res-{id}.json");
        var until = DateTime.UtcNow + timeout;
        long lastAlive = NowMs;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(resPath))
            {
                BridgeResponseDto? res = null;
                try
                {
                    res = JsonSerializer.Deserialize<BridgeResponseDto>(await File.ReadAllTextAsync(resPath, ct), Json);
                    File.Delete(resPath);
                }
                catch (Exception e) when (e is IOException or JsonException) { await Task.Delay(PollInterval, ct); continue; }
                return new BridgeResult(res?.ok == true, string.IsNullOrEmpty(res?.error) ? null : res!.error)
                {
                    CompileErrors = res?.compileErrors ?? Array.Empty<string>(),
                };
            }
            // The background heartbeat keeps going during long imports; a domain reload pauses it for a few seconds.
            if (HeartbeatFresh(out _, 5000)) lastAlive = NowMs;
            else if (NowMs - lastAlive > 120_000)
            {
                TryDelete(cmdPath);
                return new BridgeResult(false, "Unity перестал отвечать. Проверь, не открыт ли в Unity диалог, и повтори.");
            }
            await Task.Delay(PollInterval, ct);
        }
        TryDelete(cmdPath);
        return new BridgeResult(false, $"Unity не ответил за {timeout.TotalMinutes:0} мин. Проверь окно Unity и повтори.");
    }

    void StartLease()
    {
        WriteLease();
        _leaseTimer?.Dispose();
        _leaseTimer = new Timer(_ => WriteLease(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    void StopLease()
    {
        _leaseTimer?.Dispose();
        _leaseTimer = null;
        TryDelete(Path.Combine(Dir, "lease.json"));
    }

    void WriteLease()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, "lease.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new { pid = Environment.ProcessId, untilUtcMs = NowMs + 60_000 }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (IOException) { }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }
}
