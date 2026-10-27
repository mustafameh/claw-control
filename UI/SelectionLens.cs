using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClawControl.UI;

/// <summary>
/// The D-pad selection as a drop of glass (Liquid Glass style, chosen 2026-10-25): a lens with a
/// lit rim on its own layer above the panel. It springs to the focused control, stretching along
/// the way it moves and settling with one small overshoot; A makes it swell and settle. The
/// control's content grows a little under it (re-rendered bigger, so text stays sharp).
/// Works per frame only while it moves; nothing runs while it rests.
/// </summary>
sealed class SelectionLens
{
    // Slightly under-damped: one small overshoot, then still.
    const double Stiffness = 300, Damping = 24;
    const double PressStiffness = 420, PressDamping = 18;
    /// <summary>Gap between a control's edge and the drop's rim (round controls already have one).</summary>
    const double Pad = 3;
    /// <summary>The gap round buttons build in around their face (padding + border): bare circles
    /// (the app switcher's apps) get the same, so the drop sits round them like a bubble.</summary>
    const double RoundGap = 5;

    readonly Canvas _layer;
    readonly Visual _root;
    readonly ScrollViewer _scroller;
    readonly Border _lens;
    readonly ScaleTransform _stretch = new();
    readonly TranslateTransform _move = new();
    FrameworkElement? _target;
    Rect _clip = Rect.Empty;
    double _x, _y, _w, _h, _r, _vx, _vy, _vw, _vh, _vr;
    double _press = 1, _pressV;
    bool _snap = true, _running;
    TimeSpan _last;

    /// <param name="root">Everything the drop may sit on (the panel window's root: the card and the app switcher).</param>
    public SelectionLens(Canvas layer, ScrollViewer scroller, Visual root)
    {
        _layer = layer;
        _root = root;
        _scroller = scroller;
        _lens = new Border
        {
            Background = (Brush)layer.FindResource("LensFillBrush"),
            BorderBrush = (Brush)layer.FindResource("LensRimBrush"),
            BorderThickness = new Thickness(1.2),
            Opacity = 0,
            IsHitTestVisible = false,
            RenderTransform = new TransformGroup { Children = { _stretch, _move } },
        };
        layer.Children.Add(_lens);
        // Scrolling, and controls appearing or moving, move the target under the drop.
        scroller.ScrollChanged += (_, _) => Kick();
        layer.LayoutUpdated += (_, _) => Kick();
    }

    /// <summary>The selected control left the panel (a list rebuilt, a page swapped), with where
    /// the drop was (in the layer's coordinates). Raised once per control.</summary>
    public event Action<Rect>? Lost;

    bool _lost;

    /// <summary>Moves the drop to a newly focused control.</summary>
    public void Follow(FrameworkElement element)
    {
        if (element == _target) return;
        Pop(_target, up: false);
        _target = element;
        _lost = false;
        Pop(element, up: true);
        Kick();
    }

    /// <summary>A pressed: the drop swells and settles (less on wide controls).</summary>
    public void Pulse(FrameworkElement element)
    {
        _pressV += IsRound(element) ? 3.2 : 1.6;
        Kick();
    }

    /// <summary>Panel closed: nothing selected; the next selection appears in place.</summary>
    public void Hide()
    {
        Stop();
        Pop(_target, up: false);
        _target = null;
        _lens.Opacity = 0;
        _snap = true;
        _press = 1;
        _pressV = 0;
    }

    /// <summary>Something moved its control without a layout pass (a render transform, like the
    /// app switcher growing): springs the drop back onto it.</summary>
    public void Nudge() => Kick();

    void Kick()
    {
        if (_running || _target == null) return;
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

    /// <summary>Debug captures: the drop drawn at rest on a control, right away.</summary>
    public void ShowNow(FrameworkElement element)
    {
        _target = element;
        _snap = true;
        Step(1 / 60.0);
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _last) return;  // Rendering can fire more than once per frame
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;
        if (!Step(dt)) Stop();
    }

    /// <summary>Moves the drop one frame toward its control; false once it has come to rest.</summary>
    bool Step(double dt)
    {
        switch (Place(out var rect, out double radius))
        {
            // Swapped out: fade, but keep the spot so the next control's drop springs from here.
            case Placement.Gone:
                _lens.Opacity = 0;
                if (!_lost && _target != null)
                {
                    _lost = true;
                    Lost?.Invoke(new Rect(_x, _y, _w, _h));
                }
                return false;
            case Placement.Hidden:
                _lens.Opacity = 0;
                _snap = true;
                return false;
        }
        if (_snap)
        {
            (_x, _y, _w, _h, _r) = (rect.X, rect.Y, rect.Width, rect.Height, radius);
            _vx = _vy = _vw = _vh = _vr = 0;
            _snap = false;
        }
        for (double left = dt; left > 1e-6; left -= 1 / 120.0)
        {
            double step = Math.Min(left, 1 / 120.0);
            Spring(ref _x, ref _vx, rect.X, step);
            Spring(ref _y, ref _vy, rect.Y, step);
            Spring(ref _w, ref _vw, rect.Width, step);
            Spring(ref _h, ref _vh, rect.Height, step);
            Spring(ref _r, ref _vr, radius, step);
            _pressV += (PressStiffness * (1 - _press) - PressDamping * _pressV) * step;
            _press += _pressV * step;
        }

        bool settled = Near(_x, rect.X, _vx) && Near(_y, rect.Y, _vy) && Near(_w, rect.Width, _vw)
            && Near(_h, rect.Height, _vh) && Near(_r, radius, _vr)
            && Math.Abs(1 - _press) < 0.001 && Math.Abs(_pressV) < 0.01;
        if (settled)
        {
            (_x, _y, _w, _h, _r) = (rect.X, rect.Y, rect.Width, rect.Height, radius);
            _vx = _vy = 0;
            _press = 1;
        }

        // Stretched along the way it's going, by how fast; the press swell on top.
        double stretch = Math.Min(0.22, Math.Sqrt(_vx * _vx + _vy * _vy) / 3200);
        bool across = Math.Abs(_vx) >= Math.Abs(_vy);
        _stretch.ScaleX = (across ? 1 + stretch : 1 - stretch * 0.55) * _press;
        _stretch.ScaleY = (across ? 1 - stretch * 0.55 : 1 + stretch) * _press;
        _stretch.CenterX = _w / 2;
        _stretch.CenterY = _h / 2;
        _move.X = _x;
        _move.Y = _y;
        _lens.Width = Math.Max(0, _w);
        _lens.Height = Math.Max(0, _h);
        _lens.CornerRadius = new CornerRadius(Math.Max(0, _r));
        _lens.Opacity = 1;
        return !settled;
    }

    static void Spring(ref double value, ref double velocity, double target, double step)
    {
        velocity += (Stiffness * (target - value) - Damping * velocity) * step;
        value += velocity * step;
    }

    static bool Near(double value, double target, double velocity) =>
        Math.Abs(target - value) < 0.05 && Math.Abs(velocity) < 0.5;

    enum Placement { Shown, Hidden, Gone }

    /// <summary>Where the drop goes for the target: its bounds plus a small gap, matching its corners.</summary>
    Placement Place(out Rect rect, out double radius)
    {
        rect = Rect.Empty;
        radius = 0;
        var target = _target;
        if (target == null || PresentationSource.FromVisual(target) == null || !target.IsDescendantOf(_root))
            return Placement.Gone;
        if (!Shown(target)) return Placement.Hidden;

        var bounds = target.TransformToVisual(_layer).TransformBounds(new Rect(target.RenderSize));
        if (IsRound(target))
        {
            rect = target is Border ? Rect.Inflate(bounds, RoundGap, RoundGap) : bounds;
            radius = rect.Width / 2;
        }
        else
        {
            rect = Rect.Inflate(bounds, Pad, Pad);
            radius = Math.Min(Corner(target) + Pad, rect.Height / 2);
        }

        // Inside the page the drop stays within the scrolling area (not over the tabs or footer).
        var clip = target.IsDescendantOf(_scroller)
            ? Rect.Inflate(_scroller.TransformToVisual(_layer).TransformBounds(new Rect(_scroller.RenderSize)), Pad + 4, Pad + 4)
            : Rect.Empty;
        if (clip != _clip)
        {
            _clip = clip;
            _layer.Clip = clip.IsEmpty ? null : new RectangleGeometry(clip);
        }
        return Placement.Shown;
    }

    /// <summary>Not collapsed or hidden up to the panel (IsVisible would also say no for every
    /// control while the panel itself is hidden, as during debug captures).</summary>
    bool Shown(FrameworkElement target)
    {
        for (DependencyObject? node = target; node != null && node != _root; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    /// <summary>The corner radius of the control's own outline (its template's outer border).</summary>
    static double Corner(FrameworkElement element) => element is Border own ? own.CornerRadius.TopLeft :
        (VisualTreeHelper.GetChildrenCount(element) > 0 ? VisualTreeHelper.GetChild(element, 0) as Border : null)?.CornerRadius.TopLeft ?? 12;

    /// <summary>Circles (mode, power, swatch, media buttons): square with fully rounded corners.</summary>
    static bool IsRound(FrameworkElement element) =>
        Math.Abs(element.ActualWidth - element.ActualHeight) < 2 && Corner(element) * 2 >= element.ActualWidth - 6;

    /// <summary>Grows (or shrinks back) what's inside the control: circles a bit more, wide rows by
    /// a few pixels so nothing reaches the drop's rim.</summary>
    static void Pop(FrameworkElement? element, bool up)
    {
        if (element == null || FindContent(element, 6) is not { } content) return;
        if (content.RenderTransform is not ScaleTransform { IsFrozen: false } scale)
        {
            content.RenderTransform = scale = new ScaleTransform();
            content.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        double to = !up ? 1 : IsRound(element) ? 1.12 : 1 + Math.Min(0.06, 6 / Math.Max(1, content.ActualWidth));
        var grow = new DoubleAnimation(to, TimeSpan.FromMilliseconds(up ? 380 : 200))
        {
            EasingFunction = up
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }
                : new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    static ContentPresenter? FindContent(DependencyObject parent, int depth)
    {
        if (depth == 0) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ContentPresenter presenter) return presenter;
            if (FindContent(child, depth - 1) is { } found) return found;
        }
        return null;
    }
}
