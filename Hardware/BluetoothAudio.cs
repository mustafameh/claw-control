using System.Runtime.InteropServices;
using ClawControl.Core;
using Microsoft.Win32.SafeHandles;
using Windows.Devices.Enumeration;

namespace ClawControl.Hardware;

/// <summary>
/// Connects or disconnects paired Bluetooth headphones / speakers. Windows has no public
/// "connect" call; its own Bluetooth audio driver takes a one-shot reconnect / disconnect
/// property (KSPROPSETID_BtAudio) on the device's audio filters — the same thing the Settings
/// app's Connect button ends up doing (and the open-source ToothTray uses).
/// </summary>
static class BluetoothAudio
{
    // KSCATEGORY_AUDIO device interfaces of the headphones, matched by their container.
    const string AudioCategory = "{6994AD04-93EF-11D0-A3CC-00A0C9223196}";
    static readonly Guid BtAudioProperties = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    const uint Reconnect = 0, Disconnect = 1, PropertyGet = 1;
    const uint IoctlKsProperty = 0x2F0003;

    [StructLayout(LayoutKind.Sequential)]
    struct KsProperty
    {
        public Guid Set;
        public uint Id, Flags;
    }

    static Task<DeviceInformationCollection> Filters(Guid container) => DeviceInformation.FindAllAsync(
        $"System.Devices.InterfaceClassGuid:=\"{AudioCategory}\" AND System.Devices.ContainerId:=\"{{{container}}}\"").AsTask();

    /// <summary>The device has Bluetooth audio filters (so it can be connected from here).</summary>
    public static async Task<bool> Has(Guid container) =>
        container != Guid.Empty && (await Filters(container)).Any(f => f.IsEnabled);

    public static async Task<bool> Set(Guid container, bool connect)
    {
        bool sent = false;
        foreach (var filter in (await Filters(container)).Where(f => f.IsEnabled))
        {
            try
            {
                using var handle = CreateFile(filter.Id, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);  // read/write, shared, open existing
                if (handle.IsInvalid) continue;
                var property = new KsProperty { Set = BtAudioProperties, Id = connect ? Reconnect : Disconnect, Flags = PropertyGet };
                sent |= DeviceIoControl(handle, IoctlKsProperty, ref property, (uint)Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log.Error($"Bluetooth audio {(connect ? "connect" : "disconnect")}", ex);
            }
        }
        Log.Info($"Bluetooth {(connect ? "connect" : "disconnect")} {(sent ? "sent" : "failed")}");
        return sent;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref KsProperty input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
