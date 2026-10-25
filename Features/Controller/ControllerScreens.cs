using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClawControl.Input;
using ClawControl.UI;

namespace ClawControl.Features.Controller;

/// <summary>Base for the Controller sub screens: refreshes on chip changes while shown.</summary>
public abstract class ControllerScreen : UserControl, IPage
{
    protected readonly ControllerService Pad;
    protected bool Refreshing;
    bool _shown;

    protected ControllerScreen(ControllerService pad)
    {
        Pad = pad;
        Pad.Changed += () => Dispatcher.BeginInvoke(() => { if (_shown) RefreshNow(); });
    }

    public abstract string Id { get; }
    public abstract string Title { get; }
    public FrameworkElement View => this;

    public virtual void OnShown()
    {
        _shown = true;
        RefreshNow();
    }

    public virtual void OnHidden() => _shown = false;

    void RefreshNow()
    {
        Refreshing = true;
        try { Refresh(); }
        finally { Refreshing = false; }
    }

    protected abstract void Refresh();
}

/// <summary>Dead zones and outer edges for both sticks, with a live view of where each stick is.</summary>
public sealed class SticksScreen : ControllerScreen
{
    readonly StickView _left = new(), _right = new();
    readonly FillSlider _leftDead, _rightDead, _leftEdge, _rightEdge;
    readonly Button _invert, _swap;
    readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromMilliseconds(33) };

    public override string Id => "controller-sticks";
    public override string Title => "Sticks";

    public SticksScreen(ControllerService pad) : base(pad)
    {
        var views = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 4) };
        views.Children.Add(_left);
        views.Children.Add(_right);
        _leftDead = Ui.Slider(null, "Left dead zone", 50, 1);
        _rightDead = Ui.Slider(null, "Right dead zone", 50, 1);
        _leftEdge = Ui.Slider(null, "Left outer edge", 100, 1);
        _rightEdge = Ui.Slider(null, "Right outer edge", 100, 1);
        Hook(_leftDead, v => Pad.SetDeadzone(true, v));
        Hook(_rightDead, v => Pad.SetDeadzone(false, v));
        Hook(_leftEdge, v => Pad.SetOuterEdge(true, v));
        Hook(_rightEdge, v => Pad.SetOuterEdge(false, v));
        _invert = Ui.Switch("Invert up/down", () => Pad.SetInvertY(!Pad.InvertY));
        _swap = Ui.Switch("Swap sticks", () => Pad.SetSwapSticks(!Pad.SwapSticks));
        Content = Ui.Stack(views, _leftDead, _rightDead, _leftEdge, _rightEdge, _invert, _swap);
        _live.Tick += (_, _) =>
        {
            if (PadState.Read() is not { } s) return;
            _left.Show(s.LeftX, s.LeftY);
            _right.Show(s.RightX, s.RightY);
        };
    }

    void Hook(FillSlider slider, Action<int> apply) => slider.ValueChanged += (_, e) =>
    {
        slider.ValueText = $"{(int)e.NewValue}%";
        if (!Refreshing) apply((int)e.NewValue);
        RefreshViews();
    };

    public override void OnShown() { base.OnShown(); _live.Start(); }
    public override void OnHidden() { base.OnHidden(); _live.Stop(); }

    protected override void Refresh()
    {
        _leftDead.Value = Pad.Deadzone(true);
        _rightDead.Value = Pad.Deadzone(false);
        _leftEdge.Value = Pad.OuterEdge(true);
        _rightEdge.Value = Pad.OuterEdge(false);
        foreach (var s in new[] { _leftDead, _rightDead, _leftEdge, _rightEdge }) s.ValueText = $"{(int)s.Value}%";
        Ui.SetOn(_invert, Pad.InvertY);
        Ui.SetOn(_swap, Pad.SwapSticks);
        RefreshViews();
    }

    void RefreshViews()
    {
        _left.SetZones(_leftDead.Value / 100, _leftEdge.Value / 100);
        _right.SetZones(_rightDead.Value / 100, _rightEdge.Value / 100);
    }

    /// <summary>Circle with the dead zone (filled), the outer edge (dashed) and the stick's dot.</summary>
    sealed class StickView : Canvas
    {
        const double Size = 104;
        readonly Ellipse _dead = new() { Fill = Ui.Brush("TrackBrush") };
        readonly Ellipse _edge = new() { Stroke = Ui.Brush("SubtleTextBrush"), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 3 } };
        readonly Ellipse _dot = new() { Width = 14, Height = 14, Fill = Brushes.White };

        public StickView()
        {
            Width = Height = Size;
            HorizontalAlignment = HorizontalAlignment.Center;
            Children.Add(new Ellipse { Width = Size, Height = Size, Fill = Ui.Brush("CardBrush"), Stroke = Ui.Brush("TrackBrush"), StrokeThickness = 1.5 });
            Children.Add(_dead);
            Children.Add(_edge);
            Children.Add(_dot);
            Show(0, 0);
        }

        public void SetZones(double dead, double edge)
        {
            Place(_dead, Math.Max(4, dead * Size));
            Place(_edge, Math.Max(8, edge * Size));
        }

        public void Show(double x, double y)
        {
            SetLeft(_dot, Size / 2 + x * (Size / 2 - 7) - 7);
            SetTop(_dot, Size / 2 - y * (Size / 2 - 7) - 7);
        }

        void Place(Ellipse e, double diameter)
        {
            e.Width = e.Height = diameter;
            SetLeft(e, (Size - diameter) / 2);
            SetTop(e, (Size - diameter) / 2);
        }
    }
}

/// <summary>Where each trigger starts and is full, quick trigger, and live meters for both triggers.</summary>
public sealed class TriggersScreen : ControllerScreen
{
    readonly UniformGrid _sides;
    readonly Button[] _side;
    readonly ScaleTransform _ltLive = new(0, 1), _rtLive = new(0, 1);
    readonly FillSlider _start, _full;
    readonly Button _quick, _together;
    readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromMilliseconds(33) };
    bool _left = true;

    public override string Id => "controller-triggers";
    public override string Title => "Triggers";

    public TriggersScreen(ControllerService pad) : base(pad)
    {
        var meters = new UniformGrid { Columns = 2, Margin = new Thickness(0, 2, 0, 8) };
        meters.Children.Add(Meter("LT", _ltLive));
        meters.Children.Add(Meter("RT", _rtLive));
        _together = Ui.Switch("Same for both", () => Pad.TriggersTogether = !Pad.TriggersTogether);
        // LT / RT only matter when they're set separately.
        (_sides, _side) = Ui.Segment(new[] { "LT", "RT" }, i => { _left = i == 0; OnShown(); });
        _start = Ui.Slider(null, "Starts at", 50, 1);
        _full = Ui.Slider(null, "Full at", 100, 1);
        Hook(_start, (left, v) => Pad.SetTriggerStart(left, v));
        Hook(_full, (left, v) => Pad.SetTriggerFull(left, v));
        _quick = Ui.Switch("Quick trigger", () =>
        {
            bool on = !Pad.QuickTrigger(Shown);
            foreach (bool side in Sides()) Pad.SetQuickTrigger(side, on);
        });
        Content = Ui.Stack(meters, _together, _sides, _start, _full, _quick);
        _live.Tick += (_, _) =>
        {
            if (PadState.Read() is not { } s) return;
            _ltLive.ScaleX = s.LeftTrigger;
            _rtLive.ScaleX = s.RightTrigger;
        };
    }

    /// <summary>The trigger whose values are shown (LT when both are set together).</summary>
    bool Shown => Pad.TriggersTogether || _left;

    IEnumerable<bool> Sides() => Pad.TriggersTogether ? new[] { true, false } : new[] { _left };

    void Hook(FillSlider slider, Action<bool, int> apply) => slider.ValueChanged += (_, e) =>
    {
        slider.ValueText = $"{(int)e.NewValue}%";
        if (Refreshing) return;
        foreach (bool side in Sides()) apply(side, (int)e.NewValue);
    };

    /// <summary>A name and a bar that fills as the trigger is pressed.</summary>
    static FrameworkElement Meter(string name, ScaleTransform live)
    {
        var label = Ui.Text(name, bold: true);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(label, Dock.Left);
        var track = new Border
        {
            Height = 8, CornerRadius = new CornerRadius(4), Background = Ui.Brush("TrackBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(4), RenderTransform = live },
        };
        var meter = new DockPanel { Margin = new Thickness(10, 0, 10, 0) };
        meter.Children.Add(label);
        meter.Children.Add(track);
        return meter;
    }

    public override void OnShown() { base.OnShown(); _live.Start(); }
    public override void OnHidden() { base.OnHidden(); _live.Stop(); }

    protected override void Refresh()
    {
        bool shown = Shown;
        _sides.Visibility = Pad.TriggersTogether ? Visibility.Collapsed : Visibility.Visible;
        Ui.Choose(_side, _left ? 0 : 1);
        _start.Value = Pad.TriggerStart(shown);
        _full.Value = Pad.TriggerFull(shown) == 0 ? 100 : Pad.TriggerFull(shown);
        _start.ValueText = $"{(int)_start.Value}%";
        _full.ValueText = $"{(int)_full.Value}%";
        Ui.SetOn(_quick, Pad.QuickTrigger(shown));
        Ui.SetOn(_together, Pad.TriggersTogether);
    }
}

/// <summary>Grid of every button with what it does (orange = changed); A opens a picker.</summary>
public sealed class ButtonsScreen : ControllerScreen
{
    readonly Dictionary<string, (Button Tile, TextBlock Mapping)> _tiles = new();
    readonly UniformGrid _grid = new() { Columns = 4 };
    readonly Button _nintendo, _diagonal;
    readonly Border _picker;
    readonly TextBlock _pickerTitle = Ui.Text("", bold: true);
    readonly UniformGrid _targets = new() { Columns = 4 };
    readonly Dictionary<Button, int> _targetCodes = new();
    readonly Button _turbo;
    readonly Grid _layers = new();
    string? _editing;

    public override string Id => "controller-buttons";
    public override string Title => "Buttons";

    public ButtonsScreen(ControllerService pad) : base(pad)
    {
        foreach (var b in ControllerService.Buttons)
        {
            var mapping = Ui.Text("", subtle: true);
            mapping.FontSize = 12;
            mapping.Foreground = Ui.ChangedBrush;
            var label = Ui.Text(b.Label, bold: true);
            label.HorizontalAlignment = mapping.HorizontalAlignment = HorizontalAlignment.Center;
            var tile = Ui.Card(Ui.Stack(label, mapping), () => OpenPicker(b.Id), 52);
            tile.Margin = new Thickness(3);
            tile.Padding = new Thickness(4);
            tile.HorizontalContentAlignment = HorizontalAlignment.Center;
            _tiles[b.Id] = (tile, mapping);
            _grid.Children.Add(tile);
        }
        _nintendo = Ui.Switch("Nintendo layout (swap A/B, X/Y)", () => Pad.SetNintendoLayout(!Pad.NintendoLayout));
        _diagonal = Ui.Switch("D-pad diagonal lock", () => Pad.SetDiagonalLock(!Pad.DiagonalLockOn));
        var main = Ui.Stack(_grid, _nintendo, _diagonal);

        foreach (var (code, text) in ControllerService.Targets)
        {
            var chip = new Button { Style = Ui.Style("ChipButton"), Content = Ui.Text(text, bold: true) };
            chip.Click += (_, _) => Pick(code);
            _targetCodes[chip] = code;
            _targets.Children.Add(chip);
        }
        _turbo = Ui.Switch("Turbo (repeat while held)", () => { if (_editing != null) Pad.SetTurbo(_editing, !Pad.Turbo(_editing)); });
        _picker = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x3A, 0x32, 0x2E)),
            CornerRadius = new CornerRadius(20), Padding = new Thickness(10), Margin = new Thickness(0, 30, 0, 0),
            BorderBrush = Ui.Brush("TrackBrush"), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed,
            Child = Ui.Stack(_pickerTitle, _targets, _turbo),
        };
        _layers.Children.Add(main);
        _layers.Children.Add(_picker);
        Content = _layers;
    }

    void OpenPicker(string id)
    {
        _editing = id;
        _pickerTitle.Text = $"{ControllerService.Buttons.First(b => b.Id == id).Label} acts as";
        _grid.IsEnabled = _nintendo.IsEnabled = _diagonal.IsEnabled = false;  // D-pad stays in the picker
        _picker.Visibility = Visibility.Visible;
        Refresh();
        int mapped = Pad.MappedTo(id);
        var current = _targetCodes.FirstOrDefault(pair => pair.Value == mapped).Key ?? _targetCodes.Keys.First();
        Dispatcher.BeginInvoke(() => current.Focus(), DispatcherPriority.Loaded);
    }

    void Pick(int code)
    {
        if (_editing == null) return;
        Pad.SetMapping(_editing, code);
        ClosePicker();
    }

    void ClosePicker()
    {
        var id = _editing;
        _editing = null;
        _picker.Visibility = Visibility.Collapsed;
        _grid.IsEnabled = _nintendo.IsEnabled = _diagonal.IsEnabled = true;
        if (id != null) Dispatcher.BeginInvoke(() => _tiles[id].Tile.Focus(), DispatcherPriority.Loaded);
    }

    public bool HandleBack()
    {
        if (_editing == null) return false;
        ClosePicker();
        return true;
    }

    public override void OnHidden()
    {
        base.OnHidden();
        if (_editing != null) ClosePicker();
    }

    protected override void Refresh()
    {
        foreach (var b in ControllerService.Buttons)
        {
            int target = Pad.MappedTo(b.Id);
            var (_, mapping) = _tiles[b.Id];
            // Only changed buttons get a second line, in orange.
            mapping.Text = ControllerService.TargetLabel(target) + (Pad.Turbo(b.Id) ? " · turbo" : "");
            mapping.Visibility = target != b.Code || Pad.Turbo(b.Id) ? Visibility.Visible : Visibility.Collapsed;
        }
        Ui.SetOn(_nintendo, Pad.NintendoLayout);
        Ui.SetOn(_diagonal, Pad.DiagonalLockOn);
        if (_editing != null)
        {
            int current = Pad.MappedTo(_editing);
            foreach (var (chip, code) in _targetCodes) chip.Tag = code == current ? "selected" : null;
            Ui.SetOn(_turbo, Pad.Turbo(_editing));
        }
    }
}

/// <summary>Gyro on/off, what it moves, speed, and whether it only works while LT is held.</summary>
public sealed class GyroScreen : ControllerScreen
{
    readonly Button _on, _onlyLt;
    readonly Button[] _output;
    readonly FillSlider _speed;
    readonly Panel _options;

    public override string Id => "controller-gyro";
    public override string Title => "Gyro";

    public GyroScreen(ControllerService pad) : base(pad)
    {
        _on = Ui.Switch("Gyro", () => Pad.SetGyro(!Pad.GyroOn));
        (var output, _output) = Ui.Segment(new[] { "Moves right stick", "Moves mouse" }, i => Pad.SetGyro(Pad.GyroOn, asMouse: i == 1));
        _speed = Ui.Slider(null, "Speed", 100, 5);
        _speed.ValueChanged += (_, e) =>
        {
            _speed.ValueText = $"{(int)e.NewValue}%";
            if (!Refreshing) Pad.SetGyro(Pad.GyroOn, speed: (int)e.NewValue);
        };
        _onlyLt = Ui.Switch("Only while holding LT", () => Pad.SetGyro(Pad.GyroOn, onlyWhileLt: !Pad.GyroOnlyWhileLt));
        // While gyro is off its options stay in view, dimmed and out of the D-pad's way.
        _options = Ui.Stack(output, _speed, _onlyLt);
        Content = Ui.Stack(_on, _options);
    }

    protected override void Refresh()
    {
        Ui.SetOn(_on, Pad.GyroOn);
        Ui.Choose(_output, Pad.GyroAsMouse ? 1 : 0);
        _speed.Value = Pad.GyroSpeed == 0 ? 50 : Pad.GyroSpeed;
        _speed.ValueText = $"{(int)_speed.Value}%";
        Ui.SetOn(_onlyLt, Pad.GyroOnlyWhileLt);
        _options.IsEnabled = Pad.GyroOn;
        _options.Opacity = Pad.GyroOn ? 1 : 0.4;
    }
}

/// <summary>Light colour, brightness (0 = off), effect and effect speed.</summary>
public sealed class LightsScreen : ControllerScreen
{
    static readonly (string Name, string Hex)[] Swatches =
    {
        ("Red", "#FF3B30"), ("Orange", "#FF9500"), ("Green", "#34C759"), ("Cyan", "#00C7FF"),
        ("Blue", "#0A84FF"), ("Purple", "#BF5AF2"), ("Pink", "#FF2D92"), ("White", "#FFFFFF"),
    };

    readonly Dictionary<Button, string> _swatches = new();
    readonly FillSlider _brightness, _speed;
    readonly Button[] _effects;

    public override string Id => "controller-lights";
    public override string Title => "Lights";

    public LightsScreen(ControllerService pad) : base(pad)
    {
        var swatches = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (name, hex) in Swatches)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var swatch = new Button { Style = Ui.Style("SwatchButton"), Background = new SolidColorBrush(color) };
            swatch.Click += (_, _) => Pad.SetLights(Pad.Effect == LightEffect.Rainbow ? LightEffect.Steady : Pad.Effect, (color.R, color.G, color.B));
            _swatches[swatch] = name;
            swatches.Children.Add(swatch);
        }
        _brightness = Ui.Slider("", "Brightness", 100, 10);
        _brightness.ValueChanged += (_, e) =>
        {
            _brightness.ValueText = BrightnessText(e.NewValue);
            if (!Refreshing) Pad.SetLightBrightness((int)Math.Round(e.NewValue));
        };
        (var effects, _effects) = Ui.Segment(new[] { "Steady", "Breathe", "Rainbow" },
            i => Pad.SetLights((LightEffect)i, Pad.LightColor));
        _speed = Ui.Slider(null, "Effect speed", 20, 1);
        _speed.ValueChanged += (_, e) =>
        {
            _speed.ValueText = $"{(int)(e.NewValue * 5)}%";
            if (!Refreshing) Pad.SetLights(Pad.Effect, Pad.LightColor, (int)e.NewValue);
        };
        Content = Ui.Stack(swatches, _brightness, effects, _speed);
    }

    static string BrightnessText(double percent) => percent < 0.5 ? "Off" : $"{(int)Math.Round(percent)}%";

    protected override void Refresh()
    {
        var effect = Pad.Effect;
        string current = ColorName(Pad.LightColor);
        foreach (var (swatch, name) in _swatches)
            swatch.Tag = effect != LightEffect.Rainbow && name == current ? "selected" : null;
        _brightness.Value = Pad.LightBrightness;
        _brightness.ValueText = BrightnessText(_brightness.Value);
        Ui.Choose(_effects, (int)effect);
        _speed.Visibility = effect == LightEffect.Steady ? Visibility.Collapsed : Visibility.Visible;
        _speed.Value = Pad.LightSpeed;
        _speed.ValueText = $"{Pad.LightSpeed * 5}%";
    }

    /// <summary>A colour's name by its hue, matching the swatch names (so MSI's colours get one too).</summary>
    static string ColorName((byte R, byte G, byte B) c)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B)), range = max - min;
        if (max == 0 || range / max < 0.25) return "White";
        double hue = max == c.R ? 60 * (((c.G - c.B) / range % 6 + 6) % 6)
            : max == c.G ? 60 * ((c.B - c.R) / range + 2)
            : 60 * ((c.R - c.G) / range + 4);
        return hue switch
        {
            < 15 => "Red", < 45 => "Orange", < 70 => "Yellow", < 165 => "Green",
            < 200 => "Cyan", < 250 => "Blue", < 290 => "Purple", < 340 => "Pink", _ => "Red",
        };
    }
}
