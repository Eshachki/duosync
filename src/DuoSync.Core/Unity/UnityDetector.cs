namespace DuoSync.Core.Unity;

/// <summary>
/// Is the project open in the Unity Editor? The editor keeps Temp/UnityLockfile locked from ~0.6 s after start
/// (decisions E8); a stale unlocked file means Unity is closed or crashed.
/// </summary>
public static class UnityDetector
{
    public static bool IsProjectOpen(string projectRoot)
    {
        var lockFile = Path.Combine(projectRoot, "Temp", "UnityLockfile");
        if (!File.Exists(lockFile)) return false;
        try
        {
            using var fs = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
