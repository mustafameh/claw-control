using ClawControl.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Enumeration.Pnp;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;

namespace ClawControl.Hardware;

public enum LinkState { Unknown, Off, On, Connected }

/// <summary>
/// What the header and the Quick tab show: each radio's state, Wi-Fi signal bars (0-5), the
/// network and device names, and the connected device's battery (null if Windows doesn't know).
/// </summary>
public sealed record ConnectivityState(LinkState WiFi, int Bars, string? Network, LinkState Bluetooth, string? Device, int? DeviceBattery = null)
{
    public static readonly ConnectivityState Unknown = new(LinkState.Unknown, 0, null, LinkState.Unknown, null);
}

/// <summary>A network from the last scan.</summary>
public sealed record WifiNetwork(string Ssid, int Bars, bool Secured, bool Saved, bool Connected);

/// <summary>A paired Bluetooth device; Audio = it can be connected from here (headphones, speakers).</summary>
public sealed record PairedDevice(string Name, Guid Container, bool Connected, int? Battery, bool Audio);

/// <summary>
/// Wi-Fi and Bluetooth through Windows' own APIs (radios, connection profiles, Wi-Fi scans,
/// paired / connected Bluetooth devices; reads take ~0.3 s, off the UI thread). Listens for
/// changes only between StartWatching and StopWatching (while the panel is open).
/// </summary>
public sealed class Connectivity
{
    // Windows' "battery level" property of a Bluetooth device (what Settings shows).
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    readonly object _gate = new();
    readonly Dictionary<string, WiFiAvailableNetwork> _networks = new();
    WiFiAdapter? _adapter;
    IReadOnlyList<Radio> _radios = [];
    DeviceWatcher? _classic, _lowEnergy;
    bool _watching, _refreshing, _again;

    public ConnectivityState Last { get; private set; } = ConnectivityState.Unknown;

    /// <summary>Last changed (thread pool).</summary>
    public event Action? Changed;

    /// <summary>Reads everything again; overlapping requests fold into one more read.</summary>
    public Task RefreshAsync() => Task.Run(async () =>
    {
        lock (_gate)
        {
            if (_refreshing) { _again = true; return; }
            _refreshing = true;
        }
        try
        {
            do
            {
                _again = false;
                var state = await Read();
                if (state != Last)
                {
                    Last = state;
                    Changed?.Invoke();
                }
            } while (_again);
        }
        catch (Exception ex)
        {
            Log.Error("Reading Wi-Fi and Bluetooth", ex);
        }
        finally
        {
            lock (_gate) _refreshing = false;
        }
    });

    async Task<ConnectivityState> Read()
    {
        if (_radios.Count == 0) _radios = await Radio.GetRadiosAsync();
        var wifi = FindRadio(RadioKind.WiFi);
        var bluetooth = FindRadio(RadioKind.Bluetooth);

        // Connected Wi-Fi: usually the internet profile; otherwise any Wi-Fi network that is up.
        var internet = NetworkInformation.GetInternetConnectionProfile();
        var profile = internet is { IsWlanConnectionProfile: true } ? internet
            : NetworkInformation.GetConnectionProfiles().FirstOrDefault(p =>
                p.IsWlanConnectionProfile && p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None);
        LinkState wifiState = wifi switch
        {
            null => LinkState.Unknown,
            { State: not RadioState.On } => LinkState.Off,
            _ => profile != null ? LinkState.Connected : LinkState.On,
        };
        string? network = profile?.WlanConnectionProfileDetails?.GetConnectedSsid();
        if (string.IsNullOrEmpty(network)) network = profile?.ProfileName;

        var devices = bluetooth is { State: RadioState.On }
            ? (await Devices(BluetoothConnectionStatus.Connected)).Where(d => !IsTwin(d.Name)).ToList()
            : [];
        LinkState bluetoothState = bluetooth switch
        {
            null => LinkState.Unknown,
            { State: not RadioState.On } => LinkState.Off,
            _ => devices.Count > 0 ? LinkState.Connected : LinkState.On,
        };
        string? device = devices.Count switch { 0 => null, 1 => devices[0].Name, _ => $"{devices[0].Name} +{devices.Count - 1}" };
        int? battery = devices.Count > 0 ? await Battery(devices[0].Container) : null;

        return new ConnectivityState(wifiState, profile?.GetSignalBars() ?? 0, network, bluetoothState, device, battery);
    }

    // Windows lists LE Audio headphones twice; the "LE-" twin is the same device.
    static bool IsTwin(string name) => name.StartsWith("LE-", StringComparison.Ordinal);

    /// <summary>Bluetooth devices (classic and LE) that are connected, or with null: all paired ones.</summary>
    static async Task<List<(string Name, Guid Container)>> Devices(BluetoothConnectionStatus? status)
    {
        string[] selectors = status is { } s
            ? [BluetoothDevice.GetDeviceSelectorFromConnectionStatus(s), BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(s)]
            : [BluetoothDevice.GetDeviceSelectorFromPairingState(true), BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)];
        var found = new List<(string, Guid)>();
        foreach (var selector in selectors)
            foreach (var d in await DeviceInformation.FindAllAsync(selector, ["System.Devices.Aep.ContainerId"]))
            {
                var container = d.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var c) && c is Guid g ? g : Guid.Empty;
                if (!string.IsNullOrWhiteSpace(d.Name) && !found.Exists(f => f.Item1 == d.Name)) found.Add((d.Name, container));
            }
        return found;
    }

    /// <summary>The device's battery as Windows last heard it (headphones report it while connected).</summary>
    static async Task<int?> Battery(Guid container)
    {
        if (container == Guid.Empty) return null;
        var nodes = await PnpObject.FindAllAsync(PnpObjectType.Device, [BatteryKey], $"System.Devices.ContainerId:=\"{{{container}}}\"");
        var levels = nodes.Select(n => n.Properties.TryGetValue(BatteryKey, out var v) && v is byte b ? (int?)b : null).Where(v => v != null).ToList();
        return levels.Count > 0 ? levels.Max() : null;
    }

    // ---- Changes (Quick tab tiles and the Wi-Fi / Bluetooth screens) ----

    /// <summary>Turns the Wi-Fi or Bluetooth radio on or off.</summary>
    public async Task SetRadio(bool wifi, bool on)
    {
        try
        {
            await Windows.Devices.Radios.Radio.RequestAccessAsync();
            if (_radios.Count == 0) _radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
            if (FindRadio(wifi ? RadioKind.WiFi : RadioKind.Bluetooth) is { } radio)
                await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        }
        catch (Exception ex)
        {
            Log.Error($"Turning {(wifi ? "Wi-Fi" : "Bluetooth")} {(on ? "on" : "off")}", ex);
        }
        await RefreshAsync();
    }

    /// <summary>Scans for Wi-Fi networks (a few seconds); strongest first, one entry per name.</summary>
    public async Task<List<WifiNetwork>> ScanWifi()
    {
        try
        {
            if (_adapter == null)
            {
                if (await WiFiAdapter.RequestAccessAsync() != WiFiAccessStatus.Allowed) return [];
                _adapter = (await WiFiAdapter.FindAllAdaptersAsync()).FirstOrDefault();
                if (_adapter == null) return [];
            }
            await _adapter.ScanAsync();
            var saved = NetworkInformation.GetConnectionProfiles().Where(p => p.IsWlanConnectionProfile).Select(p => p.ProfileName).ToHashSet();
            string? connected = Last.WiFi == LinkState.Connected ? Last.Network : null;
            var best = _adapter.NetworkReport.AvailableNetworks
                .Where(n => !string.IsNullOrWhiteSpace(n.Ssid))
                .GroupBy(n => n.Ssid).Select(g => g.OrderByDescending(n => n.SignalBars).First())
                .OrderByDescending(n => n.Ssid == connected).ThenByDescending(n => n.SignalBars).ToList();
            lock (_gate)
            {
                _networks.Clear();
                foreach (var n in best) _networks[n.Ssid] = n;
            }
            return best.Select(n => new WifiNetwork(n.Ssid, n.SignalBars,
                n.SecuritySettings.NetworkAuthenticationType is not (NetworkAuthenticationType.Open80211 or NetworkAuthenticationType.None),
                saved.Contains(n.Ssid), n.Ssid == connected)).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Scanning for Wi-Fi networks", ex);
            return [];
        }
    }

    /// <summary>Joins a saved or open network from the last scan; false if that didn't work.</summary>
    public async Task<bool> ConnectWifi(string ssid)
    {
        WiFiAvailableNetwork? network;
        lock (_gate) _networks.TryGetValue(ssid, out network);
        if (_adapter == null || network == null) return false;
        try
        {
            var result = await _adapter.ConnectAsync(network, WiFiReconnectionKind.Automatic);
            await RefreshAsync();
            return result.ConnectionStatus == WiFiConnectionStatus.Success;
        }
        catch (Exception ex)
        {
            Log.Error($"Connecting to {ssid}", ex);
            return false;
        }
    }

    /// <summary>Paired devices (no LE twins), connected first, with their battery when connected.</summary>
    public async Task<List<PairedDevice>> PairedDevices()
    {
        try
        {
            var connected = (await Devices(BluetoothConnectionStatus.Connected)).Select(d => d.Container).ToHashSet();
            var list = new List<PairedDevice>();
            foreach (var (name, container) in await Devices(null))
            {
                if (IsTwin(name)) continue;
                bool isConnected = connected.Contains(container);
                list.Add(new PairedDevice(name, container, isConnected,
                    isConnected ? await Battery(container) : null, await BluetoothAudio.Has(container)));
            }
            return list.OrderByDescending(d => d.Connected).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Listing Bluetooth devices", ex);
            return [];
        }
    }

    /// <summary>Connects or disconnects paired headphones / speakers.</summary>
    public async Task<bool> SetDeviceConnected(Guid container, bool connect)
    {
        bool done = await BluetoothAudio.Set(container, connect);
        await Task.Delay(1500);  // the link takes a moment to come up or go down
        await RefreshAsync();
        return done;
    }

    Radio? FindRadio(RadioKind kind) => _radios.FirstOrDefault(r => r.Kind == kind);

    /// <summary>While the panel is open: radio switches, network changes, Bluetooth connects.</summary>
    public void StartWatching()
    {
        if (_watching) return;
        _watching = true;
        NetworkInformation.NetworkStatusChanged += OnNetworkChanged;
        foreach (var radio in _radios) radio.StateChanged += OnRadioChanged;
        _classic = Watch(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
        _lowEnergy = Watch(BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
    }

    public void StopWatching()
    {
        if (!_watching) return;
        _watching = false;
        NetworkInformation.NetworkStatusChanged -= OnNetworkChanged;
        foreach (var radio in _radios) radio.StateChanged -= OnRadioChanged;
        foreach (var watcher in new[] { _classic, _lowEnergy })
            if (watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted }) watcher.Stop();
        _classic = _lowEnergy = null;
    }

    DeviceWatcher Watch(string selector)
    {
        var watcher = DeviceInformation.CreateWatcher(selector);
        // Devices entering or leaving "connected"; the first enumeration is what Read saw already.
        bool ready = false;
        watcher.EnumerationCompleted += (_, _) => ready = true;
        watcher.Added += (_, _) => { if (ready) _ = RefreshAsync(); };
        watcher.Removed += (_, _) => { if (ready) _ = RefreshAsync(); };
        watcher.Start();
        return watcher;
    }

    void OnNetworkChanged(object? sender) => _ = RefreshAsync();

    void OnRadioChanged(Radio sender, object args) => _ = RefreshAsync();
}
