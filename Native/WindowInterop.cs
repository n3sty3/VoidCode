using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using VoidCode.Services;

namespace VoidCode.Native;

/// <summary>
/// The Win32 details the window needs -
/// Rounded dark chrome, drag-to-move, monitor-clamped maximize and the themed taskbar icon. Keeping the
/// P/Invoke here means the window itself has no interop declarations left.
/// </summary>
internal static class WindowInterop
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    /// <summary>
    /// The body of the old <c>window_drag</c> case. It stays in the window - it needs the
    /// HWND - and the session domain calls it through its chrome delegate.
    /// </summary>
    internal static void Drag(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        ReleaseCapture();
        SendMessage(hwnd, 0xA1 /* WM_NCLBUTTONDOWN */, 0x2 /* HTCAPTION */, 0);
    }

    internal static void ApplyThemeIcon(Window window, string? themeName)
    {
        try
        {
            string icoFile = (themeName ?? "harbor").ToLowerInvariant() switch
            {
                "obsidian" => "voidcode-obsidian-frost.ico",
                "crimson" => "voidcode-crimson-dusk.ico",
                "emerald" => "voidcode-emerald-matrix.ico",
                "nebula" => "voidcode-violet-nebula.ico",
                _ => "voidcode-harbor-glass.ico"
            };

            var icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "themes", icoFile);
            if (File.Exists(icoPath))
            {
                window.Icon = BitmapFrame.Create(new Uri(icoPath, UriKind.Absolute));
            }
        }
        catch (Exception ex) { LoggingService.Swallowed(ex, "WindowInterop.SetThemeIcon"); }
    }

    /// <summary>Rounded dark title bar and the caption colour the glass theme expects.</summary>
    internal static void EnableDarkChrome(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            int cornerPref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref cornerPref, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
            int captionColor = 0x000d0907; // COLORREF BGR for #07090d
            DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int)); // DWMWA_CAPTION_COLOR
        }
        catch (Exception) { /* best-effort window chrome: harmless if DWM rejects it on this build */ }
    }

    /// <summary>The HwndSource hook: clamps maximize to the monitor work area.</summary>
    internal static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            ApplyMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }


    /// <summary>Clamps a maximize request to the monitor work area, so the window never hides behind the taskbar.</summary>
    internal static void ApplyMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                RECT rcWorkArea = monitorInfo.rcWork;
                RECT rcMonitorArea = monitorInfo.rcMonitor;
                mmi.ptMaxPosition.x = Math.Abs(rcWorkArea.left - rcMonitorArea.left);
                mmi.ptMaxPosition.y = Math.Abs(rcWorkArea.top - rcMonitorArea.top);
                mmi.ptMaxSize.x = Math.Abs(rcWorkArea.right - rcWorkArea.left);
                mmi.ptMaxSize.y = Math.Abs(rcWorkArea.bottom - rcWorkArea.top);
            }
        }
        Marshal.StructureToPtr(mmi, lParam, true);
    }
}
