using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClawControl.Core;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Capture;

/// <summary>
/// The Quick tab's capture tiles, styled like the Wi-Fi / Bluetooth tiles: Screenshot (A takes
/// one: the panel closes first, a short flash says it's saved) and Record (A starts; while
/// recording the tile is red with the time, and A stops). Each › opens its own screen (size and
/// folder for screenshots; frame rate, quality, sound and folder for recordings).
/// </summary>
public sealed class CaptureRow : UserControl
{
    static readonly Brush RecordingBrush = Ui.Hex("#FFFF3B30");

    readonly CaptureService _capture;
    readonly Tile _shot, _record;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public CaptureRow(CaptureService capture, Action openScreenshot, Action openRecord)
    {
        _capture = capture;
        _shot = new Tile("", "Screenshot", () => _capture.Screenshot(), openScreenshot);
        _record = new Tile("", "Record", () => _capture.ToggleRecording(), openRecord);
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(_shot.View);
        Grid.SetColumn(_record.View, 1);
        grid.Children.Add(_record.View);
        Content = grid;

        _capture.Changed += () => Dispatcher.BeginInvoke(Refresh);
        _clock.Tick += (_, _) => Refresh();
        // The time only runs while it's on screen.
        IsVisibleChanged += (_, _) => Refresh();
        Refresh();
    }

    void Refresh()
    {
        var since = _capture.RecordingSince;
        _record.Show(since != null ? Elapsed(DateTime.Now - since.Value) : "Record", since != null ? RecordingBrush : null);
        if (since != null && IsVisible) _clock.Start();
        else _clock.Stop();
    }

    static string Elapsed(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    /// <summary>A rounded tile: the main part (A) and a › (the Capture screen).</summary>
    sealed class Tile
    {
        public readonly Border View;
        readonly TextBlock _title;

        public Tile(string glyph, string title, Action onPress, Action onMore)
        {
            var icon = Ui.Glyph(glyph, 17);
            icon.Margin = new Thickness(0, 0, 9, 0);
            _title = Ui.Text(title, bold: true);
            _title.FontSize = 14;
            _title.VerticalAlignment = VerticalAlignment.Center;
            var line = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            line.Children.Add(icon);
            line.Children.Add(_title);
            var main = new Button { Style = Ui.Style("RowButton"), Content = line, MinHeight = 54 };
            main.Click += (_, _) => onPress();
            var more = new Button { Style = Ui.Style("IconButton"), Content = "", FontSize = 12, Width = 30, Margin = new Thickness(0, 0, 2, 0) };
            more.Click += (_, _) => onMore();
            var tile = new DockPanel();
            DockPanel.SetDock(more, Dock.Right);
            tile.Children.Add(more);
            tile.Children.Add(main);
            View = new Border { CornerRadius = new CornerRadius(16), Margin = new Thickness(3), Child = tile, Background = Ui.Brush("CardBrush") };
        }

        public void Show(string title, Brush? lit)
        {
            _title.Text = title;
            View.Background = lit ?? Ui.Brush("CardBrush");
        }
    }
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
