using System.Runtime.InteropServices;

namespace GolfJoyBridge;

/// <summary>Windows-only helpers (declarations compile everywhere; they only run on Windows).</summary>
public static class Win32
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_LAYERED = 0x00080000;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetDllDirectory(string? path);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    /// <summary>Adds extended window style bits (for example WS_EX_NOACTIVATE).</summary>
    public static void AddExStyle(IntPtr hwnd, long bits)
    {
        long cur = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(cur | bits));
    }

    /// <summary>Places a window on top of everything, in pixel coordinates, without activating it.</summary>
    public static void PlaceTopmost(IntPtr hwnd, int x, int y, int width, int height, bool show)
    {
        uint flags = SWP_NOACTIVATE | (show ? SWP_SHOWWINDOW : 0);
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, flags);
    }

    /// <summary>
    /// Gives keyboard focus to a window. Works when our own window currently has focus (the normal case after a popup closes);
    /// falls back to the usual ALT-key trick if Windows refuses.
    /// </summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (SetForegroundWindow(hwnd)) return true;
        keybd_event(0x12, 0, 0, UIntPtr.Zero);      // ALT down
        keybd_event(0x12, 0, 2, UIntPtr.Zero);      // ALT up
        return SetForegroundWindow(hwnd);
    }
}
