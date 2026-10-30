using System.Windows;
using System.Windows.Controls;
using ClawControl.Core;
using ClawControl.Features.Graphics;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Capture;

/// <summary>
/// Screenshot's › : how big screenshots are (full size, or at most a smaller size, same shape)
/// and where they go (FolderScreen), plus the folder itself. Labels and values only.
/// </summary>
public sealed class ScreenshotScreen : UserControl, IPage
{
    static readonly int[] Heights = [1080, 1000, 900, 800, 720, 600, 540];

    readonly Settings _settings;
    readonly ListRow _size, _folder;
    readonly PickerScreen _sizes;
    readonly FolderScreen _folders;

    public string Id => "screenshot";
    public string Title => "Screenshot";
    public FrameworkElement View => this;
    public IEnumerable<IPage> Screens => [_sizes, _folders];

    public ScreenshotScreen(Settings settings, Action<IPage> openSubPage, Action back)
    {
        _settings = settings;
        _sizes = new PickerScreen("screenshot-size", "Size", () => [Sizes()], () => _settings.ScreenshotHeight, value =>
        {
            _settings.ScreenshotHeight = (int)value;
            _settings.SaveSoon();
            _sizes!.Refresh();
        });
        _folders = new FolderScreen("screenshot-folder", () => CaptureService.FolderOf(_settings.ScreenshotFolder), folder =>
        {
            _settings.ScreenshotFolder = folder;
            _settings.SaveSoon();
            back();
        });
        _size = new ListRow(Ui.Glyph("", 16), Ui.RowIconBrush, "Size");
        _size.Button.Click += (_, _) => openSubPage(_sizes);
        _folder = new ListRow(Ui.Glyph("", 16), Ui.Hex("#FF0A84FF"), "Save to");
        _folder.Button.Click += (_, _) => openSubPage(_folders);
        var open = new ListRow(Ui.Glyph("", 16), Ui.RowIconBrush, "Open folder");
        open.Button.Click += (_, _) => CaptureService.OpenFolder(_settings.ScreenshotFolder);
        Content = Ui.Stack(ListRow.Card(_size), ListRow.Card(_folder, open));
    }

    /// <summary>The screen's full size, then smaller ones of the same shape.</summary>
    static IEnumerable<(string, object)> Sizes()
    {
        var mode = DisplayModes.Current();
        yield return ($"{mode.Width} × {mode.Height}", 0);
        foreach (int height in Heights.Where(h => h < mode.Height))
            yield return ($"{(int)Math.Round(mode.Width * (double)height / mode.Height / 2) * 2} × {height}", height);
    }

    public void OnShown()
    {
        var mode = DisplayModes.Current();
        int height = _settings.ScreenshotHeight;
        _size.Value.Text = height > 0 && height < mode.Height
            ? $"{(int)Math.Round(mode.Width * (double)height / mode.Height / 2) * 2} × {height}"
            : $"{mode.Width} × {mode.Height}";
        _folder.Value.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(CaptureService.FolderOf(_settings.ScreenshotFolder)));
    }
}

/// <summary>
/// Record's › : how recordings are made (frame rate, quality, the game's sound) and where they
/// go (FolderScreen), plus the folder itself. Labels and values only.
/// </summary>
public sealed class RecordScreen : UserControl, IPage
{
    static readonly int[] Rates = [30, 60];

    readonly Settings _settings;
    readonly Button[] _rates, _quality;
    readonly Button _sound;
    readonly ListRow _folder;
    readonly FolderScreen _folders;

    public string Id => "record";
    public string Title => "Record";
    public FrameworkElement View => this;
    public IEnumerable<IPage> Screens => [_folders];

    public RecordScreen(Settings settings, Action<IPage> openSubPage, Action back)
    {
        _settings = settings;
        (var rates, _rates) = Ui.Segment(["30", "60"], i => Change(() => settings.RecordFps = Rates[i]));
        (var quality, _quality) = Ui.Segment(["Standard", "High"], i => Change(() => settings.RecordHigh = i == 1));
        _sound = Ui.Switch("Game sound", () => Change(() => settings.RecordSound = !settings.RecordSound));
        _folders = new FolderScreen("record-folder", () => CaptureService.FolderOf(_settings.RecordFolder), folder =>
        {
            _settings.RecordFolder = folder;
            _settings.SaveSoon();
            back();
        });
        _folder = new ListRow(Ui.Glyph("", 16), Ui.Hex("#FF0A84FF"), "Save to");
        _folder.Button.Click += (_, _) => openSubPage(_folders);
        var open = new ListRow(Ui.Glyph("", 16), Ui.RowIconBrush, "Open folder");
        open.Button.Click += (_, _) => CaptureService.OpenFolder(_settings.RecordFolder);

        Content = Ui.Stack(
            ListRow.Card([Ui.Labelled("Frame rate", rates), Ui.Labelled("Quality", quality), _sound]),
            ListRow.Card(_folder, open));
        Refresh();
    }

    void Change(Action change)
    {
        change();
        _settings.SaveSoon();
        Refresh();
    }

    void Refresh()
    {
        Ui.Choose(_rates, Array.IndexOf(Rates, _settings.RecordFps));
        Ui.Choose(_quality, _settings.RecordHigh ? 1 : 0);
        Ui.SetOn(_sound, _settings.RecordSound);
        _folder.Value.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(CaptureService.FolderOf(_settings.RecordFolder)));
    }

    public void OnShown() => Refresh();
}
