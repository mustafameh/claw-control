using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClawControl.UI;

/// <summary>
/// Thick split slider (volume, brightness, power limits, app volume): a white fill, a sliver of
/// glass as the handle and the empty part, with small gaps between them, all inside one rounded
/// bar (chosen 2026-10-27). Its icon, label and value sit inside the bar. They're drawn twice,
/// light and dark, and the dark copy is cut to the fill, so text crossing the fill edge stays
/// readable letter by letter (like iOS).
///
/// Motion, all on springs and only while something moves: the fill glides to a new value, the
/// value rolls in from the direction it moved, and pushing past either end stretches the bar
/// like a rubber band (Nudge). Optionally shows an arrow after the value; tapping it raises
/// MoreRequested (A does the same via PanelInput). Looks come from the implicit FillSlider style
/// in Theme.xaml.
/// </summary>
public class FillSlider : Slider
{
    public static readonly DependencyProperty GlyphProperty = Register<string?>(nameof(Glyph), null);
    public static readonly DependencyProperty IconProperty = Register<ImageSource?>(nameof(Icon), null);
    public static readonly DependencyProperty LineIconProperty = Register<Geometry?>(nameof(LineIcon), null);
    public static readonly DependencyProperty LabelProperty = Register<string?>(nameof(Label), null);
    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(nameof(ValueText), typeof(string),
        typeof(FillSlider), new FrameworkPropertyMetadata(null, (d, _) => ((FillSlider)d).RollValue()));
    public static readonly DependencyProperty FillProperty = Register<Brush?>(nameof(Fill), null);
    public static readonly DependencyProperty ShowMoreProperty = Register(nameof(ShowMore), false);
    public static readonly DependencyProperty TurnGlyphProperty = Register(nameof(TurnGlyph), false);

    /// <summary>Segoe Fluent Icons glyph shown at the start of the bar.</summary>
    public string? Glyph { get => (string?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    /// <summary>Picture shown at the start of the bar (app icons).</summary>
    public ImageSource? Icon { get => (ImageSource?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    /// <summary>A Theme.xaml line icon (24×24 geometry) when Segoe Fluent Icons has no fitting glyph.</summary>
    public Geometry? LineIcon { get => (Geometry?)GetValue(LineIconProperty); set => SetValue(LineIconProperty, value); }
    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? ValueText { get => (string?)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public bool ShowMore { get => (bool)GetValue(ShowMoreProperty); set => SetValue(ShowMoreProperty, value); }
    /// <summary>The glyph turns and grows a little with the value (the brightness sun).</summary>
    public bool TurnGlyph { get => (bool)GetValue(TurnGlyphProperty); set => SetValue(TurnGlyphProperty, value); }

    public event Action? MoreRequested;

    // Bar geometry (design units): the handle's width, the gap on each side of it, the corners
    // where the pieces meet, and how far the handle stays from the bar's ends.
    const double HandleWidth = 6, Gap = 3, InnerCorner = 4, EndInset = 4;
    // Fill: a little overshoot. Stretch: springy, a couple of small wobbles.
    const double Stiffness = 300, Damping = 24, StretchStiffness = 380, StretchDamping = 13;

    FrameworkElement? _more, _darkLayer, _body, _glyph, _darkGlyph, _value, _darkValue;
    Border? _fill, _rest, _handle;
    Track? _track;
    readonly ScaleTransform _stretchScale = new();
    readonly RotateTransform _turn = new(), _darkTurn = new();
    readonly ScaleTransform _grow = new(), _darkGrow = new();

    /// <summary>Where the fill is drawn (0-1 of the range); springs toward the value.</summary>
    double _shown = -1, _shownV;
    /// <summary>Rubber-band stretch: + pulls right (past the end), - pulls left.</summary>
    double _stretch, _stretchV;
    bool _rollUp, _running;
    TimeSpan _last;

    public FillSlider()
    {
        // Shown again after changing while hidden (or never laid out yet): start at the value.
        IsVisibleChanged += (_, _) => Snap();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _more = GetTemplateChild("PART_More") as FrameworkElement;
        _darkLayer = GetTemplateChild("PART_DarkLayer") as FrameworkElement;
        _track = GetTemplateChild("PART_Track") as Track;
        _body = GetTemplateChild("PART_Body") as FrameworkElement;
        _fill = GetTemplateChild("PART_Fill") as Border;
        _rest = GetTemplateChild("PART_Rest") as Border;
        _handle = GetTemplateChild("PART_Handle") as Border;
        _glyph = GetTemplateChild("GlyphText") as FrameworkElement;
        _darkGlyph = GetTemplateChild("DarkGlyph") as FrameworkElement;
        _value = GetTemplateChild("PART_Value") as FrameworkElement;
        _darkValue = GetTemplateChild("PART_DarkValue") as FrameworkElement;
        if (_body != null)
        {
            _body.RenderTransform = _stretchScale;
            _body.SizeChanged += (_, _) =>
            {
                // Everything stays inside the bar's rounded shape, also at 0 % and 100 %.
                double w = _body.ActualWidth, h = _body.ActualHeight;
                _body.Clip = new RectangleGeometry(new Rect(0, 0, w, h), h / 2, h / 2);
                Place();
            };
        }
        if (_glyph != null) _glyph.RenderTransform = new TransformGroup { Children = { _grow, _turn } };
        if (_darkGlyph != null) _darkGlyph.RenderTransform = new TransformGroup { Children = { _darkGrow, _darkTurn } };
        foreach (var value in new[] { _value, _darkValue })
            if (value != null) value.RenderTransform = new TranslateTransform();
        Snap();
    }

    double Fraction => Maximum > Minimum ? Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1) : 0;

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        _rollUp = newValue > oldValue;
        base.OnValueChanged(oldValue, newValue);  // page handlers set ValueText here
        Glide();
    }

    protected override void OnMinimumChanged(double oldMinimum, double newMinimum)
    {
        base.OnMinimumChanged(oldMinimum, newMinimum);
        Glide();
    }

    protected override void OnMaximumChanged(double oldMaximum, double newMaximum)
    {
        base.OnMaximumChanged(oldMaximum, newMaximum);
        Glide();
    }

    /// <summary>D-pad left / right: one step, or past either end a rubber-band stretch.</summary>
    public void Nudge(int direction)
    {
        double next = Math.Clamp(Value + direction * SmallChange, Minimum, Maximum);
        if (next != Value)
        {
            Value = next;
            return;
        }
        if (!IsVisible) return;
        _stretchV += direction * 3.2;
        Run();
    }

    /// <summary>Moves the fill to the value: springs while shown, jumps while hidden (startup,
    /// captures, values loaded in the background).</summary>
    void Glide()
    {
        if (IsVisible && _shown >= 0) Run();
        else Snap();
    }

    void Snap()
    {
        Stop();
        _shown = Fraction;
        _shownV = _stretch = _stretchV = 0;
        Place();
    }

    void Run()
    {
        if (_running) return;
        _running = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _last) return;  // Rendering can fire more than once per frame
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;
        if (!IsVisible)
        {
            Snap();
            return;
        }
        double target = Fraction;
        for (double left = dt; left > 1e-6; left -= 1 / 120.0)
        {
            double step = Math.Min(left, 1 / 120.0);
            _shownV += (Stiffness * (target - _shown) - Damping * _shownV) * step;
            _shown += _shownV * step;
            _stretchV += (-StretchStiffness * _stretch - StretchDamping * _stretchV) * step;
            _stretch += _stretchV * step;
        }
        bool settled = Math.Abs(target - _shown) < 0.0005 && Math.Abs(_shownV) < 0.005
            && Math.Abs(_stretch) < 0.002 && Math.Abs(_stretchV) < 0.02;
        if (settled)
        {
            _shown = target;
            _shownV = _stretch = _stretchV = 0;
            Stop();
        }
        Place();
    }

    /// <summary>Lays out the fill, the handle and the empty part for the drawn value.</summary>
    void Place()
    {
        if (_body == null || _fill == null || _rest == null || _handle == null) return;
        double width = _body.ActualWidth, height = _body.ActualHeight;
        if (width <= 0 || height <= 0) return;
        double shown = Math.Clamp(_shown < 0 ? Fraction : _shown, 0, 1);
        double radius = height / 2;

        // The handle travels between the ends; inside the rounded ends it gets shorter so it
        // follows the bar's curve instead of poking out of it.
        double centre = EndInset + shown * (width - 2 * EndInset);
        double fromEnd = Math.Min(centre, width - centre);
        double handleHeight = fromEnd < radius ? 2 * Math.Sqrt(radius * radius - (radius - fromEnd) * (radius - fromEnd)) : height;
        double fillEnd = centre - Gap - HandleWidth / 2, restStart = centre + Gap + HandleWidth / 2;

        _fill.Width = Math.Max(0, fillEnd);
        _fill.Visibility = fillEnd > 1 ? Visibility.Visible : Visibility.Hidden;
        Canvas.SetLeft(_rest, restStart);
        _rest.Width = Math.Max(0, width - restStart);
        _rest.Visibility = restStart < width - 1 ? Visibility.Visible : Visibility.Hidden;
        Canvas.SetLeft(_handle, centre - HandleWidth / 2);
        Canvas.SetTop(_handle, (height - handleHeight) / 2);
        _handle.Width = HandleWidth;
        _handle.Height = Math.Max(0, handleHeight);
        _handle.CornerRadius = new CornerRadius(HandleWidth / 2);
        _fill.CornerRadius = new CornerRadius(0, InnerCorner, InnerCorner, 0);
        _rest.CornerRadius = new CornerRadius(InnerCorner, 0, 0, InnerCorner);

        // Dark text only over the fill.
        if (_darkLayer != null)
        {
            double edge = _body.TranslatePoint(new Point(Math.Max(0, fillEnd), 0), _darkLayer).X;
            _darkLayer.Clip = new RectangleGeometry(new Rect(-50, -10, Math.Max(0, edge + 50), _darkLayer.ActualHeight + 20));
        }

        // Past an end the bar stretches that way and thins a little, anchored at the other end.
        _stretchScale.CenterX = _stretch > 0 ? 0 : width;
        _stretchScale.CenterY = height / 2;
        _stretchScale.ScaleX = 1 + Math.Abs(_stretch) * 0.06;
        _stretchScale.ScaleY = 1 - Math.Abs(_stretch) * 0.12;

        if (TurnGlyph)
        {
            _turn.Angle = _darkTurn.Angle = shown * 90;
            _grow.ScaleX = _grow.ScaleY = _darkGrow.ScaleX = _darkGrow.ScaleY = 0.86 + 0.26 * shown;
        }
    }

    /// <summary>The new value slides in from below when it went up, from above when it went down.</summary>
    void RollValue()
    {
        if (!IsVisible) return;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
        var time = TimeSpan.FromMilliseconds(240);
        foreach (var value in new[] { _value, _darkValue })
        {
            if (value?.RenderTransform is not TranslateTransform move) continue;
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_rollUp ? 10 : -10, 0, time) { EasingFunction = ease });
            value.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time));
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // The arrow opens more settings instead of moving the slider to that spot.
        if (_more is { IsVisible: true, IsMouseOver: true })
        {
            e.Handled = true;
            MoreRequested?.Invoke();
            return;
        }
        base.OnPreviewMouseLeftButtonDown(e);
    }

    // Touch: the panel's ScrollViewer pans on any finger slide, which stole sideways drags from
    // the bar (only taps got through). The bar takes the finger itself and follows it.
    protected override void OnPreviewTouchDown(TouchEventArgs e)
    {
        if (_more is { IsVisible: true } && new Rect(_more.RenderSize).Contains(e.GetTouchPoint(_more).Position))
        {
            e.Handled = true;
            MoreRequested?.Invoke();
            return;
        }
        if (_track == null || !CaptureTouch(e.TouchDevice)) return;
        Focus();
        FollowTouch(e);
        e.Handled = true;
    }

    protected override void OnPreviewTouchMove(TouchEventArgs e)
    {
        if (e.TouchDevice.Captured != this) return;
        FollowTouch(e);
        e.Handled = true;
    }

    protected override void OnPreviewTouchUp(TouchEventArgs e)
    {
        if (e.TouchDevice.Captured != this) return;
        ReleaseTouchCapture(e.TouchDevice);
        e.Handled = true;
    }

    /// <summary>Moves to the finger, on the bar's steps (SmallChange).</summary>
    void FollowTouch(TouchEventArgs e)
    {
        double value = _track!.ValueFromPoint(e.GetTouchPoint(_track).Position);
        double step = SmallChange > 0 ? SmallChange : 1;
        Value = Math.Clamp(Minimum + Math.Round((value - Minimum) / step) * step, Minimum, Maximum);
    }

    static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(name, typeof(T), typeof(FillSlider), new FrameworkPropertyMetadata(defaultValue));
}
