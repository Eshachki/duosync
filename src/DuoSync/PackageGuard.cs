using System.Runtime.InteropServices;
using System.Text;

namespace DuoSync;

/// <summary>
/// A process started by a packaged (MSIX) app, e.g. a desktop AI assistant, inherits that package: its writes to
/// AppData and HKCU land in the package's private copy. The program then seems installed and even updates itself,
/// but after a reboot Windows finds neither the exe nor the autostart entry (found 26.09 on a real machine).
/// Explorer starts programs outside any package, so the first start hands over to it.
/// </summary>
static class PackageGuard
{
    const int NoPackage = 15700; // APPMODEL_ERROR_NO_PACKAGE

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFamilyName(ref int length, StringBuilder? name);

    /// <summary>Family name of the package this process runs inside, or null for a normal process.</summary>
    public static string? HostPackageFamily()
    {
        try
        {
            int length = 0;
            if (GetCurrentPackageFamilyName(ref length, null) == NoPackage) return null;
            var name = new StringBuilder(length);
            return GetCurrentPackageFamilyName(ref length, name) == 0 ? name.ToString() : null;
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    /// <summary>
    /// True when this process must not continue here: it restarted itself through Explorer or told the person how to start it.
    /// </summary>
    public static bool HandOffIfPackaged()
    {
        // No log line here: inside the package it would land in the package's private copy too.
        if (HostPackageFamily() is not { } family || Environment.ProcessPath is not { } exe) return false;
        // An exe that exists only in the package's private AppData copy is invisible to Explorer.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var privateCopy = exe.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(local, "Packages", family, "LocalCache", "Local", exe[(local.Length + 1)..])
            : null;
        if (privateCopy != null && File.Exists(privateCopy))
        {
            MessageBox.Show("DuoSync запущен изнутри другой программы и так работать не сможет: автозапуск и настройки пропали бы.\n" +
                            "Скачай DuoSync.exe заново и запусти его двойным щелчком из папки «Загрузки».",
                "DuoSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return true;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{exe}\"") { UseShellExecute = false });
        return true;
    }
}
