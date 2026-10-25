using System.Runtime.InteropServices;

namespace ClawControl.Hardware;

public static class Battery
{
    /// <summary>Charge level (0-100) and whether the charger is plugged in, or null if unknown.</summary>
    public static (int Percent, bool PluggedIn)? Read()
    {
        if (!GetSystemPowerStatus(out var status) || status.BatteryLifePercent > 100)
            return null;
        return (status.BatteryLifePercent, status.ACLineStatus == 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
