using System.Runtime.InteropServices;

namespace ClawControl.Hardware;

/// <summary>A screen mode Windows offers: resolution and refresh rate.</summary>
public readonly record struct DisplayMode(int Width, int Height, int Hz);

/// <summary>
/// Resolution and refresh rate of the main screen through Windows' own display settings
/// (EnumDisplaySettings / ChangeDisplaySettingsEx); a change is saved like Settings does.
/// </summary>
public static class DisplayModes
{
    /// <summary>Every 32-bit colour mode, as Windows lists them.</summary>
    public static List<DisplayMode> All()
    {
        var modes = new List<DisplayMode>();
        var mode = NewDevMode();
        for (int i = 0; EnumDisplaySettings(null, i, ref mode); i++)
        {
            var m = new DisplayMode(mode.PelsWidth, mode.PelsHeight, mode.DisplayFrequency);
            if (mode.BitsPerPel == 32 && !modes.Contains(m)) modes.Add(m);
        }
        return modes;
    }

    public static DisplayMode Current()
    {
        var mode = NewDevMode();
        return EnumDisplaySettings(null, CurrentSettings, ref mode)
            ? new DisplayMode(mode.PelsWidth, mode.PelsHeight, mode.DisplayFrequency)
            : default;
    }

    /// <summary>Switches the main screen; false if Windows refused.</summary>
    public static bool Apply(DisplayMode target)
    {
        var mode = NewDevMode();
        if (!EnumDisplaySettings(null, CurrentSettings, ref mode)) return false;
        mode.PelsWidth = target.Width;
        mode.PelsHeight = target.Height;
        mode.DisplayFrequency = target.Hz;
        mode.Fields = PelsWidth | PelsHeight | DisplayFrequencyField;
        return ChangeDisplaySettingsEx(null, ref mode, IntPtr.Zero, UpdateRegistry, IntPtr.Zero) == 0;
    }

    static DevMode NewDevMode() => new() { Size = (short)Marshal.SizeOf<DevMode>() };

    const int CurrentSettings = -1, UpdateRegistry = 0x01;
    const int PelsWidth = 0x80000, PelsHeight = 0x100000, DisplayFrequencyField = 0x400000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string? device, int modeNumber, ref DevMode mode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string? device, ref DevMode mode, IntPtr hwnd, int flags, IntPtr param);
}
