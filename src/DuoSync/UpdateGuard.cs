using System.Diagnostics;
using System.Text.Json;
using DuoSync.Core.Updates;

namespace DuoSync;

/// <summary>
/// Installs a downloaded update and makes sure it runs (§8а). The idle old copy stages and swaps the exe (a running exe
/// can be renamed), hands over the single-instance mutex, starts the new copy and waits for its «ok» in update.json.
/// A version that crashes, quits or hangs goes back to the previous exe and is never offered again.
/// The update.json format and the --after-update argument are a contract between all versions: only add fields.
/// </summary>
static class UpdateGuard
{
    sealed class State
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        /// <summary>starting | ok | rolledback | failed</summary>
        public string Status { get; set; } = "";
        public int Starts { get; set; }
        public int WatchdogPid { get; set; }
        public string? Reason { get; set; }
        public bool Announced { get; set; }
        public List<string> Notes { get; set; } = new();
        public List<string> Bad { get; set; } = new();
        public DateTime Utc { get; set; }
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Only the installed copy updates itself: lab instances and dev builds never do.</summary>
    public static bool Enabled => AutoStart.IsInstalledCopy;

    public static Version Current => typeof(UpdateGuard).Assembly.GetName().Version is { } v
        ? new Version(v.Major, v.Minor, Math.Max(0, v.Build))
        : new Version(0, 0, 0);

    static string CurrentText => Current.ToString(3);
    static string FilePath => Path.Combine(AutoStart.InstallDir, "update.json");
    static string OldExe => Path.Combine(AutoStart.InstallDir, "DuoSync.old.exe");

    static State Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath)) ?? new State();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new State();
    }

    static void Save(State s)
    {
        s.Utc = DateTime.UtcNow;
        Directory.CreateDirectory(AutoStart.InstallDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static bool IsBad(Version v) => Load().Bad.Contains(v.ToString(3));

    /// <summary>
    /// First line of Main. Counts starts of a fresh update and rolls back a version that never confirmed, e.g. after
    /// a reboot killed the watchdog. A crash of a fresh version without a watchdog rolls back at once.
    /// </summary>
    public static void OnStartup()
    {
        if (!Enabled) return;
        var s = Load();
        if (s.Status != "starting" || s.To != CurrentText) return;
        s.Starts++;
        Save(s);
        AppLog.Write($"update: start {s.Starts} of fresh {s.To}");
        if (s.Starts >= 3) RollBack(s, "не запускается", exit: true);
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            var st = Load();
            if (st.Status == "starting" && st.To == CurrentText && !IsAlive(st.WatchdogPid)) RollBack(st, "упала при запуске", exit: false);
        };
    }

    /// <summary>The new copy runs its message loop: the watchdog may exit. Returns the release notes once.</summary>
    public static (string Version, IReadOnlyList<string> Notes)? ConfirmStarted()
    {
        if (!Enabled) return null;
        var s = Load();
        if (s.To != CurrentText) return null;
        if (s.Status == "starting")
        {
            s.Status = "ok";
            Save(s);
            AppLog.Write($"update: {s.From} -> {s.To} ok");
        }
        if (s.Status != "ok" || s.Announced) return null;
        s.Announced = true;
        Save(s);
        return (s.To, s.Notes);
    }

    /// <summary>After a rollback the previous version says once why it is still here.</summary>
    public static string? TakeRollbackNotice()
    {
        if (!Enabled) return null;
        var s = Load();
        if (s.Status != "rolledback" || s.Announced || s.From != CurrentText) return null;
        s.Announced = true;
        Save(s);
        return $"Обновление {s.To} не запустилось ({s.Reason}). Осталась версия {CurrentText}, программа подождёт исправленную.";
    }

    /// <summary>
    /// Old copy, idle. Stages and swaps the exe, then <paramref name="handOver"/> frees the mutex and the tray,
    /// starts the new copy and watches it. Throws only before the swap is complete, with the working exe untouched;
    /// after it returns the caller exits (the new version runs, or the previous one was restarted).
    /// </summary>
    public static async Task InstallAsync(string downloadedExe, ReleaseInfo release, bool showWindow, Action handOver)
    {
        var installed = Installer.InstalledExe;
        var staged = Path.Combine(AutoStart.InstallDir, "DuoSync.new.exe");
        File.Copy(downloadedExe, staged, overwrite: true);
        if (await ReleaseFeed.Sha256Async(staged) != release.Sha256)
        {
            File.Delete(staged);
            throw new InvalidDataException("копия обновления повреждена");
        }
        if (File.Exists(OldExe)) File.Delete(OldExe);
        File.Move(installed, OldExe); // allowed while this very exe runs
        try { File.Move(staged, installed); }
        catch (Exception) { File.Move(OldExe, installed); throw; }

        var s = Load();
        s.From = CurrentText;
        s.To = release.Version.ToString(3);
        s.Status = "starting";
        s.Starts = 0;
        s.WatchdogPid = Environment.ProcessId;
        s.Reason = null;
        s.Announced = false;
        s.Notes = release.Notes.ToList();
        Save(s);

        AppLog.Write($"update: {s.From} -> {s.To} swapped, handing over");
        handOver();
        // From here on nothing may throw: the mutex is gone and the caller exits whatever happens.
        string? failure;
        Process? started = null;
        try
        {
            started = Process.Start(new ProcessStartInfo(installed) { UseShellExecute = false, Arguments = "--after-update" + (showWindow ? "" : " --tray") });
            failure = started == null ? "не запустилась" : await WaitForOkAsync(started, TimeSpan.FromSeconds(90));
        }
        catch (System.ComponentModel.Win32Exception) { failure = "Windows не дала её запустить"; }
        catch (Exception e) { failure = e.Message; }
        AppLog.Write($"update: new copy pid {started?.Id} -> {failure ?? "ok"}");
        if (failure == null) return;

        try
        {
            if (started is { HasExited: false }) started.Kill(entireProcessTree: true);
            var now = Load();
            if (now.Status != "rolledback") RollBack(now, failure, exit: false);
        }
        catch (Exception) { /* nothing left to try: autostart starts whatever exe is in place */ }
    }

    static async Task<string?> WaitForOkAsync(Process process, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var s = Load();
            if (s.Status == "ok") return null;
            if (s.Status == "rolledback") return s.Reason ?? "откат";
            if (process.HasExited) return process.ExitCode == 0 ? "закрылась сама" : "упала при запуске";
            await Task.Delay(500);
        }
        return "не ответила за 90 с";
    }

    /// <summary>Puts DuoSync.old.exe back, marks the version bad and starts the previous version.</summary>
    static void RollBack(State s, string reason, bool exit)
    {
        var installed = Installer.InstalledExe;
        s.Reason = reason;
        AppLog.Write($"update: rolling back {s.To} -> {s.From}: {reason}");
        if (!File.Exists(OldExe))
        {
            s.Status = "failed";
            Save(s);
            return;
        }
        try
        {
            var bad = Path.Combine(AutoStart.InstallDir, $"DuoSync.bad-{s.To}.exe");
            if (File.Exists(bad)) File.Delete(bad);
            File.Move(installed, bad);
            File.Move(OldExe, installed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            s.Status = "failed";
            s.Reason += "; откат не удался: " + e.Message;
            Save(s);
            return;
        }
        if (!s.Bad.Contains(s.To)) s.Bad.Add(s.To);
        s.Status = "rolledback";
        s.Announced = false;
        Save(s);
        try { Process.Start(new ProcessStartInfo(installed) { UseShellExecute = false, Arguments = "--after-update --tray" }); }
        catch (System.ComponentModel.Win32Exception) { }
        if (exit) Environment.Exit(1);
    }

    static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
