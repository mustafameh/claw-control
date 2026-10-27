using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using ClawControl.Core;
using ClawControl.Hardware;
using ClawControl.Input;
using ClawControl.UI;

namespace ClawControl.Features.Apps;

/// <summary>
/// The app switcher, floating left of the panel's card while the panel is open (chosen
/// 2026-10-27 from mockups, "Hub v6"): the app in front in a glass tile, the other open apps on
/// a glass arc around it and a live preview of the selected one further left.
///
/// The arc: the selected app always sits at 9 o'clock with room for two apps above it (the top
/// one faded, a floating count when more are above); the next ones wait around the bottom and
/// the arc ends in a rounded cap before it reaches the card (a count there too). Down turns it
/// clockwise, so the next app rises into the spot. Its last stop is Close background apps. D-pad
/// left from the page's left edge comes in on the tile, left again reaches the arc; A switches
/// to an app (on the tile: back to it), X ends one, B / right from the tile go back to the page.
/// Taps work too.
///
/// Lives in the panel's window, left of the card (touch and the controller stay with the panel);
/// the panel draws its glass and the live preview in its glass window (GlassPieces, Preview).
/// Nothing runs while the panel is closed; the arc turns per frame only while moving.
/// </summary>
sealed class AppSwitcher : Canvas
{
    // Layout in design units (UI/ScreenFit), from the switcher's top-left; the card's shadow room
    // starts just right of SwitcherWidth. Matches the mockup.
    public const double SwitcherWidth = 530;
    const double SwitcherHeight = 600;
    const double CX = 420, CY = 300, R = 118, HalfBand = 28, Step = 32, ArcTop = 250, ArcEnd = 58;
    const double PreviewX = 18, PreviewWidth = 236, PreviewHeight = 148, PreviewInset = 8;
    const double HubSize = 92, ItemSize = 42, SelectedSize = 56, CountSize = 34;
    const double Stiffness = 260, Damping = 23;
    /// <summary>The mini switcher's size while the page has the D-pad (it grows when entered).</summary>
    const double Mini = 0.6, GrowStiffness = 320, GrowDamping = 26;
    /// <summary>Finger travel (units) that turns the arc by one stop: the arc's length between two stops.</summary>
    const double SwipePerStop = R * Step * Math.PI / 180;

    readonly record struct Swipe(TouchDevice Finger, double StartY, double StartTurn, bool Turning);
    Swipe? _swipe;

    /// <summary>How far the tile's centre is below the switcher's top (the panel lines it up with
    /// the card's first row of controls).</summary>
    public const double TileCentre = CY;

    enum Spot { Ring, Hub }

    /// <summary>A stop on the arc: an app, or (App null) Close background apps at the end.</summary>
    sealed record Item(RunningApp? App, Border View, FrameworkElement Content);

    readonly System.Windows.Shapes.Path _band = new();
    readonly Border _hub, _frame, _frameFill, _frameRim, _countAbove, _countBelow;
    readonly Image _hubIcon = new() { Width = 54, Height = 54 }, _frameIcon = new() { Width = 64, Height = 64 };
    readonly TextBlock _hubName, _name, _detail;
    /// <summary>A red dot on the tile when the app in front is frozen.</summary>
    readonly FrameworkElement _hubFrozen = FrozenDot(inset: 10);
    readonly List<Item> _ring = new();

    RunningApp? _front;
    List<RunningApp> _lastList = [];
    int _selected;
    Spot _spot = Spot.Ring;
    double _turn, _turnV, _unitPx = 1.5;
    bool _animating, _turnHeld, _open, _pictureOn;
    /// <summary>0 = the mini switcher tucked against the card, 1 = full size (it has the D-pad).</summary>
    double _grow, _growV;
    readonly ScaleTransform _zoom = new() { CenterY = CY };
    RunningApp? _shown;
    TimeSpan _last;
    string? _armed;

    /// <summary>The switcher has the D-pad (the panel passes its commands on).</summary>
    public bool Active { get; private set; }

    /// <summary>B or right from the tile: back to the page.</summary>
    public event Action? Exited;
    /// <summary>An app was brought to the front (or A on the tile): the panel should close.</summary>
    public event Action? Done;
    /// <summary>The selected tile or app moved to another view (the panel's glass drop follows it).</summary>
    public event Action<FrameworkElement>? SelectionChanged;
    /// <summary>The glass pieces changed (the panel redraws its glass).</summary>
    public event Action? GlassChanged;

    /// <summary>Set by the panel: shows a window's live picture in a box (switcher units) at an
    /// opacity (0-1), or clears it for IntPtr.Zero; false if there's no picture.</summary>
    public Func<IntPtr, Rect, double, bool>? Preview { get; set; }

    /// <summary>What A does on the selected spot, for the panel's footer.</summary>
    public string AcceptHint => _spot == Spot.Hub ? "Back to game" : Selected?.App == null ? "Close all" : "Switch";

    Item? Selected => _spot == Spot.Ring && _ring.Count > 0 ? _ring[_selected] : null;

    /// <summary>The selected tile or app (the panel's glass drop sits on it while the switcher has the D-pad).</summary>
    public FrameworkElement? SelectedView => _spot == Spot.Hub ? _hub : Selected?.View;

    public AppSwitcher()
    {
        Width = SwitcherWidth;
        Height = SwitcherHeight;
        RenderTransform = _zoom;
        TextElement.SetFontSize(this, 15);

        var tint = Ui.Brush("PanelBrush");
        _band.Data = BandShape();
        _band.Fill = tint;
        _band.Stroke = Ui.Brush("GlassRimBrush");
        Children.Add(_band);

        // The preview: a glass frame around a hole the live picture shows through (it's drawn in
        // the panel's glass window underneath); the app's icon fills in when there's no picture.
        _frameFill = new Border { CornerRadius = new CornerRadius(10), Background = tint, Child = _frameIcon, Margin = new Thickness(PreviewInset) };
        _frame = new Border
        {
            Width = PreviewWidth, Height = PreviewHeight, CornerRadius = new CornerRadius(18),
            BorderBrush = tint, BorderThickness = new Thickness(PreviewInset), Child = _frameFill,
        };
        Put(_frame, PreviewX, CY - PreviewHeight / 2);
        _frameRim = new Border
        {
            Width = PreviewWidth, Height = PreviewHeight, CornerRadius = new CornerRadius(18), Tag = "rim",
            BorderBrush = Ui.Brush("GlassRimBrush"), IsHitTestVisible = false,
        };
        Put(_frameRim, PreviewX, CY - PreviewHeight / 2);
        _name = Label(15);
        _name.Width = PreviewWidth;
        Put(_name, PreviewX, CY + PreviewHeight / 2 + 8);
        _detail = Label(13);
        _detail.FontWeight = FontWeights.Normal;
        _detail.Width = PreviewWidth;
        Put(_detail, PreviewX, CY + PreviewHeight / 2 + 30);

        _hub = Glass(HubSize, 27, new Grid { Children = { _hubIcon, _hubFrozen } });
        _hub.RenderTransformOrigin = new Point(0.5, 0.5);
        Put(_hub, CX - HubSize / 2, CY - 50);
        _hubName = Label(13);
        _hubName.Width = 120;
        Put(_hubName, CX - 60, CY + 48);
        // Tap the tile: back to the app.
        _hub.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Done?.Invoke();
        };

        // Swipe along the arc to turn it; a finger that barely moves is a tap.
        PreviewTouchDown += (_, e) =>
        {
            if (_ring.Count > 0 && _swipe == null) _swipe = new Swipe(e.TouchDevice, e.GetTouchPoint(this).Position.Y, _turn, false);
        };
        PreviewTouchMove += (_, e) =>
        {
            if (_swipe is not { } swipe || e.TouchDevice != swipe.Finger) return;
            double moved = e.GetTouchPoint(this).Position.Y - swipe.StartY;
            if (!swipe.Turning)
            {
                if (Math.Abs(moved) < 10) return;
                CaptureTouch(e.TouchDevice);
                _turnHeld = true;
                _turnV = 0;
                _swipe = swipe with { Turning = true };
            }
            // Up turns it clockwise: the apps follow the finger round the left side.
            _turn = Math.Clamp(swipe.StartTurn - moved / SwipePerStop, -0.4, _ring.Count - 0.6);
            PlaceItems();
            e.Handled = true;
        };
        PreviewTouchUp += (_, e) =>
        {
            if (_swipe is not { } swipe || e.TouchDevice != swipe.Finger) return;
            _swipe = null;
            if (!swipe.Turning) return;
            ReleaseTouchCapture(e.TouchDevice);
            _turnHeld = false;
            _spot = Spot.Ring;
            Select((int)Math.Round(_turn));
            e.Handled = true;
        };

        _countAbove = Count();
        _countBelow = Count();
        var (ax, ay) = OnArc(ArcTop + 24);
        Put(_countAbove, ax - CountSize / 2, ay - CountSize / 2);
        var (bx, by) = OnArc(ArcEnd - 16);
        Put(_countBelow, bx - CountSize / 2, by - CountSize / 2);
    }

    // ---- Opening and closing (with the panel) ----

    /// <summary>The card's left edge (switcher units): the mini switcher shrinks toward it.</summary>
    public void SetAnchor(double cardEdge) => _zoom.CenterX = cardEdge;

    /// <summary>Rims stay one device pixel (set whenever the panel is placed).</summary>
    public void SetUnit(double unitPx)
    {
        _unitPx = unitPx;
        double hair = 1 / unitPx;
        _band.StrokeThickness = hair;
        foreach (var rim in Children.OfType<Border>().Where(b => Equals(b.Tag, "rim"))) rim.BorderThickness = new Thickness(hair);
    }

    /// <summary>The panel opened: the app in front on the tile, the last list of apps at once,
    /// then the fresh one.</summary>
    public async void Open(RunningApp? front)
    {
        _open = true;
        _front = front;
        _armed = null;
        Active = false;
        _spot = front != null ? Spot.Hub : Spot.Ring;
        _grow = _growV = 0;
        ApplyGrow();
        Fill(_lastList, keep: null);
        Refresh();
        try
        {
            var list = await Task.Run(RunningApps.List);
            if (!_open) return;
            _lastList = list;
            Fill(list, keep: Selected?.App?.Key);
            Refresh();
        }
        catch (Exception ex)
        {
            Log.Error("Listing apps for the switcher", ex);
        }
    }

    /// <summary>The app in front, once the panel has found out whose window it is (that can take
    /// a moment for a game): it takes the tile and leaves the arc.</summary>
    public void SetFront(RunningApp? front)
    {
        if (!_open) return;
        bool same = front?.Key == _front?.Key;
        _front = front;
        if (same)
        {
            Refresh();  // same app, fresh numbers (memory)
            return;
        }
        if (!Active && front != null) _spot = Spot.Hub;
        Fill(_lastList, keep: Selected?.App?.Key);
        Refresh();
    }

    /// <summary>The panel closed.</summary>
    public void Dismiss()
    {
        _open = false;
        Active = false;
        StopAnimating();
        _turnHeld = false;
        _swipe = null;
        _pictureOn = false;
        Preview?.Invoke(IntPtr.Zero, Rect.Empty, 0);
        _grow = _growV = 0;  // mini for the next opening
        ApplyGrow();
    }

    /// <summary>The other apps (the one in front is on the tile) and Close background apps last,
    /// keeping the selected app if it's still open.</summary>
    void Fill(List<RunningApp> apps, string? keep)
    {
        foreach (var item in _ring) Children.Remove(item.View);
        _ring.Clear();
        foreach (var app in apps.Where(a => a.Key != _front?.Key))
            Add(app, new Image { Source = IconOf(app) });
        if (_ring.Count > 0)
            Add(null, new TextBlock
            {
                Text = "\uE711", FontFamily = Ui.Icons, FontSize = 18, Foreground = AppsPowerScreen.EndBrush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
        int index = keep != null ? _ring.FindIndex(i => i.App?.Key == keep) : -1;
        _selected = Math.Max(0, index);
        _turn = _selected;
        _turnV = 0;
        _hubIcon.Source = _front != null ? IconOf(_front) : null;
        _hubName.Text = _front?.Name ?? "";
        _hubFrozen.Visibility = _front?.NotResponding == true ? Visibility.Visible : Visibility.Collapsed;
        // Nothing in front (the desktop, Start, Search): no tile at all.
        _hub.Visibility = _hubName.Visibility = _front != null ? Visibility.Visible : Visibility.Hidden;
    }

    void Add(RunningApp? app, FrameworkElement content)
    {
        var view = new Border
        {
            CornerRadius = new CornerRadius(ItemSize / 2), Background = Ui.Brush("CardBrush"),
            Child = app?.NotResponding == true ? new Grid { Children = { content, FrozenDot() } } : content, Opacity = 0,
        };
        var item = new Item(app, view, content);
        // Tap: switch to the app; on Close background apps, select it and press it (twice closes).
        view.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (app != null)
            {
                RunningApps.SwitchTo(app);
                Done?.Invoke();
                return;
            }
            _spot = Spot.Ring;
            Select(_ring.IndexOf(item));
            Accept();
        };
        Children.Add(view);
        _ring.Add(item);
    }

    /// <summary>Worked out with the app in the background (never on the panel's thread).</summary>
    static ImageSource? IconOf(RunningApp app) => app.BigIcon ?? app.Icon;

    // ---- D-pad ----

    /// <summary>D-pad left from the page's left edge: takes the D-pad, starting on the app in front
    /// (left again reaches the arc), if there's anything here.</summary>
    public bool Enter()
    {
        if (!_open || (_ring.Count == 0 && _front == null)) return false;
        Active = true;
        _spot = _front != null ? Spot.Hub : Spot.Ring;
        Refresh();
        Animate();
        return true;
    }

    /// <summary>A command while the switcher has the D-pad; false if the panel should handle it
    /// (the switcher has let go then).</summary>
    public bool Handle(NavCommand command)
    {
        if (!Active) return false;
        if (command is not NavCommand.Accept and not NavCommand.Action) Disarm();
        switch (command)
        {
            case NavCommand.Down when _spot == Spot.Ring:
                Select(_selected + 1);
                break;
            case NavCommand.Up when _spot == Spot.Ring:
                Select(_selected - 1);
                break;
            case NavCommand.Right when _spot == Spot.Ring:
                if (_front != null) Go(Spot.Hub);
                else Leave();
                break;
            case NavCommand.Left when _spot == Spot.Hub:
                if (_ring.Count > 0) Go(Spot.Ring);
                break;
            case NavCommand.Right when _spot == Spot.Hub:
            case NavCommand.Back:
                Leave();
                break;
            case NavCommand.Accept:
                Accept();
                break;
            case NavCommand.Action:
                End();
                break;
            case NavCommand.PrevPage or NavCommand.NextPage or NavCommand.Menu:
                Leave();
                return false;
        }
        return true;
    }

    void Leave()
    {
        Active = false;
        Disarm();
        Refresh();
        Animate();
        Exited?.Invoke();
    }

    void Go(Spot spot)
    {
        _spot = spot;
        Refresh();
    }

    void Select(int index)
    {
        _selected = Math.Clamp(index, 0, Math.Max(0, _ring.Count - 1));
        Refresh();
        Turn();
    }

    void Accept()
    {
        if (_spot == Spot.Hub)
        {
            Done?.Invoke();
            return;
        }
        if (Selected is not { } item) return;
        if (item.App != null)
        {
            // While the panel still has the foreground (Windows lets it hand it on), then close.
            RunningApps.SwitchTo(item.App);
            Done?.Invoke();
            return;
        }
        if (!Arm("close")) return;
        var background = _ring.Select(i => i.App).OfType<RunningApp>().ToList();
        foreach (var each in _ring) Vanish(each.View);
        _ring.Clear();
        _lastList.RemoveAll(a => a.Key != _front?.Key);
        Fill([], keep: null);
        if (_front != null) _spot = Spot.Hub;
        else Leave();
        Refresh();
        _ = Task.WhenAll(background.Select(a => RunningApps.Close(a, AppsPowerScreen.CloseWait)));
    }

    /// <summary>X: ends the selected app (the one in front needs a second X).</summary>
    void End()
    {
        if (_spot == Spot.Hub && _front != null)
        {
            if (!Arm("end")) return;
            var app = _front;
            _lastList.RemoveAll(a => a.Key == app.Key);
            // The next app takes the tile.
            _front = _lastList.FirstOrDefault();
            Fill(_lastList, keep: null);
            if (_ring.Count == 0 && _front == null) Leave();
            Refresh();
            _ = RunningApps.Close(app, AppsPowerScreen.CloseWait);
            return;
        }
        if (Selected is not { App: { } ended } item) return;
        Vanish(item.View);
        _ring.Remove(item);
        _lastList.RemoveAll(a => a.Key == ended.Key);
        // Only Close background apps left: nothing to close any more.
        if (_ring.Count == 1)
        {
            Vanish(_ring[0].View);
            _ring.Clear();
        }
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _ring.Count - 1));
        if (_ring.Count == 0)
        {
            if (_front != null) _spot = Spot.Hub;
            else Leave();
        }
        Refresh();
        Turn();
        _ = RunningApps.Close(ended, AppsPowerScreen.CloseWait);
    }

    /// <summary>The first press arms (the name asks for a second), the second goes ahead.</summary>
    bool Arm(string what)
    {
        if (_armed == what)
        {
            Disarm();
            return true;
        }
        _armed = what;
        Refresh();
        return false;
    }

    void Disarm()
    {
        if (_armed == null) return;
        _armed = null;
        Refresh();
    }

    /// <summary>An ended app's icon shrinks and fades out, then goes (it's already off the arc).</summary>
    void Vanish(FrameworkElement view)
    {
        var shrink = new ScaleTransform();
        view.RenderTransformOrigin = new Point(0.5, 0.5);
        view.RenderTransform = shrink;
        var time = TimeSpan.FromMilliseconds(260);
        var ease = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.4 };
        shrink.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.2, time) { EasingFunction = ease });
        shrink.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.2, time) { EasingFunction = ease });
        var fade = new DoubleAnimation(0, time);
        fade.Completed += (_, _) => Children.Remove(view);
        view.BeginAnimation(OpacityProperty, fade);
    }

    // ---- Drawing ----

    /// <summary>Selection, names, counts, preview and glass for the current state.</summary>
    void Refresh()
    {
        if (_spot == Spot.Ring && _ring.Count == 0 && _front != null) _spot = Spot.Hub;
        bool onHub = Active && _spot == Spot.Hub;

        _hub.RenderTransform = new ScaleTransform(onHub ? 1.06 : 1, onHub ? 1.06 : 1);

        int above = _ring.Count == 0 ? 0 : Math.Max(0, _selected - 2), below = Math.Max(0, _ring.Count - 1 - _selected - 3);
        ShowCount(_countAbove, above);
        ShowCount(_countBelow, below);

        // The preview shows the selected app; on Close background apps its text takes the frame's place.
        bool closing = _spot == Spot.Ring && Selected is { App: null };
        RunningApp? shown = _spot == Spot.Hub ? _front : closing ? null : Selected?.App ?? _front;
        _frame.Visibility = _frameRim.Visibility = shown != null ? Visibility.Visible : Visibility.Hidden;
        _name.Text = closing ? (_armed == "close" ? "Press A again to close them" : "Close background apps")
            : _armed == "end" ? "Press X again to end" : shown?.Name ?? "";
        SetTop(_name, closing ? CY - 10 : CY + PreviewHeight / 2 + 8);
        // Under the name: its memory, or that it's frozen.
        _detail.Text = closing || shown == null ? "" : shown.NotResponding ? "Not responding" : shown.MemoryBytes > 0 ? Size(shown.MemoryBytes) : "";
        _detail.Foreground = shown?.NotResponding == true ? AppsPowerScreen.EndBrush : Ui.Brush("SubtleTextBrush");
        _shown = shown;
        _frameIcon.Source = shown != null ? IconOf(shown) : null;
        UpdatePicture();

        PlaceItems();
        GlassChanged?.Invoke();
        if (Active && SelectedView is { } view) SelectionChanged?.Invoke(view);
    }

    void ShowCount(Border count, int value)
    {
        ((TextBlock)count.Child).Text = $"+{value}";
        count.Visibility = value > 0 ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>Puts the arc's stops where the turn has them (a fraction while turning).</summary>
    void PlaceItems()
    {
        for (int i = 0; i < _ring.Count; i++)
        {
            var (_, view, content) = _ring[i];
            double d = i - _turn;
            bool selected = Active && _spot == Spot.Ring && i == _selected;
            double size = selected ? SelectedSize : ItemSize;
            var (x, y) = OnArc(180 - d * Step);
            view.Width = view.Height = size;
            view.CornerRadius = new CornerRadius(size / 2);
            if (content is Image) content.Width = content.Height = size * 0.62;
            SetLeft(view, x - size / 2);
            SetTop(view, y - size / 2);
            SetZIndex(view, selected ? 2 : 1);
            double opacity = Fade(d);
            view.Opacity = opacity;
            view.Visibility = opacity > 0.01 ? Visibility.Visible : Visibility.Hidden;
        }
    }

    /// <summary>Two stops fit above the selected one (the top one faded), three below; beyond, they fade out.</summary>
    static double Fade(double d)
    {
        if (d >= 0) return d <= 3 ? 1 - d * 0.06 : Math.Max(0, 0.82 - (d - 3) * 2.2);
        if (d >= -1) return 1 + d * 0.15;
        if (d >= -2) return 0.85 - (-d - 1) * 0.43;
        return Math.Max(0, 0.42 - (-d - 2) * 1.2);
    }

    void Turn() => Animate();

    /// <summary>The live picture: only while the switcher has the D-pad and has grown to full
    /// size (Windows draws it as a fixed rectangle, so not while it's growing).</summary>
    void UpdatePicture()
    {
        bool want = _shown != null && _open && Active && _grow > 0.97;
        if (want && !_pictureOn)
        {
            // It fades in over the empty glass frame (no icon first, no pop).
            _pictureFade = 0;
            Animate();
        }
        bool pictured = want && Preview?.Invoke(_shown!.Window, PreviewBox, _pictureFade) == true;
        if (!want && _pictureOn) Preview?.Invoke(IntPtr.Zero, Rect.Empty, 0);
        _pictureOn = want;
        // The app's icon only stands in when Windows has no picture of it.
        _frameFill.Visibility = want && !pictured ? Visibility.Visible : Visibility.Hidden;
    }

    static Rect PreviewBox => new(PreviewX + PreviewInset, CY - PreviewHeight / 2 + PreviewInset,
        PreviewWidth - 2 * PreviewInset, PreviewHeight - 2 * PreviewInset);

    /// <summary>The live picture's opacity while it fades in (0-1).</summary>
    double _pictureFade = 1;

    /// <summary>Mini or full size for the current grow; the preview and names fade in late.</summary>
    void ApplyGrow()
    {
        _zoom.ScaleX = _zoom.ScaleY = Mini + (1 - Mini) * _grow;
        double late = Math.Clamp(_grow * 1.6 - 0.6, 0, 1);
        _frame.Opacity = _frameRim.Opacity = _name.Opacity = _detail.Opacity = late;
        _hubName.Opacity = Math.Clamp(_grow, 0, 1);
    }

    /// <summary>Runs the turn and grow springs per frame until both rest.</summary>
    void Animate()
    {
        if (_animating) return;
        _animating = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    void StopAnimating()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _last) return;  // Rendering can fire more than once per frame
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;
        double grown = Active ? 1 : 0;
        bool wasFull = _grow > 0.97;
        for (double left = dt; left > 1e-6; left -= 1 / 120.0)
        {
            double step = Math.Min(left, 1 / 120.0);
            if (!_turnHeld)
            {
                _turnV += (Stiffness * (_selected - _turn) - Damping * _turnV) * step;
                _turn += _turnV * step;
            }
            _growV += (GrowStiffness * (grown - _grow) - GrowDamping * _growV) * step;
            _grow += _growV * step;
        }
        bool turned = _turnHeld || (Math.Abs(_selected - _turn) < 0.001 && Math.Abs(_turnV) < 0.01);
        bool sized = Math.Abs(grown - _grow) < 0.001 && Math.Abs(_growV) < 0.01;
        if (turned && !_turnHeld) (_turn, _turnV) = (_selected, 0);
        if (sized) (_grow, _growV) = (grown, 0);
        PlaceItems();
        ApplyGrow();
        // At full size (and once more when it rests, for the exact spot) the picture comes in.
        if ((_grow > 0.97) != wasFull || (sized && Active)) UpdatePicture();
        bool faded = !_pictureOn || _pictureFade >= 1;
        if (!faded)
        {
            _pictureFade = Math.Min(1, _pictureFade + dt / 0.18);
            if (_shown != null) Preview?.Invoke(_shown.Window, PreviewBox, _pictureFade);
        }
        GlassChanged?.Invoke();  // the glass follows the size
        if (turned && sized && faded) StopAnimating();
    }

    /// <summary>The blurred pieces under the switcher (switcher units, corner radius): the arc as
    /// a row of overlapping circles, the tile, the preview frame and the counts.</summary>
    public List<(Rect Area, double Radius, double Opacity)> GlassPieces()
    {
        var pieces = new List<(Rect, double, double)>();
        if (!_open) return pieces;
        void Circle(double cx, double cy, double r) => pieces.Add((new Rect(cx - r, cy - r, 2 * r, 2 * r), r, 1));
        for (double angle = ArcEnd; angle <= ArcTop + 0.01; angle += 3)
        {
            var (x, y) = OnArc(angle);
            Circle(x, y, HalfBand);
        }
        if (_front != null) pieces.Add((new Rect(CX - HubSize / 2, CY - 50, HubSize, HubSize), 27, 1));
        if (_frame.Visibility == Visibility.Visible && _frame.Opacity > 0.01)
            pieces.Add((new Rect(PreviewX, CY - PreviewHeight / 2, PreviewWidth, PreviewHeight), 18, _frame.Opacity));
        foreach (var count in new[] { _countAbove, _countBelow })
            if (count.Visibility == Visibility.Visible) Circle(GetLeft(count) + CountSize / 2, GetTop(count) + CountSize / 2, CountSize / 2);
        return pieces;
    }

    static (double X, double Y) OnArc(double degrees)
    {
        double a = degrees * Math.PI / 180;
        return (CX + R * Math.Cos(a), CY + R * Math.Sin(a));
    }

    /// <summary>The arc: a band around the tile from above-left, round the bottom, to a rounded
    /// end short of the card.</summary>
    static Geometry BandShape()
    {
        double outer = R + HalfBand, inner = R - HalfBand;
        Point At(double degrees, double radius)
        {
            double a = degrees * Math.PI / 180;
            return new Point(CX + radius * Math.Cos(a), CY + radius * Math.Sin(a));
        }
        bool large = ArcTop - ArcEnd > 180;
        var figure = new PathFigure { StartPoint = At(ArcTop, outer), IsClosed = true };
        figure.Segments.Add(new ArcSegment(At(ArcEnd, outer), new Size(outer, outer), 0, large, SweepDirection.Counterclockwise, true));
        figure.Segments.Add(new ArcSegment(At(ArcEnd, inner), new Size(HalfBand, HalfBand), 0, false, SweepDirection.Counterclockwise, true));
        figure.Segments.Add(new ArcSegment(At(ArcTop, inner), new Size(inner, inner), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new ArcSegment(At(ArcTop, outer), new Size(HalfBand, HalfBand), 0, false, SweepDirection.Counterclockwise, true));
        var shape = new PathGeometry([figure]);
        shape.Freeze();
        return shape;
    }

    // ---- Building blocks ----

    void Put(UIElement element, double x, double y)
    {
        SetLeft(element, x);
        SetTop(element, y);
        if (!Children.Contains(element)) Children.Add(element);
    }

    /// <summary>A glass piece: the panel's tint, a lit rim, rounded.</summary>
    static Border Glass(double size, double radius, UIElement child) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(radius), Background = Ui.Brush("PanelBrush"),
        BorderBrush = Ui.Brush("GlassRimBrush"), Child = child, Tag = "rim",
    };

    static FrameworkElement FrozenDot(double inset = 1) => new System.Windows.Shapes.Ellipse
    {
        Width = 10, Height = 10, Fill = AppsPowerScreen.EndBrush, Margin = new Thickness(0, inset, inset, 0),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
    };

    static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes >> 20} MB";

    static Border Count() => Glass(CountSize, CountSize / 2, new TextBlock
    {
        FontSize = 13, FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    });

    static TextBlock Label(double size) => new()
    {
        FontSize = size, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        // Readable straight over the game.
        Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.6, Color = Colors.Black },
    };
}
