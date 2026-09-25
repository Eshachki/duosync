using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DuoSync.Core.Ops;

namespace DuoSync;

/// <summary>Tray icons drawn in code: one filled circle per state colour (§10.2).</summary>
static class Icons
{
    static readonly Dictionary<Color, Icon> Cache = new();

    public static Color ColorOf(SyncState state) => state switch
    {
        SyncState.InSync => Color.FromArgb(46, 160, 67),
        SyncState.Incoming => Color.FromArgb(31, 111, 235),
        SyncState.Unsent or SyncState.Both or SyncState.NotPrepared => Color.FromArgb(219, 133, 20),
        SyncState.AuthFailed or SyncState.Rewritten => Color.FromArgb(207, 34, 46),
        _ => Color.FromArgb(128, 128, 128),
    };

    public static Icon For(SyncState state)
    {
        var color = ColorOf(state);
        if (Cache.TryGetValue(color, out var icon)) return icon;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 3, 3, 26, 26);
            using var pen = new Pen(Color.White, 3);
            g.DrawArc(pen, 9, 9, 14, 14, 200, 300);
        }
        var handle = bmp.GetHicon();
        icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        Cache[color] = icon;
        return icon;
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
