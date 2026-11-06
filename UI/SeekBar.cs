using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClawControl.UI;

/// <summary>
/// The now-playing seek line: a thin white fill on a faint track, in seconds. Selected, it
/// thickens and shows a glass knob; D-pad left / right jump (SmallChange seconds, handled by the
/// panel like any slider), a finger or the mouse drags it. A seek is sent once the jumps stop
/// for a moment, so several presses make one seek.
/// </summary>
sealed class SeekBar : Slider
{
    const double Thin = 6, Thick = 10, Knob = 18, Inset = 2;

    static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(double), typeof(SeekBar), new FrameworkPropertyMetadata(Thin, FrameworkPropertyMetadataOptions.AffectsRender));

    static readonly Brush TrackFill = Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));
    static readonly Brush PlayedFill = Freeze(new SolidColorBrush(Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF)));
    static readonly Brush KnobFill = Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)));
    static readonly Brush KnobShade = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)));
    static readonly Pen KnobRim = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), 1));

    readonly DispatcherTimer _settle;
    bool _fromCode, _dragging;
    DateTime _holdUntil;

    /// <summary>The user moved it to this many seconds (after the jumps stopped, or the finger let go).</summary>
    public event Action<double>? Seeked;

    /// <summary>The user is moving it, or just did: the track's own position shouldn't move it now.</summary>
    public bool Holding => _dragging || _settle.IsEnabled || DateTime.UtcNow < _holdUntil;

    double Line
    {
        get => (double)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    public SeekBar()
    {
        Height = 22;
        Minimum = 0;
        SmallChange = 10;
        FocusVisualStyle = null;
        // No parts: drawn in OnRender. The clear border takes taps and gives the drop its corners.
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        Template = new ControlTemplate(typeof(SeekBar)) { VisualTree = face };
        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _settle.Tick += (_, _) => Send();
        GotFocus += (_, _) => Grow(true);
        LostFocus += (_, _) => Grow(false);
    }

    /// <summary>Where the track is (from its own clock); ignored while the user is moving it.</summary>
    public void Show(double seconds, double length)
    {
        if (Holding) return;
        _fromCode = true;
        Maximum = Math.Max(1, length);
        Value = Math.Clamp(seconds, 0, Maximum);
        SmallChange = length > 20 * 60 ? 30 : 10;
        _fromCode = false;
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        InvalidateVisual();
        if (_fromCode || _dragging) return;
        _settle.Stop();
        _settle.Start();
    }

    void Send()
    {
        _settle.Stop();
        // The app takes a moment to report the new spot; until then the bar stays where it was put.
        _holdUntil = DateTime.UtcNow.AddSeconds(1.5);
        Seeked?.Invoke(Value);
    }

    void Grow(bool selected)
    {
        InvalidateVisual();
        if (Motion.Reduced) { Line = selected ? Thick : Thin; return; }
        BeginAnimation(LineProperty, new DoubleAnimation(selected ? Thick : Thin, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth - Inset * 2, line = Line, middle = ActualHeight / 2;
        if (width <= 0) return;
        double share = Maximum > Minimum ? Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1) : 0;
        var track = new Rect(Inset, middle - line / 2, width, line);
        dc.DrawRoundedRectangle(TrackFill, null, track, line / 2, line / 2);
        if (share > 0)
        {
            dc.PushClip(new RectangleGeometry(new Rect(Inset, 0, width * share, ActualHeight)));
            dc.DrawRoundedRectangle(PlayedFill, null, track, line / 2, line / 2);
            dc.Pop();
        }
        if (IsFocused || _dragging)
        {
            var spot = new Point(Inset + width * share, middle);
            dc.DrawEllipse(KnobShade, null, new Point(spot.X, spot.Y + 2), Knob / 2 + 1, Knob / 2 + 1);
            dc.DrawEllipse(KnobFill, KnobRim, spot, Knob / 2, Knob / 2);
        }
    }

    // Mouse and touch: follow the pointer, seek on release. The panel's ScrollViewer would take a
    // sideways finger slide as a pan, so the bar takes the finger itself (as FillSlider does).
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!CaptureMouse()) return;
        Begin(e.GetPosition(this));
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Follow(e.GetPosition(this));
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        e.Handled = true;
        ReleaseMouseCapture();
        End();
    }

    protected override void OnPreviewTouchDown(TouchEventArgs e)
    {
        e.Handled = true;
        if (!CaptureTouch(e.TouchDevice)) return;
        Begin(e.GetTouchPoint(this).Position);
    }

    protected override void OnPreviewTouchMove(TouchEventArgs e)
    {
        if (e.TouchDevice.Captured != this) return;
        e.Handled = true;
        Follow(e.GetTouchPoint(this).Position);
    }

    protected override void OnPreviewTouchUp(TouchEventArgs e)
    {
        if (e.TouchDevice.Captured != this) return;
        e.Handled = true;
        ReleaseTouchCapture(e.TouchDevice);
        End();
    }

    void Begin(Point point)
    {
        _dragging = true;
        _settle.Stop();
        Follow(point);
    }

    void Follow(Point point)
    {
        double width = ActualWidth - Inset * 2;
        if (width <= 0) return;
        Value = Minimum + Math.Clamp((point.X - Inset) / width, 0, 1) * (Maximum - Minimum);
        InvalidateVisual();
    }

    void End()
    {
        if (!_dragging) return;
        _dragging = false;
        Send();
    }

    static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
