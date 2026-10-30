using System.Runtime.InteropServices;
using ClawControl.Hardware;

namespace ClawControl.Monitoring;

/// <summary>What's upscaling the picture, if anything (the overlay's "Upscaling" number).</summary>
public enum ScalingKind { Native, Scaled, Rsr }

/// <summary>
/// Whether the picture on the screen is being stretched, and by what, from facts Windows and the
/// driver report rather than from settings: the mode the screen is driven at against its native
/// size, and whether the app in front fills the screen.
///
/// AMD's Radeon Super Resolution (FSR 1 in the driver) only works when it's on, a game runs
/// full screen and at a lower resolution than the screen's: then the driver upscales it with
/// RSR. A lower resolution without those (RSR off, or the desktop itself set lower) is plain
/// scaling. A game's own FSR / upscaling happens inside its frames, before Windows or the driver
/// see them, so it can't be told from outside: the screen then shows its native mode.
/// </summary>
public static class Scaling
{
    static DisplayMode? _native;

    /// <summary>The screen's own size (the Claw's panel: 1920 × 1200), else its biggest mode.</summary>
    public static DisplayMode Native => _native ??= DisplayScale.NativeSize() is var (width, height)
        ? new DisplayMode(width, height, 0)
        : DisplayModes.All().MaxBy(m => (long)m.Width * m.Height);

    public static (ScalingKind Kind, DisplayMode Mode) Read(IntPtr front, bool rsrOn)
    {
        var mode = DisplayModes.Current();
        var native = Native;
        if (mode.Width <= 0 || native.Width <= 0 || (mode.Width >= native.Width && mode.Height >= native.Height))
            return (ScalingKind.Native, mode);
        return (rsrOn && FillsScreen(front) ? ScalingKind.Rsr : ScalingKind.Scaled, mode);
    }

    /// <summary>The window covers its whole screen (a full-screen game).</summary>
    public static bool FillsScreen(IntPtr window)
    {
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect)) return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref info)) return false;
        var screen = info.Monitor;
        return rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)] struct Rect32 { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor, Work;
        public int Flags;
    }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect32 rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, int flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
