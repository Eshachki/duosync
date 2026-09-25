namespace DuoSync;

/// <summary>
/// Day log for finding out later what happened on a computer, the friend's one included:
/// <c>&lt;settings folder&gt;\logs\YYYY-MM-DD.log</c>, kept 14 days. Only what happened and why:
/// no tokens, no file contents.
/// </summary>
static class AppLog
{
    static readonly object Gate = new();

    public static string Dir => Path.Combine(AppSettings.Dir, "logs");

    public static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, $"{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {text.ReplaceLineEndings(" ⏎ ")}\n");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static void Error(string what, Exception e) => Write($"{what}: {e.GetType().Name}: {e.Message}");

    /// <summary>Start line, old logs away, and every exception nobody caught ends up here.</summary>
    public static void Start(string[] args)
    {
        Write($"start {UpdateGuard.Current.ToString(3)} {Environment.ProcessPath} {string.Join(' ', args)} " +
              $"installed={AutoStart.IsInstalledCopy} windows={Environment.OSVersion.Version}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("crash: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("background task failed: " + e.Exception.InnerException?.GetType().Name + ": " + e.Exception.InnerException?.Message);
            e.SetObserved();
        };
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var file in Directory.EnumerateFiles(Dir, "*.log"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14)) File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
