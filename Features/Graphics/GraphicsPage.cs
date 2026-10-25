using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Graphics;

/// <summary>
/// The Graphics tab: FPS limit bar (one step past 120 = Unlimited, the limiter off), circles for
/// frame generation, upscaling, sharpening and FreeSync (each only if the driver has it), the
/// sharpness bar (only while sharpening is on), then rows that open the resolution, refresh
/// rate, Windows scaling and graphics memory pickers. Labels and values only.
/// </summary>
public sealed class GraphicsPage : UserControl, IPage
{
    /// <summary>FPS limit bar: Radeon Chill's lowest cap (30) to 120 in steps of 5, then Unlimited.</summary>
    const int MinFps = 30, MaxFps = 120, NoLimit = MaxFps + 5;

    readonly GraphicsService _gfx;
    readonly FillSlider _fps, _upscalingSharpness, _sharpness;
    readonly Toggle _frameGen, _upscaling, _sharpen, _freeSync;
    readonly ListRow _resolution, _refresh, _scaling, _memory;
    readonly PickerScreen[] _screens;
    readonly Button _restart;
    readonly TextBlock _error;
    readonly DispatcherTimer _disarm = new() { Interval = TimeSpan.FromSeconds(4) };
    bool _restartArmed;
    bool _refreshing;

    public string Id => "graphics";
    public string Title => "Graphics";
    public FrameworkElement View => this;

    public GraphicsPage(GraphicsService gfx, Action<IPage> openSubPage)
    {
        _gfx = gfx;

        _fps = Ui.Slider("", "FPS limit", NoLimit, 5);
        _fps.Minimum = MinFps;
        _fps.ValueChanged += (_, e) =>
        {
            _fps.ValueText = FpsText(e.NewValue);
            if (!_refreshing) _gfx.SetFpsLimit(e.NewValue >= NoLimit ? 0 : (int)Math.Round(e.NewValue));
        };

        _frameGen = new Toggle(Ui.LineIcon("FrameGenIcon", 20), "#FFBF5AF2", "Frame gen", () => _gfx.SetFrameGen(!_gfx.Amd.FrameGenOn));
        _upscaling = new Toggle(Ui.LineIcon("UpscaleIcon", 20), "#FF30B0C7", "Upscaling", () => _gfx.SetUpscaling(!_gfx.Amd.UpscalingOn));
        _sharpen = new Toggle(Ui.LineIcon("SharpenIcon", 20), "#FFFF9F0A", "Sharpen", _gfx.ToggleSharpening);
        _freeSync = new Toggle(Ui.LineIcon("FreeSyncIcon", 20), "#FFFF453A", "FreeSync", () => _gfx.SetFreeSync(!_gfx.Amd.FreeSyncOn));
        var toggles = new UniformGrid { Rows = 1, Margin = new Thickness(4, 10, 4, 4) };
        foreach (var toggle in new[] { _frameGen, _upscaling, _sharpen, _freeSync }) toggles.Children.Add(toggle.View);

        // Each sharpness bar is only shown while its circle is on: upscaling sharpens what it
        // stretches; the Sharpen circle is a separate filter for any game.
        _upscalingSharpness = Ui.Slider(null, "Upscaling sharpness", 100, 5);
        _upscalingSharpness.LineIcon = (Geometry)FindResource("UpscaleIcon");
        _upscalingSharpness.ValueChanged += (_, e) =>
        {
            _upscalingSharpness.ValueText = $"{(int)Math.Round(e.NewValue)}%";
            if (!_refreshing) _gfx.SetUpscalingSharpness((int)Math.Round(e.NewValue));
        };

        _sharpness = Ui.Slider(null, "Sharpness", 100, 10);
        _sharpness.Minimum = 10;
        _sharpness.LineIcon = (Geometry)FindResource("SharpenIcon");
        _sharpness.ValueChanged += (_, e) =>
        {
            _sharpness.ValueText = $"{(int)Math.Round(e.NewValue)}%";
            if (!_refreshing) _gfx.SetSharpness((int)Math.Round(e.NewValue));
        };

        var resolution = new PickerScreen("graphics-resolution", "Resolution",
            () => _gfx.Resolutions.Select(group => group.Select(s => ($"{s.Width} × {s.Height}", (object)s))),
            () => (_gfx.Mode.Width, _gfx.Mode.Height),
            value => { var (w, h) = ((int, int))value; _gfx.SetResolution(w, h); });
        var refresh = new PickerScreen("graphics-refresh", "Refresh rate",
            () => [_gfx.RefreshRates.Select(hz => ($"{hz} Hz", (object)hz))],
            () => _gfx.Mode.Hz,
            value => _gfx.SetRefreshRate((int)value));
        var scaling = new PickerScreen("graphics-scaling", "Scaling",
            () => [_gfx.Scale.Choices.Select(percent => ($"{percent}%", (object)percent))],
            () => _gfx.Scale.Percent,
            value => _gfx.SetScale((int)value));

        _restart = new Button { Style = Ui.Style("CardButton"), Tag = "selected", MinHeight = 48, Margin = new Thickness(4, 8, 4, 4),
            HorizontalContentAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
        _restart.Click += (_, _) => Restart();
        _disarm.Tick += (_, _) => { _disarm.Stop(); _restartArmed = false; ShowRestart(); };
        var memory = new PickerScreen("graphics-memory", "Graphics memory",
            () => [_gfx.MemoryChoices.Select(i => (MemoryText(_gfx.Amd.MemoryOptions[i]), (object)i))],
            () => _gfx.Amd.MemoryChoice,
            value => _gfx.SetMemory((int)value)) { Footer = _restart };
        _screens = [resolution, refresh, scaling, memory];

        _resolution = new ListRow(Ui.Glyph("", 16), Ui.RowIconBrush, "Resolution");
        _resolution.Button.Click += (_, _) => openSubPage(resolution);
        _refresh = new ListRow(Ui.Glyph("", 16), Ui.RowIconBrush, "Refresh rate");
        _refresh.Button.Click += (_, _) => openSubPage(refresh);
        _scaling = new ListRow(Ui.Glyph("", 16), Ui.RowIconBrush, "Scaling");
        _scaling.Button.Click += (_, _) => openSubPage(scaling);
        _memory = new ListRow(Ui.Glyph("", 16), Ui.Hex("#FF34C759"), "Graphics memory");
        _memory.Button.Click += (_, _) => openSubPage(memory);

        // Errors are the only status text.
        _error = new TextBlock { Style = Ui.Style("CardDetail"), Margin = new Thickness(6, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
            Text = "AMD's graphics settings aren't available.", Visibility = Visibility.Collapsed };

        Content = Ui.Stack(_fps, toggles, _upscalingSharpness, _sharpness, ListRow.Card(_resolution, _refresh, _scaling, _memory), _error);

        _gfx.Changed += () => Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    static string FpsText(double fps) => fps >= NoLimit - 0.5 ? "Unlimited" : $"{(int)Math.Round(fps)}";

    static string MemoryText(MemoryOption option) => $"{option.Gb:0.#} GB";

    public FrameworkElement DefaultFocus => _fps;

    public IEnumerable<IPage> Screens => _screens;

    public void OnShown()
    {
        Refresh();
        _ = _gfx.RefreshAsync();
    }

    void Refresh()
    {
        var amd = _gfx.Amd;
        // A bar under the D-pad keeps what was asked for: the driver's reply can be a step behind.
        _refreshing = true;
        if (!_fps.IsFocused)
        {
            _fps.Value = amd.FpsLimitOn ? Math.Clamp(amd.FpsLimit, MinFps, MaxFps) : NoLimit;
            _fps.ValueText = FpsText(_fps.Value);
        }
        if (!_upscalingSharpness.IsFocused && amd.UpscalingOn)
        {
            _upscalingSharpness.Value = amd.UpscalingSharpness;
            _upscalingSharpness.ValueText = $"{amd.UpscalingSharpness}%";
        }
        if (!_sharpness.IsFocused && amd.SharpeningOn)
        {
            _sharpness.Value = amd.Sharpness;
            _sharpness.ValueText = $"{amd.Sharpness}%";
        }
        _refreshing = false;

        _fps.Visibility = Shown(amd.FpsLimitSupported);
        _upscalingSharpness.Visibility = Shown(amd.UpscalingSupported && amd.UpscalingOn);
        _sharpness.Visibility = Shown(amd.SharpeningSupported && amd.SharpeningOn);
        _frameGen.Show(amd.FrameGenSupported, amd.FrameGenOn);
        _upscaling.Show(amd.UpscalingSupported, amd.UpscalingOn);
        _sharpen.Show(amd.SharpeningSupported, amd.SharpeningOn);
        _freeSync.Show(amd.FreeSyncSupported, amd.FreeSyncOn);

        _resolution.Value.Text = _gfx.Mode.Width > 0 ? $"{_gfx.Mode.Width}×{_gfx.Mode.Height}" : "";
        _refresh.Value.Text = _gfx.Mode.Hz > 0 ? $"{_gfx.Mode.Hz} Hz" : "";
        _scaling.View.Visibility = Shown(_gfx.Scale.Choices.Count > 1);
        _scaling.Value.Text = _gfx.Scale.Percent > 0 ? $"{_gfx.Scale.Percent}%" : "";
        _memory.View.Visibility = Shown(amd.MemoryOptions.Count > 0);
        _memory.Value.Text = amd.MemoryChoice >= 0 ? MemoryText(amd.MemoryOptions[amd.MemoryChoice]) : "";
        _error.Visibility = Shown(!amd.Available && _gfx.Modes.Count > 0);
        ShowRestart();
        foreach (var screen in _screens) screen.Refresh();
    }

    static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Only while a new size waits for a restart; the first A arms, the second restarts.</summary>
    void ShowRestart()
    {
        _restart.Visibility = Shown(_gfx.RestartNeeded);
        _restart.Content = Ui.Text(_restartArmed ? "Press A again to restart" : "Restart now", bold: true);
    }

    void Restart()
    {
        if (_restartArmed)
        {
            GraphicsService.Restart();
            return;
        }
        _restartArmed = true;
        _disarm.Start();
        ShowRestart();
    }

    /// <summary>A coloured circle that lights up while its setting is on, with its name under it.</summary>
    sealed class Toggle
    {
        readonly Button _button;
        public readonly FrameworkElement View;

        public Toggle(FrameworkElement icon, string color, string name, Action onClick)
        {
            _button = new Button { Style = Ui.Style("ModeButton"), Background = Ui.Hex(color), Content = icon };
            _button.Click += (_, _) => onClick();
            var caption = new TextBlock
            {
                Text = name, Style = Ui.Style("CardDetail"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0),
            };
            View = Ui.Stack(_button, caption);
        }

        public void Show(bool supported, bool on)
        {
            View.Visibility = Shown(supported);
            _button.Tag = on ? "selected" : null;
        }
    }
}
