namespace DuoSync;

static class Program
{
    static Mutex? _instance;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--cli") return Cli.Run(args);
        AppLog.Start(args);
        UpdateGuard.OnStartup();
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) =>
        {
            AppLog.Error("ошибка в окне", e.Exception);
            MessageBox.Show("Ошибка: " + e.Exception.Message, "DuoSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        if (Installer.InstallAndRelaunch(args)) return 0;

        // After an update the old copy is still handing over: wait for its mutex instead of showing it.
        _instance = SingleInstance.Acquire(args.Contains("--after-update") ? TimeSpan.FromSeconds(30) : TimeSpan.Zero);
        if (_instance == null)
        {
            SingleInstance.AskRunningToShow();
            return 0;
        }
        try
        {
            Installer.CleanupLeftovers();
            var snapshot = ArgValue(args, "--snapshot");
            Application.Run(new TrayContext(showWindow: !args.Contains("--tray"), snapshotPath: snapshot));
        }
        finally { ReleaseInstance(); }
        return 0;
    }

    /// <summary>Frees the single-instance mutex early: an update hands it to the new copy while this one watches.</summary>
    public static void ReleaseInstance()
    {
        var mutex = Interlocked.Exchange(ref _instance, null);
        if (mutex == null) return;
        try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        mutex.Dispose();
    }

    static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
