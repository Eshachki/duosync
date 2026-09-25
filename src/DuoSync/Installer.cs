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
            if (!File.Exists(InstalledExe) || (!SameFile(source, InstalledExe) && !IsNewer(InstalledExe)))
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

    /// <summary>
    /// Leftovers of updates: a stale staged exe, versions that failed a week ago, downloads of installed versions.
    /// DuoSync.old.exe stays until the next update: it is what a failed version rolls back to.
    /// </summary>
    public static void CleanupLeftovers()
    {
        if (!AutoStart.IsInstalledCopy) return;
        TryDelete(Path.Combine(AutoStart.InstallDir, "DuoSync.new.exe"));
        foreach (var bad in Directory.EnumerateFiles(AutoStart.InstallDir, "DuoSync.bad-*.exe"))
            if (File.GetLastWriteTimeUtc(bad) < DateTime.UtcNow.AddDays(-7)) TryDelete(bad);
        var updates = Path.Combine(AutoStart.InstallDir, "updates");
        if (!Directory.Exists(updates)) return;
        foreach (var dir in Directory.EnumerateDirectories(updates))
            if (Version.TryParse(Path.GetFileName(dir), out var v) && v <= UpdateGuard.Current)
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
    }

    /// <summary>An old exe from Downloads must not replace a newer installed copy: it only starts that copy.</summary>
    static bool IsNewer(string installedExe)
    {
        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(installedExe);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) > UpdateGuard.Current;
    }

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
