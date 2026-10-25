using System.Management;
using ClawControl.Core;

namespace ClawControl.Hardware;

/// <summary>
/// Built-in screen brightness through Windows' own WMI interface (root\WMI WmiMonitorBrightness),
/// the same one the Windows quick settings slider uses. Calls run on their own worker so they never
/// queue behind (or hold up) firmware calls, and quick slider moves only send the latest value.
/// </summary>
public sealed class Brightness : IDisposable
{
    readonly HardwareWorker _worker = new();
    int _pending = -1;          // latest value asked for and not yet sent, or -1
    DateTime _lastSetAt;

    /// <summary>Reads the current brightness (0-100), or null if the screen doesn't support it.</summary>
    public Task<int?> ReadAsync() => _worker.Run<int?>(() =>
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject monitor in searcher.Get())
                using (monitor)
                    return Convert.ToInt32(monitor["CurrentBrightness"]);
        }
        catch (Exception ex)
        {
            Log.Error("Reading brightness", ex);
        }
        return null;
    });

    /// <summary>True for a moment after the user moved the slider, so a read doesn't jump it back.</summary>
    public bool RecentlySet => DateTime.UtcNow - _lastSetAt < TimeSpan.FromSeconds(1);

    public void Set(int percent)
    {
        _lastSetAt = DateTime.UtcNow;
        // Only the first of a burst schedules work; the rest just replace the value it will send.
        if (Interlocked.Exchange(ref _pending, Math.Clamp(percent, 0, 100)) != -1) return;
        _worker.Run(() =>
        {
            int value = Interlocked.Exchange(ref _pending, -1);
            if (value < 0) return 0;
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject monitor in searcher.Get())
                    using (monitor)
                        monitor.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)value });
            }
            catch (Exception ex)
            {
                Log.Error("Setting brightness", ex);
            }
            return 0;
        });
    }

    public void Dispose() => _worker.Dispose();
}
