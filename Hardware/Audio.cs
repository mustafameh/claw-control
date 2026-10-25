using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClawControl.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ClawControl.Hardware;

/// <summary>An output device Windows has right now (speakers, headphones, Bluetooth, HDMI...).</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>One app's volume (all of its audio sessions together).</summary>
public sealed record AppVolume(string Key, string Name, ImageSource? Icon, int Percent);

/// <summary>
/// Sound through Windows Core Audio: master volume, output devices, per-app volume. Every call
/// runs on its own worker thread (never the UI thread, never behind firmware calls). Nothing is
/// watched while the panel is closed: change notifications are only registered while it's open.
/// </summary>
public sealed class AudioService : IDisposable
{
    readonly HardwareWorker _worker = new();
    readonly LatestOnly<int> _master;
    readonly Dictionary<string, LatestOnly<int>> _appSenders = new();
    readonly Dictionary<string, ImageSource?> _icons = new();
    MMDeviceEnumerator? _enumerator;     // worker thread only
    MMDevice? _masterDevice;             // default output while the panel is open
    MMDevice? _appsDevice;               // while the Sound screen is open
    AudioSessionManager? _appsManager;
    Dictionary<string, List<AudioSessionControl>> _sessionsByApp = new();
    DateTime _lastMasterSetAt;

    /// <summary>Master volume changed outside the panel (volume keys, Windows) while it's open. Any thread.</summary>
    public event Action<int, bool>? MasterChanged;

    /// <summary>An app started playing sound while the Sound screen is open. Any thread.</summary>
    public event Action? AppsChanged;

    public AudioService()
    {
        _master = new LatestOnly<int>(_worker, "Setting volume", percent =>
        {
            var volume = DefaultDevice().AudioEndpointVolume;
            volume.MasterVolumeLevelScalar = percent / 100f;
            if (percent > 0 && volume.Mute) volume.Mute = false;
        });
    }

    MMDeviceEnumerator Enumerator => _enumerator ??= new MMDeviceEnumerator();

    MMDevice DefaultDevice() => _masterDevice ?? (_masterDevice = Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));

    // ---- Master volume ----

    /// <summary>Starts listening for volume changes from elsewhere; call when the panel opens.</summary>
    public Task<(int Percent, bool Muted)?> OpenAsync() => _worker.Run<(int, bool)?>(() =>
    {
        try
        {
            ReleaseMaster();
            var volume = DefaultDevice().AudioEndpointVolume;
            volume.OnVolumeNotification += OnMasterNotification;
            return ((int)Math.Round(volume.MasterVolumeLevelScalar * 100), volume.Mute);
        }
        catch (Exception ex)
        {
            Log.Error("Reading volume", ex);
            return null;
        }
    });

    /// <summary>Stops listening; call when the panel closes.</summary>
    public void Close() => _worker.Run(() => { ReleaseMaster(); return 0; });

    void ReleaseMaster()
    {
        if (_masterDevice == null) return;
        try { _masterDevice.AudioEndpointVolume.OnVolumeNotification -= OnMasterNotification; } catch { }
        _masterDevice.Dispose();
        _masterDevice = null;
    }

    void OnMasterNotification(AudioVolumeNotificationData data)
    {
        // Our own slider moves come back here too; ignore them so the slider doesn't jump.
        if (DateTime.UtcNow - _lastMasterSetAt < TimeSpan.FromSeconds(1)) return;
        MasterChanged?.Invoke((int)Math.Round(data.MasterVolume * 100), data.Muted);
    }

    public void SetMaster(int percent)
    {
        _lastMasterSetAt = DateTime.UtcNow;
        _master.Post(Math.Clamp(percent, 0, 100));
    }

    // ---- Output devices ----

    public Task<List<AudioDevice>> ReadDevicesAsync() => _worker.Run(() =>
    {
        var result = new List<(string Id, string Desc, string Friendly)>();
        string? defaultId = null;
        try
        {
            using (var current = Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                defaultId = current.ID;
            foreach (var device in Enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (device)
                {
                    string friendly = device.FriendlyName;
                    string desc = device.Properties.Contains(PropertyKeys.PKEY_Device_DeviceDesc)
                        ? device.Properties[PropertyKeys.PKEY_Device_DeviceDesc].Value as string ?? friendly
                        : friendly;
                    result.Add((device.ID, desc, friendly));
                }
        }
        catch (Exception ex)
        {
            Log.Error("Listing output devices", ex);
        }
        // Short names ("Speakers", "Headphones"); when two share one, use the name in brackets
        // ("Headphones (Galaxy Buds)" -> "Galaxy Buds") so they can be told apart.
        return result.Select(d => new AudioDevice(d.Id,
                result.Count(o => o.Desc == d.Desc) > 1 ? Bracketed(d.Friendly) ?? d.Friendly : d.Desc,
                d.Id == defaultId))
            .ToList();
    });

    static string? Bracketed(string name)
    {
        int open = name.IndexOf('('), close = name.LastIndexOf(')');
        return open >= 0 && close > open + 1 ? name[(open + 1)..close].Trim() : null;
    }

    public Task SetDefaultDeviceAsync(string id) => _worker.Run(() =>
    {
        try
        {
            PolicyConfig.SetDefaultOutput(id);
            Log.Info("Default output device changed");
        }
        catch (Exception ex)
        {
            Log.Error("Changing output device", ex);
        }
        // Volume and apps belong to the device; follow the new one.
        bool listening = _masterDevice != null;
        ReleaseMaster();
        if (listening) DefaultDevice().AudioEndpointVolume.OnVolumeNotification += OnMasterNotification;
        if (_appsManager != null)
        {
            ReleaseApps();
            OpenAppsCore();
        }
        return 0;
    });

    // ---- Per-app volume ----

    /// <summary>Watches for apps starting to play while the Sound screen is open.</summary>
    public Task OpenAppsAsync() => _worker.Run(() => { ReleaseApps(); OpenAppsCore(); return 0; });

    public void CloseApps() => _worker.Run(() => { ReleaseApps(); return 0; });

    void OpenAppsCore()
    {
        try
        {
            _appsDevice = Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _appsManager = _appsDevice.AudioSessionManager;
            _appsManager.OnSessionCreated += OnSessionCreated;
        }
        catch (Exception ex)
        {
            Log.Error("Opening app volumes", ex);
        }
    }

    void ReleaseApps()
    {
        if (_appsManager != null)
        {
            try { _appsManager.OnSessionCreated -= OnSessionCreated; } catch { }
            _appsManager.Dispose();
            _appsManager = null;
        }
        _appsDevice?.Dispose();
        _appsDevice = null;
        _sessionsByApp = new();
    }

    void OnSessionCreated(object sender, IAudioSessionControl session) => AppsChanged?.Invoke();

    /// <summary>Apps with sound sessions on the current output, playing ones first.</summary>
    public Task<List<AppVolume>> ReadAppsAsync() => _worker.Run(() =>
    {
        var apps = new List<(AppVolume App, bool Active)>();
        var byApp = new Dictionary<string, List<AudioSessionControl>>();
        try
        {
            if (_appsManager == null) return new List<AppVolume>();
            _appsManager.RefreshSessions();
            var sessions = _appsManager.Sessions;
            var info = new Dictionary<string, (string Name, string? Path, bool Active, float Volume)>();
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.IsSystemSoundsSession || session.State == AudioSessionState.AudioSessionStateExpired) continue;
                var (key, name, path) = DescribeProcess((int)session.GetProcessID);
                if (key == null) continue;
                bool active = session.State == AudioSessionState.AudioSessionStateActive;
                if (!byApp.TryGetValue(key, out var list)) byApp[key] = list = new();
                list.Add(session);
                info[key] = info.TryGetValue(key, out var seen)
                    ? (seen.Name, seen.Path, seen.Active || active, Math.Max(seen.Volume, session.SimpleAudioVolume.Volume))
                    : (name, path, active, session.SimpleAudioVolume.Volume);
            }
            foreach (var (key, (name, path, active, volume)) in info)
                apps.Add((new AppVolume(key, name, Icon(path), (int)Math.Round(volume * 100)), active));
        }
        catch (Exception ex)
        {
            Log.Error("Reading app volumes", ex);
        }
        _sessionsByApp = byApp;
        return apps.OrderByDescending(a => a.Active).ThenBy(a => a.App.Name).Select(a => a.App).ToList();
    });

    public void SetApp(string key, int percent)
    {
        if (!_appSenders.TryGetValue(key, out var sender))
            _appSenders[key] = sender = new LatestOnly<int>(_worker, "Setting app volume", value =>
            {
                if (!_sessionsByApp.TryGetValue(key, out var sessions)) return;
                foreach (var session in sessions)
                {
                    session.SimpleAudioVolume.Volume = value / 100f;
                    if (value > 0 && session.SimpleAudioVolume.Mute) session.SimpleAudioVolume.Mute = false;
                }
            });
        sender.Post(Math.Clamp(percent, 0, 100));
    }

    /// <summary>Groups sessions by program and gives it a readable name ("Spotify", "Brave Browser").</summary>
    static (string? Key, string Name, string? Path) DescribeProcess(int pid)
    {
        if (pid <= 0) return (null, "", null);
        try
        {
            using var process = Process.GetProcessById(pid);
            string? path = null;
            string name = process.ProcessName;
            try
            {
                path = process.MainModule?.FileName;
                if (path != null && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description)
                    name = description.Trim();
            }
            catch { /* protected process: keep the plain name */ }
            return (process.ProcessName.ToLowerInvariant(), name, path);
        }
        catch
        {
            return (null, "", null);  // already gone
        }
    }

    ImageSource? Icon(string? path)
    {
        if (path == null) return null;
        if (_icons.TryGetValue(path, out var cached)) return cached;
        ImageSource? icon = null;
        var info = new ShFileInfo();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon) != IntPtr.Zero
            && info.hIcon != IntPtr.Zero)
        {
            try
            {
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                icon = bitmap;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        return _icons[path] = icon;
    }

    public void Dispose() => _worker.Dispose();

    const uint ShgfiIcon = 0x100, ShgfiLargeIcon = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);
}
