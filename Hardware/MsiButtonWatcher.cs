using System.Management;
using ClawControl.Core;

namespace ClawControl.Hardware;

/// <summary>
/// Listens for the firmware events the Claw's dedicated buttons raise (WMI root\WMI, MSI_Event).
/// The low byte of MSIEvt is the button code. Several programs can listen at once.
/// </summary>
public sealed class MsiButtonWatcher : IDisposable
{
    public const int CenterButton = 41;          // "MSI Center M" button, short press
    public const int QuickSettingsButton = 88;   // Quick Settings button (Game Bar)
    public const int LongPressWinTab = 42;
    public const int LongPressFullScreenXbox = 49;

    ManagementEventWatcher? _watcher;

    /// <summary>Raised on a WMI thread with the button code.</summary>
    public event Action<int>? Pressed;

    public void Start()
    {
        _watcher = new ManagementEventWatcher(
            new ManagementScope(@"\\.\root\WMI"),
            new WqlEventQuery("SELECT * FROM MSI_Event"));
        _watcher.EventArrived += (_, e) =>
        {
            try
            {
                int code = Convert.ToInt32(e.NewEvent["MSIEvt"]) & 0xFF;
                Log.Detail($"Button event {code}");
                Pressed?.Invoke(code);
            }
            catch (Exception ex)
            {
                Log.Error("Handling button event", ex);
            }
        };
        _watcher.Start();
        Log.Info("Listening for MSI button events");
    }

    public void Dispose()
    {
        try { _watcher?.Stop(); } catch { }
        _watcher?.Dispose();
        _watcher = null;
    }
}
