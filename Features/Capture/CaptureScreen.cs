using System.Windows;
using System.Windows.Controls;
using ClawControl.UI;

namespace ClawControl.Features.Capture;

/// <summary>
/// The Capture screen (› on Screenshot / Record): the two tiles again, how recordings are made
/// (frame rate, quality, the game's sound) and the folder they go to. Labels and values only.
/// </summary>
public sealed class CaptureScreen : UserControl, IPage
{
    static readonly int[] Rates = [30, 60];

    readonly CaptureService _capture;
    readonly Button[] _rates, _quality;
    readonly Button _sound;

    public string Id => "capture";
    public string Title => "Capture";
    public FrameworkElement View => this;

    public CaptureScreen(CaptureService capture)
    {
        _capture = capture;
        var settings = capture.Settings;
        var tiles = new CaptureRow(capture, () => { });

        (var rates, _rates) = Ui.Segment(["30", "60"], i => Change(() => settings.RecordFps = Rates[i]));
        (var quality, _quality) = Ui.Segment(["Standard", "High"], i => Change(() => settings.RecordHigh = i == 1));
        _sound = Ui.Switch("Game sound", () => Change(() => settings.RecordSound = !settings.RecordSound));
        var folder = new ListRow(Ui.Glyph("", 16), Ui.RowIconBrush, "Captures");
        folder.Value.Text = "Videos";
        folder.Button.Click += (_, _) => CaptureService.OpenFolder();

        Content = Ui.Stack(
            tiles,
            ListRow.Card([Labelled("Frame rate", rates), Labelled("Quality", quality), _sound]),
            ListRow.Card(folder));
        Refresh();
    }

    /// <summary>A name on the left, its chips on the right.</summary>
    static FrameworkElement Labelled(string name, FrameworkElement chips)
    {
        var title = Ui.Text(name, bold: true);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(12, 0, 8, 0);
        chips.Width = 190;
        DockPanel.SetDock(chips, Dock.Right);
        return new DockPanel { MinHeight = 50, Children = { chips, title } };
    }

    void Change(Action change)
    {
        change();
        _capture.Settings.SaveSoon();
        Refresh();
    }

    void Refresh()
    {
        var settings = _capture.Settings;
        Ui.Choose(_rates, Array.IndexOf(Rates, settings.RecordFps));
        Ui.Choose(_quality, settings.RecordHigh ? 1 : 0);
        Ui.SetOn(_sound, settings.RecordSound);
    }

    public void OnShown() => Refresh();
}
