using ClawControl.Hardware;

namespace ClawControl.Monitoring;

/// <summary>
/// Sensors read from MSI's firmware with MSI Center M's own formulas (MCMOSDInfo.exe).
/// Has its own firmware connection, used only on the metrics thread, so overlay reads never
/// queue behind TDP changes.
/// </summary>
public sealed class FirmwareSensors
{
    readonly MsiAcpi _acpi = new();

    /// <summary>APU temperature in °C (the firmware reports no separate GPU temperature on the A8).</summary>
    public int? CpuTemperature() =>
        _acpi.Query("Get_Temperature", 0) is { } reply && reply[1] > 0 ? reply[1] : null;

    public int? FanRpm()
    {
        if (_acpi.Query("Get_Fan", 0) is not { } reply) return null;
        int period = reply[1] - reply[2];
        return period == 0 ? 0 : (int)Math.Abs(6e7 / (period * 2 * 62.5));
    }

    /// <summary>Battery current × voltage in watts: negative while drawing from the battery, positive while charging.</summary>
    public double? BatteryWatts()
    {
        if (_acpi.Get(0x46) is not byte currentLow || _acpi.Get(0x47) is not byte currentHigh
            || _acpi.Get(0x4A) is not byte voltageLow || _acpi.Get(0x4B) is not byte voltageHigh)
            return null;
        short milliamps = (short)((currentHigh << 8) | currentLow);
        int millivolts = (voltageHigh << 8) | voltageLow;
        return milliamps * millivolts / 1_000_000.0;
    }
}
