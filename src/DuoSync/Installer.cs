namespace DuoSync;

/// <summary>
/// Zero-effort install: the first start from anywhere (Downloads, Desktop, a zip from Telegram) copies the exe to
/// %LOCALAPPDATA%\DuoSync, starts it from there and exits; that copy registers autostart. Starting a newer exe the same
/// way replaces a running older copy (the running exe can be renamed but not overwritten, decisions F3).
/// Lab instances (DUOSYNC_HOME) and dev builds (…\bin\…) never install.
/// </summary>
static class Installer
{
    public static string InstalledExe => Path.Combine(AutoStart.InstallDir, "DuoSync.exe");

    static bool IsDevBuild => Environment.ProcessPath is { } exe &&
                              exe.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static bool ShouldInstall =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DUOSYNC_HOME")) && !IsDevBuild && !AutoStart.IsInstalledCopy;

    /// <summary>Returns true when this process handed over to the installed copy and must exit.</summary>
    public static bool InstallAndRelaunch(string[] args)
    {
        if (!ShouldInstall) return false;
        var source = Environment.ProcessPath!;
        try
        {
            Directory.CreateDirectory(AutoStart.InstallDir);
            if (!File.Exists(InstalledExe) || !SameFile(source, InstalledExe))
            {
                SingleInstance.AskRunningToQuit(TimeSpan.FromSeconds(8));
                if (File.Exists(InstalledExe))
                {
                    var old = Path.Combine(AutoStart.InstallDir, "DuoSync.old.exe");
                    TryDelete(old);
                    File.Move(InstalledExe, old); // allowed even while that exe is still running
                }
                File.Copy(source, InstalledExe);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Не получилось установить DuoSync в " + AutoStart.InstallDir + ": " + e.Message +
                            "\nПрограмма запустится отсюда, но без автозапуска.", "DuoSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(InstalledExe) { UseShellExecute = false, Arguments = string.Join(' ', args) });
        return true;
    }

    /// <summary>Left over from a replacement: deletable once the old process has exited.</summary>
    public static void CleanupOldCopy() => TryDelete(Path.Combine(AutoStart.InstallDir, "DuoSync.old.exe"));

    static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sa)) == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sb));
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
