using System;
using Forms = System.Windows.Forms;

namespace GolfJoyBridge.App;

/// <summary>Monitor lookup shared by the camera windows and the settings window, so both agree on
/// which screen "Monitor = N" means and on the default position for a window of a given size.</summary>
public static class ScreenHelper
{
    public static Forms.Screen Pick(int monitorSetting)
    {
        var all = Forms.Screen.AllScreens;
        if (monitorSetting >= 1 && monitorSetting <= all.Length) return all[monitorSetting - 1];
        return Forms.Screen.PrimaryScreen ?? all[0];
    }

    public static string[] MonitorNames()
    {
        var all = Forms.Screen.AllScreens;
        var names = new string[all.Length + 1];
        names[0] = "Primary";
        for (int i = 0; i < all.Length; i++)
            names[i + 1] = $"Monitor {i + 1} ({all[i].Bounds.Width}x{all[i].Bounds.Height}" + (all[i].Primary ? ", primary)" : ")");
        return names;
    }

    /// <summary>Loop window's default spot: bottom-right corner, clear of the taskbar.</summary>
    public static (int x, int y) DefaultLoopPosition(System.Drawing.Rectangle bounds, int w, int h) =>
        (bounds.Width - w - 20, bounds.Height - h - 60);

    /// <summary>Popup window's default spot: centered.</summary>
    public static (int x, int y) DefaultPopupPosition(System.Drawing.Rectangle bounds, int w, int h) =>
        ((bounds.Width - w) / 2, (bounds.Height - h) / 2);
}
