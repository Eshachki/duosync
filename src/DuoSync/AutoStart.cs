using Microsoft.Win32;

namespace DuoSync;

/// <summary>
/// Start with Windows via HKCU\...\Run (one entry, no scheduled tasks, no extra launchers) and remember where the
/// exe lives for the Unity overlay's «Открыть DuoSync» (HKCU\Software\DuoSync\ExePath).
/// Only the installed copy touches the registry: a lab instance (DUOSYNC_HOME) or a dev build never does.
/// </summary>
static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "DuoSync";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuoSync");

    public static bool IsInstalledCopy =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DUOSYNC_HOME")) &&
        Environment.ProcessPath is { } exe &&
        string.Equals(Path.GetDirectoryName(exe), InstallDir, StringComparison.OrdinalIgnoreCase);

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Apply(bool enabled)
    {
        if (!IsInstalledCopy) return;
        var exe = Environment.ProcessPath!;
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled) key.SetValue(ValueName, $"\"{exe}\" --tray");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        using var app = Registry.CurrentUser.CreateSubKey(@"Software\DuoSync");
        app.SetValue("ExePath", exe);
    }
}
