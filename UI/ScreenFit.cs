using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace ClawControl.UI;

/// <summary>
/// Sizes and places windows from the primary screen's real pixels, so they cover the same share
/// of the screen at any resolution and Windows scaling. WPF's own screen sizes
/// (SystemParameters) use the scaling Windows had when the app started, so after a resolution
/// change (which often comes with a new scaling) they put windows in the wrong place.
/// </summary>
static class ScreenFit
{
    /// <summary>The screen's height in design units: on the Claw's 1920 × 1200 one unit is 1.5 px,
    /// the size the panel was designed at (150 % scaling).</summary>
    const double DesignHeight = 800;

    /// <summary>The primary screen in physical pixels: all of it, and the part the taskbar leaves.</summary>
    public readonly record struct Screen(Int32Rect Bounds, Int32Rect Work)
    {
        /// <summary>Physical pixels per design unit.</summary>
        public double UnitPx => Bounds.Height / DesignHeight;
    }

    public static Screen Primary()
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY), ref info))
            return new Screen(new Int32Rect(0, 0, 1920, 1200), new Int32Rect(0, 0, 1920, 1200));
        return new Screen(info.rcMonitor.ToRect(), info.rcWork.ToRect());
    }

    /// <summary>The window's own units per design unit: cancels whatever scaling WPF applies to it.</summary>
    public static double Scale(Visual window, Screen screen) => screen.UnitPx / VisualTreeHelper.GetDpi(window).DpiScaleX;

    /// <summary>Physical pixels → the window's own units (what Left / Top / Width take).</summary>
    public static double ToWindowUnits(Visual window, double pixels) => pixels / VisualTreeHelper.GetDpi(window).DpiScaleX;

    const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
        public Int32Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
