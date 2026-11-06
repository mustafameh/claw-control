using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Performance;

/// <summary>
/// The Quick tab's now-playing tile (design "B", chosen 2026-11-06 from the mockups): the cover's
/// colours glow behind it and faint bars in the cover's colour move with the sound. Compact while
/// the selection is elsewhere: cover, title, time, and previous / play-pause in a progress ring /
/// next. When the selection comes onto it, it opens: a big cover, title, artist and app, a seek
/// line with the time gone and left, and big previous / play / next. Leaving closes it again.
/// Taps on the compact buttons act without opening it; a tap on the cover or title opens it.
/// Nothing runs while the panel is closed: the clock ticks once a second and the bars listen
/// only while it's open and playing.
/// </summary>
sealed class NowPlayingTile : Border
{
    // Corners: like the tiles around it while compact; rounder open, as a big card (11 looked too square at full size).
    const double CompactHeight = 64, CompactRadius = 11, OpenRadius = 18;

    static readonly Geometry PlayShape = Shape("M7 4.5v15l13-7.5z");
    static readonly Geometry PauseShape = Shape("M6 4h4v16H6zM14 4h4v16h-4z");
    static readonly Geometry PreviousShape = Shape("M6 5h2.4v14H6zM20 5v14L9.5 12z");
    static readonly Geometry NextShape = Shape("M15.6 5H18v14h-2.4zM4 5v14l10.5-7z");

    static readonly Brush White = Brushes.White;
    static readonly Brush Dark = Solid(0xFF, 0x1C, 0x1C, 0x1E);
    static readonly Brush Soft78 = Solid(0xC7, 0xFF, 0xFF, 0xFF);
    static readonly Brush Soft75 = Solid(0xBF, 0xFF, 0xFF, 0xFF);
    static readonly Brush Soft70 = Solid(0xB3, 0xFF, 0xFF, 0xFF);
    static readonly Brush Soft55 = Solid(0x8C, 0xFF, 0xFF, 0xFF);
    static readonly Brush SideFace = Solid(0x1A, 0xFF, 0xFF, 0xFF);
    static readonly Brush Shade = Solid(0x52, 0x14, 0x10, 0x0E);
    static readonly Brush RingTrack = Solid(0x40, 0xFF, 0xFF, 0xFF);
    static readonly Brush RingInside = Solid(0x40, 0x00, 0x00, 0x00);

    readonly MediaService _media;
    readonly AudioSpectrum _spectrum = new();
    readonly Grid _root;
    readonly Image _glow;
    readonly SpectrumBars _bars;
    readonly Grid _compact;
    readonly StackPanel _expanded;
    readonly Cover _smallCover, _bigCover;
    readonly TextBlock _compactTitle, _compactTime, _title, _artist, _app, _elapsed, _remaining;
    readonly Button _compactInfo, _compactPrevious, _compactPlay, _compactNext, _previous, _play, _next;
    readonly Path _ring, _compactPlayIcon, _playIcon;
    readonly SeekBar _seek;
    readonly FrameworkElement _seekBlock;
    readonly DispatcherTimer _tick, _gone;
    MediaInfo? _info;
    ImageSource? _shownArt;
    bool _shown, _editing, _tapping;

    public NowPlayingTile(MediaService media)
    {
        _media = media;
        CornerRadius = new CornerRadius(CompactRadius);
        Background = Ui.Brush("CardBrush");
        Margin = new Thickness(4, 0, 4, 5);
        Height = CompactHeight;
        Visibility = Visibility.Collapsed;

        // Behind everything: the cover blurred (a small pre-blurred copy stretched, so nothing
        // blurs per frame), a dark wash so white text reads on any cover, and the bars.
        _glow = new Image { Stretch = Stretch.UniformToFill, Opacity = 0.5, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(_glow, BitmapScalingMode.Linear);
        _bars = new SpectrumBars(_spectrum) { Opacity = 0.3, Margin = new Thickness(10, 0, 10, 0), CompactHeight = CompactHeight };

        // Compact: [cover · title / time] [previous] [play-pause in a ring] [next].
        _smallCover = new Cover(44, 10, 18, shadow: 12);
        _compactTitle = Text(14, White, bold: true);
        _compactTime = Text(12, Soft75);
        var compactText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _compactTitle, _compactTime } };
        var compactLine = new DockPanel { Children = { _smallCover.View, compactText } };
        DockPanel.SetDock(_smallCover.View, Dock.Left);
        _smallCover.View.Margin = new Thickness(0, 0, 12, 0);
        _compactInfo = new Button { Template = PlainTemplate(10), Content = compactLine, Height = 54, FocusVisualStyle = null, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _compactInfo.Click += (_, _) => SetOpen(true);

        _compactPrevious = Round(30, null, Icon(PreviousShape, 16, White, out _));
        _compactNext = Round(30, null, Icon(NextShape, 16, White, out _));
        _ring = new Path { Stroke = White, StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var ringFace = new Grid
        {
            Width = 34, Height = 34,
            Children =
            {
                new Ellipse { Fill = RingInside, Stroke = RingTrack, StrokeThickness = 2.5, Margin = new Thickness(1.75) },
                _ring,
                Icon(PauseShape, 12, White, out _compactPlayIcon),
            },
        };
        _compactPlay = Round(34, null, ringFace);
        _compactPrevious.Click += (_, _) => Previous();
        _compactPlay.Click += (_, _) => PlayPause();
        _compactNext.Click += (_, _) => Next();

        _compact = new Grid { Height = CompactHeight, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 6, 0) };
        _compact.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var _ in new[] { 1, 2, 3 }) _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var (control, column) in new[] { (_compactInfo, 0), (_compactPrevious, 1), (_compactPlay, 2), (_compactNext, 3) })
        {
            Grid.SetColumn(control, column);
            control.VerticalAlignment = VerticalAlignment.Center;
            _compact.Children.Add(control);
            // A tap here does the button's job without opening the tile.
            control.PreviewMouseLeftButtonDown += (_, _) => Tapped();
            control.PreviewTouchDown += (_, _) => Tapped();
        }

        // Open: big cover with title, artist and app; the seek line and times; big buttons.
        _bigCover = new Cover(84, 14, 30, shadow: 22);
        _title = Text(18, White, bold: true);
        _artist = Text(14, Soft78);
        _app = Text(12, Soft55);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _artist, _app } };
        var header = new DockPanel { Height = 84, Background = Brushes.Transparent, Children = { _bigCover.View, words } };
        DockPanel.SetDock(_bigCover.View, Dock.Left);
        _bigCover.View.Margin = new Thickness(0, 0, 14, 0);
        // A tap on the cover or title closes it again (touch).
        header.MouseLeftButtonUp += (_, _) => SetOpen(false);

        _seek = new SeekBar();
        _seek.Seeked += seconds => _media.Seek(TimeSpan.FromSeconds(seconds));
        _seek.ValueChanged += (_, _) => { if (_seek.Holding) ShowTime(); };
        PanelInput.SetAccept(_seek, PlayPause);
        _elapsed = Text(12, Soft70);
        _remaining = Text(12, Soft70);
        _remaining.HorizontalAlignment = HorizontalAlignment.Right;
        var times = new Grid { Margin = new Thickness(2, 3, 2, 0), Children = { _elapsed, _remaining } };
        _seekBlock = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Children = { _seek, times } };

        _previous = Round(46, SideFace, Icon(PreviousShape, 22, White, out _));
        _play = Round(58, White, Icon(PauseShape, 22, Dark, out _playIcon));
        _next = Round(46, SideFace, Icon(NextShape, 22, White, out _));
        _play.Margin = new Thickness(18, 0, 18, 0);
        _previous.Click += (_, _) => Previous();
        _play.Click += (_, _) => PlayPause();
        _next.Click += (_, _) => Next();
        PanelInput.SetHint(_previous, "Previous");
        // Left / right stay on this row (then the app switcher), never down to the power modes.
        foreach (var button in new[] { _previous, _play, _next }) PanelInput.SetStayInRow(button, true);
        PanelInput.SetHint(_next, "Next");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
            Children = { _previous, _play, _next },
        };
        _expanded = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 16, 16, 10), Opacity = 0, IsHitTestVisible = false,
            Children = { header, _seekBlock, buttons },
        };
        PanelInput.SetSkip(_expanded, true);

        _root = new Grid { Children = { _glow, new Border { Background = Shade, IsHitTestVisible = false }, _bars, _compact, _expanded } };
        _root.SizeChanged += (_, _) => Round();
        Child = _root;

        // Opens when the selection comes onto it, closes when it leaves (logical focus: the
        // panel isn't always the active window).
        AddHandler(GotFocusEvent, new RoutedEventHandler(OnSelectionIn), true);
        AddHandler(LostFocusEvent, new RoutedEventHandler(OnSelectionOut), true);
        _tick = new DispatcherTimer();
        _tick.Tick += (_, _) => { ShowTime(); Plan(); };
        _gone = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _gone.Tick += (_, _) =>
        {
            _gone.Stop();
            Show(_media.Current, _editing, now: true);
        };
        // The panel appearing (or the tile being hidden in Edit) starts or stops the bars.
        IsVisibleChanged += (_, _) => Plan();
    }

    /// <summary>The Quick tab is showing (panel open): the clock and the bars may run.</summary>
    public void OnShown()
    {
        _shown = true;
        Plan();
    }

    /// <summary>Debug captures: the tile open with play / pause to select (null when nothing plays), or closed again.</summary>
    public FrameworkElement? ShowOpenForCapture(bool open)
    {
        if (_info == null) return null;
        SetOpen(open, animate: false);
        return open ? _play : null;
    }

    /// <summary>Gone (panel closed, other tab): compact again, nothing running.</summary>
    public void OnHidden()
    {
        _shown = false;
        SetOpen(false, animate: false);
        Plan();
        _bars.StopNow();
    }

    /// <summary>What's playing (null = nothing; while editing the Quick tab it shows anyway, so it can be moved).</summary>
    public void Show(MediaInfo? info, bool editing) => Show(info, editing, now: false);

    void Show(MediaInfo? info, bool editing, bool now)
    {
        // Between tracks apps often clear the title for a moment (Brave after Next): the last
        // track stays a little, so the tile and a selection on it don't drop out and come back.
        if (info == null && _info != null && _shown && !editing && !now)
        {
            _editing = editing;
            if (!_gone.IsEnabled) _gone.Start();
            return;
        }
        _gone.Stop();
        _info = info;
        _editing = editing;
        Visibility = info == null && !editing ? Visibility.Collapsed : Visibility.Visible;
        if (editing || info == null) SetOpen(false, animate: false);

        _compactTitle.Text = _title.Text = info?.Title ?? "Now playing";
        _artist.Text = info?.Artist ?? "";
        _artist.Visibility = _artist.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _app.Text = info?.App ?? "";
        var cover = info?.Cover;
        if (cover?.Art != _shownArt)
        {
            _shownArt = cover?.Art;
            _smallCover.Show(cover?.Art);
            _bigCover.Show(cover?.Art);
            _glow.Source = cover?.Glow;
            _bars.Tint = cover?.Tint ?? Color.FromRgb(0xE6, 0xE8, 0xEE);
        }
        Dim(_compactPrevious, info?.CanPrevious ?? false);
        Dim(_previous, info?.CanPrevious ?? false);
        Dim(_compactNext, info?.CanNext ?? false);
        Dim(_next, info?.CanNext ?? false);

        // The seek line only for apps that say where they are; dragging it only if they take seeks.
        var timeline = info?.Timeline;
        // While it's selected it stays (a track change can drop the timeline for a moment).
        var seekShown = timeline != null || _seek.IsFocused ? Visibility.Visible : Visibility.Collapsed;
        _seekBlock.Visibility = seekShown;
        _seek.IsEnabled = (timeline?.CanSeek ?? false) || _seek.IsFocused;
        // A new track can change the open tile's height (no artist, no seek line).
        if (IsOpen && !HasAnimatedProperties)
        {
            double height = OpenHeight();
            if (Math.Abs(height - Height) > 0.5)
            {
                Height = height;
                Round();
            }
        }
        ShowPlaying();
        ShowTime();
        Plan();
    }

    bool IsOpen => PanelInput.GetSkip(_compact);

    void ShowPlaying()
    {
        bool playing = _info?.Playing ?? false;
        _compactPlayIcon.Data = _playIcon.Data = playing ? PauseShape : PlayShape;
        string hint = playing ? "Pause" : "Play";
        PanelInput.SetHint(_play, hint);
        PanelInput.SetHint(_seek, hint);
    }

    /// <summary>The times, the ring and the seek line, from the track's clock (or the line while it's being moved).</summary>
    void ShowTime()
    {
        var info = _info;
        if (info?.Timeline is not { } timeline)
        {
            _compactTime.Text = info == null ? "" : string.Join(" · ", new[] { info.Artist, info.App }.Where(s => s.Length > 0));
            // An empty line would push the title off the middle.
            _compactTime.Visibility = _compactTime.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _ring.Data = null;
            return;
        }
        _compactTime.Visibility = Visibility.Visible;
        var position = _seek.Holding ? TimeSpan.FromSeconds(_seek.Value) : timeline.Now(info.Playing);
        if (position > timeline.Length) position = timeline.Length;
        _compactTime.Text = $"{Clock(position)} / {Clock(timeline.Length)}";
        _elapsed.Text = Clock(position);
        _remaining.Text = "-" + Clock(timeline.Length - position);
        _seek.Show(position.TotalSeconds, timeline.Length.TotalSeconds);
        _ring.Data = Arc(position.TotalSeconds / timeline.Length.TotalSeconds);
    }

    /// <summary>Starts or stops the clock and the bars for how things are now.</summary>
    void Plan()
    {
        bool running = _shown && !_editing && IsVisible && _info is { Playing: true };
        if (running) _spectrum.Start();
        else _spectrum.Stop();
        _bars.Run(running);
        if (running && _info?.Timeline is { } timeline)
        {
            // Next tick just after the next whole second of the track.
            int wait = 1000 - timeline.Now(true).Milliseconds + 20;
            _tick.Interval = TimeSpan.FromMilliseconds(wait);
            _tick.Start();
        }
        else _tick.Stop();
    }

    void PlayPause()
    {
        _media.PlayPause();
        // Shown at once; Windows confirms a moment later.
        if (_info is { } info)
        {
            var timeline = info.Timeline is { } t ? t with { Position = t.Now(info.Playing), At = DateTime.UtcNow } : null;
            _info = info with { Playing = !info.Playing, Timeline = timeline };
            ShowPlaying();
            ShowTime();
            Plan();
        }
    }

    void Previous()
    {
        if (_info?.CanPrevious == true) _media.Previous();
    }

    void Next()
    {
        if (_info?.CanNext == true) _media.Next();
    }

    void Tapped()
    {
        _tapping = true;
        Dispatcher.BeginInvoke(() => _tapping = false, DispatcherPriority.Background);
    }

    void OnSelectionIn(object sender, RoutedEventArgs e)
    {
        if (_editing || _tapping) return;
        bool fromCompact = e.OriginalSource is DependencyObject source && _compact.IsAncestorOf(source);
        SetOpen(true);
        // Coming in from above or below lands on play / pause.
        if (fromCompact) Dispatcher.BeginInvoke(() => Select(_play), DispatcherPriority.Loaded);
    }

    void OnSelectionOut(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (!IsOpen) return;
        var scope = FocusManager.GetFocusScope(this);
        if (scope != null && FocusManager.GetFocusedElement(scope) is DependencyObject now && IsAncestorOf(now)) return;
        SetOpen(false);
    }, DispatcherPriority.Input);

    static void Select(FrameworkElement element)
    {
        if (!element.IsVisible) return;
        element.Focus();
        if (FocusManager.GetFocusScope(element) is { } scope) FocusManager.SetFocusedElement(scope, element);
        element.BringIntoView();
    }

    int _morph;
    ScrollViewer? _held;
    const int MorphMs = 340;

    /// <summary>Parts both layouts have: the open one grows out of the compact one's spot (and
    /// shrinks back into it when closing).</summary>
    (FrameworkElement Small, FrameworkElement Big)[] Shared() =>
    [
        (_smallCover.View, _bigCover.View), (_compactTitle, _title),
        (_compactPrevious, _previous), (_compactPlay, _play), (_compactNext, _next),
    ];

    /// <summary>Only in the open tile: they fade in under the title.</summary>
    FrameworkElement[] OpenOnly() => [_artist, _app, _seekBlock];

    /// <summary>
    /// Opens or closes the tile. Animated, it morphs: the cover grows out of the small one, the
    /// title out of the small title, and previous / play / next slide down from the compact row
    /// into their places, while the tile grows and the tiles below glide down. The panel itself
    /// takes its new size once (the page is held that tall for the move): growing it every frame
    /// resized the see-through window and its glass each frame, and Windows drew each new strip
    /// late (an empty band, then a snap). Only drawing transforms and the tile's height move.
    /// </summary>
    void SetOpen(bool open, bool animate = true)
    {
        if (open == IsOpen) return;
        int morph = ++_morph;
        Settle();
        PanelInput.SetSkip(_compact, open);
        PanelInput.SetSkip(_expanded, !open);
        _compact.IsHitTestVisible = !open;
        _expanded.IsHitTestVisible = open;
        double from = ActualHeight, to = open ? OpenHeight() : CompactHeight;
        CornerRadius = new CornerRadius(open ? OpenRadius : CompactRadius);
        bool move = animate && !Motion.Reduced && IsVisible && from > 0;
        if (!move)
        {
            Animate(this, HeightProperty, to, 0);
            _compact.Opacity = open ? 0 : 1;
            _expanded.Opacity = open ? 1 : 0;
            Animate(_glow, OpacityProperty, open ? 0.85 : 0.5, 0);
            Animate(_bars, OpacityProperty, open ? 0.55 : 0.3, 0);
            Round();
            return;
        }

        // Where each shared part is in both layouts (the open one is laid out even while hidden).
        var moves = Shared().Select(pair => (pair.Big, Spot: Bounds(pair.Small), Home: Bounds(pair.Big))).ToArray();
        Hold(to - from);
        var time = TimeSpan.FromMilliseconds(MorphMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimation(from, to, time) { EasingFunction = ease };
        grow.Completed += (_, _) =>
        {
            if (morph != _morph) return;
            Release();
            // Hand the height back from the animation, so a new track can change it.
            BeginAnimation(HeightProperty, null);
            Height = to;
            if (!open)
            {
                _expanded.Opacity = 0;
                _compact.Opacity = 1;
            }
            ClearMoves();
        };
        BeginAnimation(HeightProperty, grow);
        Round();
        if (open)
        {
            _compact.Opacity = 0;
            _expanded.Opacity = 1;
            foreach (var (big, spot, home) in moves) Move(big, spot, home, toSpot: false, time, ease);
            foreach (var part in OpenOnly()) Fade(part, 0, 1, 300, new SineEase { EasingMode = EasingMode.EaseIn }, 10);
        }
        else
        {
            foreach (var (big, spot, home) in moves) Move(big, spot, home, toSpot: true, time, ease);
            foreach (var part in OpenOnly()) Fade(part, 1, 0, 120, new QuadraticEase { EasingMode = EasingMode.EaseOut }, 0);
        }
        Animate(_glow, OpacityProperty, open ? 0.85 : 0.5, MorphMs);
        Animate(_bars, OpacityProperty, open ? 0.55 : 0.3, MorphMs);
    }

    /// <summary>A change while one is still moving: the moving one jumps to its end first.</summary>
    void Settle()
    {
        if (HasAnimatedProperties)
        {
            double now = ActualHeight;
            BeginAnimation(HeightProperty, null);
            Height = now;
        }
        Release();
        ClearMoves();
        bool open = IsOpen;
        _compact.Opacity = open ? 0 : 1;
        _expanded.Opacity = open ? 1 : 0;
    }

    void ClearMoves()
    {
        foreach (var (_, big) in Shared()) big.RenderTransform = Transform.Identity;
        foreach (var part in OpenOnly())
        {
            part.BeginAnimation(OpacityProperty, null);
            part.Opacity = 1;
            part.RenderTransform = Transform.Identity;
        }
    }

    Rect Bounds(FrameworkElement part) => part.TransformToAncestor(this).TransformBounds(new Rect(part.RenderSize));

    /// <summary>From the compact part's spot and size to the open part's own (or back).</summary>
    static void Move(FrameworkElement part, Rect spot, Rect home, bool toSpot, TimeSpan time, IEasingFunction ease)
    {
        double scale = home.Height > 0 ? spot.Height / home.Height : 1;
        var size = new ScaleTransform();
        var shift = new TranslateTransform();
        part.RenderTransformOrigin = new Point(0, 0);
        part.RenderTransform = new TransformGroup { Children = { size, shift } };
        void Run(Animatable target, DependencyProperty property, double spotValue, double homeValue) =>
            target.BeginAnimation(property, new DoubleAnimation(toSpot ? homeValue : spotValue, toSpot ? spotValue : homeValue, time) { EasingFunction = ease });
        Run(size, ScaleTransform.ScaleXProperty, scale, 1);
        Run(size, ScaleTransform.ScaleYProperty, scale, 1);
        Run(shift, TranslateTransform.XProperty, spot.X - home.X, 0);
        Run(shift, TranslateTransform.YProperty, spot.Y - home.Y, 0);
    }

    /// <summary>Fades a part in (rising a little into place) or out.</summary>
    static void Fade(FrameworkElement part, double from, double to, int ms, IEasingFunction ease, double rise)
    {
        var time = TimeSpan.FromMilliseconds(ms);
        part.Opacity = from;
        part.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to, time) { EasingFunction = ease });
        if (rise == 0) return;
        var shift = new TranslateTransform();
        part.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, 0, time) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>Holds the page at least as tall as it'll be (no taller than the panel may get),
    /// so the panel window takes its new size once instead of every frame.</summary>
    void Hold(double growth)
    {
        ScrollViewer? scroller = null;
        for (var node = VisualTreeHelper.GetParent(this); node != null && scroller == null; node = VisualTreeHelper.GetParent(node))
            scroller = node as ScrollViewer;
        if (scroller == null || Window.GetWindow(this) is not { } window) return;
        double extra = Math.Max(0, growth);
        if (extra > 0 && !double.IsInfinity(window.MaxHeight))
        {
            double scale = scroller.TransformToAncestor(window).TransformBounds(new Rect(0, 0, 1, 1)).Height;
            if (scale > 0) extra = Math.Min(extra, Math.Max(0, (window.MaxHeight - window.ActualHeight) / scale));
        }
        scroller.MinHeight = scroller.ActualHeight + extra;
        _held = scroller;
    }

    void Release()
    {
        if (_held == null) return;
        _held.MinHeight = 0;
        _held = null;
    }

    /// <summary>Clips the glow and bars to the tile's rounded shape.</summary>
    void Round()
    {
        double radius = CornerRadius.TopLeft;
        _root.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, Height is double h && !double.IsNaN(h) ? h : ActualHeight), radius, radius);
    }

    double OpenHeight()
    {
        _expanded.Measure(new Size(ActualWidth > 0 ? ActualWidth : 440, double.PositiveInfinity));
        double height = Math.Ceiling(_expanded.DesiredSize.Height);
        _bars.OpenHeight = height;
        return height;
    }

    static void Animate(UIElement element, DependencyProperty property, double to, int ms)
    {
        if (ms == 0)
        {
            element.BeginAnimation(property, null);
            element.SetValue(property, to);
            return;
        }
        element.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>Faint and inactive when the app doesn't offer it (stays selectable, so the selection never drops out).</summary>
    static void Dim(Button button, bool available) => button.Opacity = available ? 1 : 0.35;

    /// <summary>The progress ring: from the top, clockwise.</summary>
    static Geometry? Arc(double share)
    {
        share = Math.Clamp(share, 0, 1);
        if (share < 0.002) return null;
        const double Centre = 17, R = 14;
        if (share > 0.998) return new EllipseGeometry(new Point(Centre, Centre), R, R);
        double angle = share * 2 * Math.PI;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(Centre, Centre - R), false, false);
            path.ArcTo(new Point(Centre + R * Math.Sin(angle), Centre - R * Math.Cos(angle)), new Size(R, R), 0, share > 0.5, SweepDirection.Clockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    static string Clock(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
        : $"{(int)time.TotalMinutes}:{time.Seconds:00}";

    static TextBlock Text(double size, Brush color, bool bold = false)
    {
        var text = new TextBlock
        {
            FontSize = size, Foreground = color, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        return text;
    }

    static Geometry Shape(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    static FrameworkElement Icon(Geometry shape, double size, Brush fill, out Path path)
    {
        path = new Path { Data = shape, Fill = fill };
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
    }

    /// <summary>A round button: a face (or none) with a clear 5-unit ring round it, like the mode
    /// circles, so the glass drop sits round the face. The face grows under the drop.</summary>
    static Button Round(double face, Brush? fill, FrameworkElement icon)
    {
        double size = face + 10;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var content = new Border { Width = face, Height = face, CornerRadius = new CornerRadius(face / 2), Background = fill ?? Brushes.Transparent, Child = icon };
        return new Button { Template = PlainTemplate(size / 2), Content = content, Width = size, Height = size, FocusVisualStyle = null };
    }

    /// <summary>A button that draws nothing itself (the drop shows the selection); its corners guide the drop's.</summary>
    static ControlTemplate PlainTemplate(double corner)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(corner));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    static SolidColorBrush Solid(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>A rounded cover with a soft shadow, or a note sign while there's no picture.</summary>
    sealed class Cover
    {
        public readonly Border View;
        readonly TextBlock _note;

        public Cover(double size, double corner, double noteSize, double shadow)
        {
            _note = new TextBlock
            {
                Text = "", FontFamily = Ui.Icons, FontSize = noteSize, Foreground = Ui.Brush("SubtleTextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            View = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(corner), Child = _note,
                Background = Ui.Brush("TrackBrush"), VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect { BlurRadius = shadow, ShadowDepth = shadow / 4, Direction = 270, Opacity = 0.35, Color = Colors.Black },
            };
        }

        public void Show(ImageSource? art)
        {
            if (art == null)
            {
                View.Background = Ui.Brush("TrackBrush");
                _note.Visibility = Visibility.Visible;
                return;
            }
            var brush = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            View.Background = brush;
            _note.Visibility = Visibility.Collapsed;
        }
    }
}
