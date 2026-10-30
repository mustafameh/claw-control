using System.Runtime.InteropServices;

namespace ClawControl.Hardware;

/// <summary>Windows scaling of the main screen: the current step and the steps Windows offers now.</summary>
public readonly record struct ScaleInfo(int Percent, IReadOnlyList<int> Choices)
{
    public static readonly ScaleInfo Unknown = new(0, []);
}

/// <summary>
/// Windows scaling (Settings › Display › Scale) through the display-config calls Settings itself
/// uses (undocumented device-info types -3 / -4). Only Windows' own steps: they apply at once,
/// unlike custom scaling, which needs a sign-out. Steps are counted from the recommended one.
/// </summary>
public static class DisplayScale
{
    /// <summary>Windows' scaling steps; the offered range depends on the screen and resolution.</summary>
    static readonly int[] Steps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    public static ScaleInfo Read()
    {
        if (PrimarySource() is not { } source) return ScaleInfo.Unknown;
        var get = new GetScale { Header = Header(GetDpiScale, Marshal.SizeOf<GetScale>(), source) };
        if (DisplayConfigGetDeviceInfo(ref get) != 0) return ScaleInfo.Unknown;
        int recommended = Math.Abs(get.Min);
        int Percent(int relative) => Steps[Math.Clamp(recommended + relative, 0, Steps.Length - 1)];
        var choices = Enumerable.Range(get.Min, get.Max - get.Min + 1).Select(Percent).Distinct().ToList();
        return new ScaleInfo(Percent(get.Current), choices);
    }

    /// <summary>Sets one of Windows' steps on the main screen; false if Windows refused.</summary>
    public static bool Apply(int percent)
    {
        if (PrimarySource() is not { } source) return false;
        var get = new GetScale { Header = Header(GetDpiScale, Marshal.SizeOf<GetScale>(), source) };
        int step = Array.IndexOf(Steps, percent);
        if (step < 0 || DisplayConfigGetDeviceInfo(ref get) != 0) return false;
        var set = new SetScale { Header = Header(SetDpiScale, Marshal.SizeOf<SetScale>(), source), Relative = step - Math.Abs(get.Min) };
        return DisplayConfigSetDeviceInfo(ref set) == 0;
    }

    /// <summary>The active display source showing the main screen (adapter + source id).</summary>
    static DeviceHeader? PrimarySource() => PrimaryPath()?.Source;

    /// <summary>
    /// The main screen's own resolution (its preferred mode, as the panel reports it), whatever
    /// Windows or a game drives it at; null if unknown. Not the biggest mode Windows lists: the
    /// driver can add bigger virtual ones.
    /// </summary>
    public static (int Width, int Height)? NativeSize()
    {
        if (PrimaryPath()?.Target is not { } target) return null;
        // DISPLAYCONFIG_TARGET_PREFERRED_MODE: header, width, height, then the timing (80 bytes in all).
        var packet = new byte[80];
        BitConverter.TryWriteBytes(packet.AsSpan(0), GetTargetPreferredMode);
        BitConverter.TryWriteBytes(packet.AsSpan(4), packet.Length);
        BitConverter.TryWriteBytes(packet.AsSpan(8), target.AdapterLow);
        BitConverter.TryWriteBytes(packet.AsSpan(12), target.AdapterHigh);
        BitConverter.TryWriteBytes(packet.AsSpan(16), target.Id);
        if (DisplayConfigGetDeviceInfo(packet) != 0) return null;
        int width = BitConverter.ToInt32(packet, 20), height = BitConverter.ToInt32(packet, 24);
        return width > 0 && height > 0 ? (width, height) : null;
    }

    /// <summary>The active path showing the main screen: its source (what Windows draws) and target (the panel).</summary>
    static (DeviceHeader Source, DeviceHeader Target)? PrimaryPath()
    {
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0 || pathCount == 0) return null;
        var paths = new byte[pathCount * PathSize];
        var modes = new byte[modeCount * ModeSize];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return null;

        string? primary = PrimaryDeviceName();
        (DeviceHeader, DeviceHeader)? first = null;
        for (int i = 0; i < pathCount; i++)
        {
            // DISPLAYCONFIG_PATH_INFO: sourceInfo (adapter LUID low, high, source id, mode index,
            // flags), then targetInfo (adapter LUID low, high, target id, ...).
            DeviceHeader At(int offset) => new()
            {
                AdapterLow = BitConverter.ToUInt32(paths, i * PathSize + offset),
                AdapterHigh = BitConverter.ToInt32(paths, i * PathSize + offset + 4),
                Id = BitConverter.ToUInt32(paths, i * PathSize + offset + 8),
            };
            var path = (At(0), At(20));
            first ??= path;
            var name = new SourceName { Header = Header(GetSourceName, Marshal.SizeOf<SourceName>(), path.Item1) };
            if (primary != null && DisplayConfigGetDeviceInfo(ref name) == 0
                && string.Equals(name.GdiName, primary, StringComparison.OrdinalIgnoreCase)) return path;
        }
        return first;
    }

    static string? PrimaryDeviceName()
    {
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        return GetMonitorInfo(MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY), ref info) ? info.Device : null;
    }

    static DeviceHeader Header(int type, int size, DeviceHeader source) => source with { Type = type, Size = size };

    const int GetSourceName = 1, GetTargetPreferredMode = 3, GetDpiScale = -3, SetDpiScale = -4;
    const uint QDC_ONLY_ACTIVE_PATHS = 2, MONITOR_DEFAULTTOPRIMARY = 1;
    const int PathSize = 72, ModeSize = 64;  // DISPLAYCONFIG_PATH_INFO, DISPLAYCONFIG_MODE_INFO

    [StructLayout(LayoutKind.Sequential)]
    record struct DeviceHeader
    {
        public int Type, Size;
        public uint AdapterLow;
        public int AdapterHigh;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GetScale { public DeviceHeader Header; public int Min, Current, Max; }

    [StructLayout(LayoutKind.Sequential)]
    struct SetScale { public DeviceHeader Header; public int Relative; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SourceName
    {
        public DeviceHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfoEx
    {
        public int Size;
        public int MonitorLeft, MonitorTop, MonitorRight, MonitorBottom, WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] byte[] paths, ref uint modeCount, [Out] byte[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref GetScale info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceName info);
    [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref SetScale info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo([In, Out] byte[] packet);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
}
