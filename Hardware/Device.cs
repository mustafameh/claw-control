using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClawControl.Hardware;

/// <summary>
/// What this machine is and has. Everything Claw Control writes (firmware power limits, the
/// controller chip, MSI's settings) was worked out on the MSI Claw A8, so any other device is
/// refused before anything is touched. Read from the registry copy of the BIOS data: instant.
/// </summary>
public static class Device
{
    /// <summary>MSI's board code for the Claw A8.</summary>
    public const string SupportedBoard = "MS-1T8K";

    public static string Board => Bios("BaseBoardProduct");

    /// <summary>E.g. "Micro-Star International Co., Ltd. Claw A8 BZ2EM".</summary>
    public static string Name => $"{Bios("SystemManufacturer")} {Bios("SystemProductName")}".Trim();

    public static bool IsSupported => string.Equals(Board, SupportedBoard, StringComparison.OrdinalIgnoreCase);

    /// <summary>Windows can hibernate here (hibernation is on and a hibernation file exists).</summary>
    public static bool CanHibernate
    {
        get
        {
            // SYSTEM_POWER_CAPABILITIES starts with one-byte flags: ... [6] SystemS4, [8] HiberFilePresent.
            var caps = new byte[256];
            return GetPwrCapabilities(caps) && caps[6] != 0 && caps[8] != 0;
        }
    }

    static string Bios(string name) =>
        (Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", name, null) as string)?.Trim() ?? "";

    [DllImport("powrprof.dll")] static extern bool GetPwrCapabilities([Out] byte[] capabilities);
}
