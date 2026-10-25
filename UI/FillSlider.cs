using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ClawControl.UI;

/// <summary>
/// Thick pill slider that fills up (volume, brightness, power limits, app volume). Its icon, label
/// and value sit inside the bar. They're drawn twice, light and dark, and the dark copy is cut to
/// the fill, so text crossing the fill edge stays readable letter by letter (like iOS).
/// Optionally shows an arrow after the value; tapping it raises MoreRequested (A does the same
/// via PanelInput). Looks come from the implicit FillSlider style in Theme.xaml.
/// </summary>
public class FillSlider : Slider
{
    public static readonly DependencyProperty GlyphProperty = Register<string?>(nameof(Glyph), null);
    public static readonly DependencyProperty IconProperty = Register<ImageSource?>(nameof(Icon), null);
    public static readonly DependencyProperty LineIconProperty = Register<Geometry?>(nameof(LineIcon), null);
    public static readonly DependencyProperty LabelProperty = Register<string?>(nameof(Label), null);
    public static readonly DependencyProperty ValueTextProperty = Register<string?>(nameof(ValueText), null);
    public static readonly DependencyProperty FillProperty = Register<Brush?>(nameof(Fill), null);
    public static readonly DependencyProperty ShowMoreProperty = Register(nameof(ShowMore), false);

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

    public event Action? MoreRequested;

    FrameworkElement? _more, _darkLayer;
    Track? _track;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _more = GetTemplateChild("PART_More") as FrameworkElement;
        _darkLayer = GetTemplateChild("PART_DarkLayer") as FrameworkElement;
        _track = GetTemplateChild("PART_Track") as Track;
        if (_track != null) _track.LayoutUpdated += (_, _) => UpdateClip();
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

    double _lastClip = -1;

    /// <summary>Cuts the dark text layer to the filled part of the bar.</summary>
    void UpdateClip()
    {
        if (_darkLayer == null || _track?.Thumb == null || !_track.Thumb.IsArrangeValid) return;
        double edge = _track.Thumb.TranslatePoint(new Point(_track.Thumb.ActualWidth / 2, 0), _darkLayer).X;
        if (Math.Abs(edge - _lastClip) < 0.1 && _darkLayer.Clip != null) return;
        _lastClip = edge;
        _darkLayer.Clip = new RectangleGeometry(new Rect(-50, -10, Math.Max(0, edge + 50), _darkLayer.ActualHeight + 20));
    }

    static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(name, typeof(T), typeof(FillSlider), new FrameworkPropertyMetadata(defaultValue));
}
