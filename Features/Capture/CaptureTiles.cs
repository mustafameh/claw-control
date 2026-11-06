using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClawControl.Core;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Capture;

/// <summary>
/// The Quick tab's capture tiles, styled like the Wi-Fi / Bluetooth tiles and each a tile of its
/// own: Screenshot (A takes one: the panel closes first, a short flash says it's saved) and Record
/// (A starts; while recording the tile is red with the time, and A stops). Each › opens its own
/// screen (size and folder for screenshots; frame rate, quality, sound and folder for recordings).
/// </summary>
public sealed class CaptureTiles
{
    static readonly Brush RecordingBrush = Ui.Hex("#FFFF3B30");

    readonly CaptureService _capture;
    readonly QuickTile _shot, _record;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public FrameworkElement Screenshot => _shot.View;
    public FrameworkElement Record => _record.View;

    public CaptureTiles(CaptureService capture, Action openScreenshot, Action openRecord)
    {
        _capture = capture;
        _shot = new QuickTile("", "Screenshot", () => _capture.Screenshot(), openScreenshot);
        _record = new QuickTile("", "Record", () => _capture.ToggleRecording(), openRecord);

        _capture.Changed += () => _record.View.Dispatcher.BeginInvoke(Refresh);
        _clock.Tick += (_, _) => Refresh();
        // The time only runs while it's on screen.
        _record.View.IsVisibleChanged += (_, _) => Refresh();
        Refresh();
    }

    void Refresh()
    {
        var since = _capture.RecordingSince;
        _record.Show(since != null ? Elapsed(DateTime.Now - since.Value) : "Record", since != null ? RecordingBrush : null);
        if (since != null && _record.View.IsVisible) _clock.Start();
        else _clock.Stop();
    }

    static string Elapsed(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
}

/// <summary>Screenshots and recordings from the panel: closes it first (it isn't in the
/// picture), names files after the app in front, and keeps the panel out of a running recording.</summary>
public sealed class CaptureService(ScreenCapture capture, Settings settings, Func<Task> hidePanel, Func<string?> frontApp)
{
    public DateTime? RecordingSince => capture.RecordingSince;

    public event Action? Changed
    {
        add => capture.Changed += value;
        remove => capture.Changed -= value;
    }

    public Settings Settings => settings;

    public async void Screenshot()
    {
        string name = frontApp() ?? "Screen";
        await hidePanel();
        if (await capture.ScreenshotAsync(name, settings.ScreenshotFolder, settings.ScreenshotHeight) != null) CaptureFlash.Show();
    }

    public async void ToggleRecording()
    {
        if (capture.RecordingSince != null)
        {
            capture.StopRecording();
            return;
        }
        string name = frontApp() ?? "Screen";
        await hidePanel();
        await capture.StartRecordingAsync(name, new RecordOptions(settings.RecordFps, settings.RecordHigh, settings.RecordSound), settings.RecordFolder);
    }

    /// <summary>The folder a setting names, or the usual one (Videos\Captures) when unset or gone.</summary>
    public static string FolderOf(string chosen) => !string.IsNullOrEmpty(chosen) && Directory.Exists(chosen) ? chosen : ScreenCapture.Folder;

    public static void OpenFolder(string chosen)
    {
        try
        {
            string folder = FolderOf(chosen);
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Opening the captures folder", ex);
        }
    }
}
