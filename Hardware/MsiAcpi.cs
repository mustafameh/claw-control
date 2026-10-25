using System.Management;

namespace ClawControl.Hardware;

/// <summary>
/// MSI's firmware interface (WMI root\WMI, class MSI_ACPI): the same calls MSI Center M makes.
/// Get_Data / Set_Data take a 32-byte Package_32 whose byte 0 is the setting index and byte 1
/// the value. Replies come back as [status, value, ...] with status 1 meaning success.
/// Not thread-safe: use it only from the HardwareWorker thread.
/// </summary>
public sealed class MsiAcpi
{
    public const byte Spl = 0x50;        // sustained power limit, watts
    public const byte Sppt = 0x51;       // slow boost limit, watts
    public const byte Fppt = 0x52;       // fast boost limit, watts
    public const byte ShiftMode = 0xD2;  // firmware power/fan profile, 0xC0 | profile

    ManagementObject? _instance;
    ManagementClass? _packageClass;

    ManagementObject Instance => _instance ??=
        new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM MSI_ACPI").Get().Cast<ManagementObject>().First();

    public byte? Get(byte index)
    {
        var reply = Invoke("Get_Data", index, 0);
        return reply is { Length: > 1 } && reply[0] == 1 ? reply[1] : null;
    }

    public void Set(byte index, byte value) => Invoke("Set_Data", index, value);

    /// <summary>
    /// Raw reply of another Get_* method (Get_Temperature, Get_Fan, ...): [status, values...],
    /// or null if the firmware refused.
    /// </summary>
    public byte[]? Query(string method, byte index)
    {
        var reply = Invoke(method, index, 0);
        return reply is { Length: > 2 } && reply[0] == 1 ? reply : null;
    }

    byte[]? Invoke(string method, byte index, byte value)
    {
        _packageClass ??= new ManagementClass(@"root\WMI", "Package_32", null);
        var bytes = new byte[32];
        bytes[0] = index;
        bytes[1] = value;
        using var package = _packageClass.CreateInstance();
        package["Bytes"] = bytes;
        using var inParams = Instance.GetMethodParameters(method);
        inParams["Data"] = package;
        using var outParams = Instance.InvokeMethod(method, inParams, null);
        return (outParams?["Data"] as ManagementBaseObject)?["Bytes"] as byte[];
    }
}
