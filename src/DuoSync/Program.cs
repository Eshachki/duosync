namespace DuoSync;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--cli") return Cli.Run(args);
        ApplicationConfiguration.Initialize();
        if (Installer.InstallAndRelaunch(args)) return 0;

        using var mutex = new Mutex(true, SingleInstance.MutexName, out bool first);
        if (!first)
        {
            SingleInstance.AskRunningToShow();
            return 0;
        }
        Installer.CleanupOldCopy();
        var snapshot = ArgValue(args, "--snapshot");
        Application.Run(new TrayContext(showWindow: !args.Contains("--tray"), snapshotPath: snapshot));
        return 0;
    }

    static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
