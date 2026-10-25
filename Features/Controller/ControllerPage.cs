using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ClawControl.Input;
using ClawControl.UI;

namespace ClawControl.Features.Controller;

/// <summary>
/// The Controller tab: mode circles (applied when the panel closes), the lights bar (opens the
/// Lights screen like volume opens Sound), left / right rumble bars, then one card of rows for
/// gyro (with a switch), sticks, triggers and buttons that open their screens.
/// Everything is stored on the controller chip itself (ControllerService).
/// </summary>
public sealed class ControllerPage : UserControl, IPage
{
    readonly ControllerService _pad;
    readonly Dictionary<Button, PadMode> _modes = new();
    readonly TextBlock _modeCaption = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 4) };
    readonly FillSlider _lights, _leftRumble, _rightRumble;
    readonly ListRow _gyro, _sticks, _triggers, _buttons;
    readonly Panel _rest;
    readonly IPage[] _screens;
    readonly DispatcherTimer _buzz = new() { Interval = TimeSpan.FromMilliseconds(350) };
    bool _buzzLeft;
    bool _refreshing;

    public string Id => "controller";
    public string Title => "Controller";
    public FrameworkElement View => this;

    public ControllerPage(ControllerService pad, Action<IPage> openSubPage)
    {
        _pad = pad;
        var lights = new LightsScreen(pad);
        var gyro = new GyroScreen(pad);
        var sticks = new SticksScreen(pad);
        var triggers = new TriggersScreen(pad);
        var buttons = new ButtonsScreen(pad);
        _screens = [lights, gyro, sticks, triggers, buttons];

        // The Quick tab's circles plus Off; the caption names the one under the D-pad.
        var modes = new UniformGrid { Rows = 1, Margin = new Thickness(4, 4, 4, 0) };
        foreach (var mode in new[] { PadMode.Xbox, PadMode.PlayStation, PadMode.Desktop, PadMode.Off })
        {
            var button = new Button { Style = Ui.Style("ModeButton"), Background = Ui.Look(mode).Color, Content = Ui.ModeIcon(mode, 20) };
            button.Click += (_, _) => _pad.SetMode(mode);
            button.GotFocus += (_, _) => UpdateModeCaption();
            button.LostFocus += (_, _) => UpdateModeCaption();
            _modes[button] = mode;
            modes.Children.Add(button);
        }
        _modeCaption.Style = Ui.Style("CardDetail");

        // Like volume: left/right sets the brightness (0 = off), A or the arrow opens Lights.
        _lights = Ui.Slider("", "Lights", 100, 10);
        _lights.ShowMore = true;
        _lights.ValueChanged += (_, e) =>
        {
            _lights.ValueText = PercentText(e.NewValue);
            if (!_refreshing) _pad.SetLightBrightness((int)Math.Round(e.NewValue));
        };
        PanelInput.SetAccept(_lights, () => openSubPage(lights));
        _lights.MoreRequested += () => openSubPage(lights);

        _leftRumble = RumbleSlider(left: true, "Left rumble");
        _rightRumble = RumbleSlider(left: false, "Right rumble");
        // Feel the changed motor once its slider settles.
        _buzz.Tick += (_, _) => { _buzz.Stop(); _ = PadState.Buzz(_buzzLeft); };

        _gyro = new ListRow(Ui.LineIcon("GyroIcon"), Ui.GyroBrush, "Gyro", withSwitch: true);
        _gyro.Button.Click += (_, _) => _pad.SetGyro(!_pad.GyroOn);
        _gyro.More!.Click += (_, _) => openSubPage(gyro);
        _sticks = new ListRow(Ui.LineIcon("StickIcon"), Ui.RowIconBrush, "Sticks");
        _sticks.Button.Click += (_, _) => openSubPage(sticks);
        _triggers = new ListRow(Ui.LineIcon("TriggerIcon"), Ui.RowIconBrush, "Triggers");
        _triggers.Button.Click += (_, _) => openSubPage(triggers);
        _buttons = new ListRow(Ui.Glyph("", 16), Ui.DesktopBrush, "Buttons");
        _buttons.Button.Click += (_, _) => openSubPage(buttons);

        // Everything but the mode dims while the controller is off (still changeable).
        _rest = Ui.Stack(_lights, _leftRumble, _rightRumble, ListRow.Card(_gyro, _sticks, _triggers, _buttons));
        Content = Ui.Stack(modes, _modeCaption, _rest);

        _pad.Changed += () => Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    FillSlider RumbleSlider(bool left, string label)
    {
        var slider = Ui.Slider("", label, 100, 5);
        slider.ValueChanged += (_, e) =>
        {
            slider.ValueText = PercentText(e.NewValue);
            if (_refreshing) return;
            _pad.SetRumble(left, (int)Math.Round(e.NewValue));
            _buzzLeft = left;
            _buzz.Stop();
            _buzz.Start();
        };
        return slider;
    }

    /// <summary>Rumble and lights: a percentage, or "Off" at 0.</summary>
    static string PercentText(double percent) => percent < 0.5 ? "Off" : $"{(int)Math.Round(percent)}%";

    public FrameworkElement DefaultFocus => _modes.First(pair => pair.Value == _pad.Mode).Key;

    public IEnumerable<IPage> Screens => _screens;

    public void OnShown()
    {
        if (!_pad.Ready) _ = _pad.RefreshAsync();
        Refresh();
    }

    /// <summary>Names the mode under the D-pad (or the chosen one); a new choice applies on close.</summary>
    void UpdateModeCaption()
    {
        var focused = _modes.Keys.FirstOrDefault(b => b.IsFocused);
        var mode = focused != null ? _modes[focused] : _pad.Mode;
        string name = mode == PadMode.Off ? "Controller off" : $"{Ui.Look(mode).Name} mode";
        _modeCaption.Text = mode == _pad.Mode && _pad.ModePending ? $"{name} when you close the panel" : name;
    }

    void Refresh()
    {
        foreach (var (button, mode) in _modes) button.Tag = mode == _pad.Mode ? "selected" : null;
        UpdateModeCaption();
        _rest.Opacity = _pad.Mode == PadMode.Off ? 0.4 : 1;

        if (!_pad.Ready)
        {
            _gyro.Value.Text = _sticks.Value.Text = _triggers.Value.Text = _buttons.Value.Text = "…";
            return;
        }
        _refreshing = true;
        _lights.Value = _pad.LightBrightness;
        _lights.ValueText = PercentText(_lights.Value);
        _leftRumble.Value = _pad.Rumble(left: true);
        _leftRumble.ValueText = PercentText(_leftRumble.Value);
        _rightRumble.Value = _pad.Rumble(left: false);
        _rightRumble.ValueText = PercentText(_rightRumble.Value);
        _refreshing = false;

        Ui.SetOn(_gyro.Button, _pad.GyroOn);
        _gyro.Value.Text = !_pad.GyroOn ? "Off"
            : (_pad.GyroAsMouse ? "Mouse" : "Right stick") + (_pad.GyroOnlyWhileLt ? " · hold LT" : "");

        int left = _pad.Deadzone(true), right = _pad.Deadzone(false);
        _sticks.Value.Text = left != right ? $"Dead zone {left}% · {right}%"
            : left == 0 ? "No dead zone" : $"Dead zone {left}%";

        string lt = TriggerText(true), rt = TriggerText(false);
        _triggers.Value.Text = lt == rt ? lt : $"LT {lt} · RT {rt}";

        int changed = ControllerService.Buttons.Count(b => _pad.MappedTo(b.Id) != b.Code || _pad.Turbo(b.Id));
        _buttons.Value.Text = changed == 0 ? "Default" : $"{changed} changed";
        _buttons.Value.Foreground = changed == 0 ? Ui.Brush("SubtleTextBrush") : Ui.ChangedBrush;
    }

    string TriggerText(bool left)
    {
        if (_pad.QuickTrigger(left)) return "Quick";
        int start = _pad.TriggerStart(left);
        int full = _pad.TriggerFull(left) is 0 or > 100 ? 100 : _pad.TriggerFull(left);
        return start == 0 && full == 100 ? "Full range" : $"{start}–{full}%";
    }
}
