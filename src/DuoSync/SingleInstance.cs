using System.Security.Cryptography;
using System.Text;

namespace DuoSync;

/// <summary>
/// One running copy per settings folder. A second start asks the running copy to show its window; the installer
/// can ask it to quit before replacing the exe. Named events only, no pipes or ports.
/// </summary>
static class SingleInstance
{
    static string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppSettings.Dir.ToLowerInvariant())))[..16];
    public static string MutexName => @"Local\DuoSync." + Key;
    static string ShowEventName => @"Local\DuoSync.Show." + Key;
    static string QuitEventName => @"Local\DuoSync.Quit." + Key;

    public static void AskRunningToShow()
    {
        if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev)) using (ev) ev.Set();
    }

    public static void AskRunningToQuit(TimeSpan wait)
    {
        if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var ev)) return;
        using (ev) ev.Set();
        // Wait until the running copy releases its mutex.
        var until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until)
        {
            if (!Mutex.TryOpenExisting(MutexName, out var m)) return;
            m.Dispose();
            Thread.Sleep(200);
        }
    }

    /// <summary>Listens for show/quit requests on background threads and runs the callbacks on the UI thread.</summary>
    public static void Listen(SynchronizationContext ui, Action show, Action quit)
    {
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { showEvent, quitEvent };
            while (true)
            {
                var i = WaitHandle.WaitAny(handles);
                if (i == 0) ui.Post(_ => show(), null);
                else { ui.Post(_ => quit(), null); return; }
            }
        }) { IsBackground = true, Name = "DuoSync single instance" };
        thread.Start();
    }
}
