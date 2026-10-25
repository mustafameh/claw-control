using System.Runtime.InteropServices;

namespace ClawControl.Monitoring;

/// <summary>Cheap Windows readings that need nothing running: memory and battery status.</summary>
public static class SystemInfo
{
    public static (double UsedGb, double TotalGb)? Ram()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) return null;
        const double gb = 1024.0 * 1024 * 1024;
        return ((status.TotalPhys - status.AvailPhys) / gb, status.TotalPhys / gb);
    }

    public static (int Percent, bool Charging, bool PluggedIn, int? MinutesLeft)? Battery()
    {
        if (!GetSystemPowerStatus(out var s) || s.BatteryLifePercent > 100) return null;
        bool charging = (s.BatteryFlag & 8) != 0;
        int? minutes = s.BatteryLifeTime > 0 ? s.BatteryLifeTime / 60 : null;
        return (s.BatteryLifePercent, charging, s.ACLineStatus == 1, minutes);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
