using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClawControl.Core;
using ClawControl.Features;
using ClawControl.Features.Apps;
using ClawControl.Hardware;
using ClawControl.Input;

namespace ClawControl.UI;

/// <summary>
/// The side panel. Built once at startup and only shown/hidden afterwards, so opening it is
/// instant. Remembers the page and the focused control between openings.
/// </summary>
public partial class OverlayWindow : Window
{
    readonly IReadOnlyList<IPage> _pages;
    readonly Settings _settings;
    readonly Dictionary<IPage, FrameworkElement> _lastFocus = new();
    readonly DispatcherTimer _clock;
    readonly Connectivity _net;
    int _pageIndex;
    DateTime _shownAt;
    static readonly TimeSpan DuplicateWindow = TimeSpan.FromMilliseconds(150);
    NavCommand? _lastCommand;
    InputSource _lastSource;
    DateTime _lastCommandAt;

    public bool SaveSnapshots { get; set; }

    public OverlayWindow(IReadOnlyList<IPage> pages, Settings settings, Connectivity net)
    {
        InitializeComponent();
        _pages = pages;
        _settings = settings;
        _net = net;
        _pageIndex = Math.Max(0, pages.ToList().FindIndex(p => p.Id == settings.LastPage));

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _clock.Tick += (_, _) => UpdateHeader();
        _net.Changed += () => Dispatcher.BeginInvoke(ShowConnectivity);

        // The glass layer sits right under the panel: the panel is owned by it, so Windows always
        // keeps the panel on top of it.
        _glass = new GlassBackdrop();
        _preview = new WindowPreview(_glass.Handle);
        new WindowInteropHelper(this).Owner = _glass.Handle;
        SourceInitialized += (_, _) => NativeWindow.HideFromAltTab(new WindowInteropHelper(this).Handle);
        // The panel's height follows its content; the glass follows the panel.
        SizeChanged += (_, _) => { if (IsVisible) ShowGlass(); };
        // Tapping outside the panel closes it (ignore the focus shuffle right after opening).
        Deactivated += (_, _) =>
        {
            if (IsVisible && DateTime.UtcNow - _shownAt > TimeSpan.FromMilliseconds(500)) HidePanel();
        };
        // The shadow around the card isn't fully clear, so Windows sends taps on it to this
        // window: those are outside the panel too.
        PreviewMouseDown += (_, _) => { if (!Card.IsMouseOver && Switcher?.IsMouseOver != true) HidePanel(); };
        PreviewTouchDown += (_, _) => { if (!Card.AreAnyTouchesOver && Switcher?.AreAnyTouchesOver != true) HidePanel(); };
        // The shadow is drawn only outside the see-through card.
        Card.SizeChanged += (_, _) => ClipShadow();
        PreviewKeyDown += OnPreviewKeyDown;
        // The footer says what A does on the selected control (PanelInput.Hint), else "Select".
        PreviewGotKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(ShowFooter);
        // The glass drop follows the selected control (focus passing through the window itself,
        // as during a page swap, leaves it where it is until the next control).
        _lens = new SelectionLens(LensLayer, PageScroller, Root);
        GotKeyboardFocus += (_, e) => FollowSelection(e.NewFocus);
        // The D-pad selection is the window's logical focus, which also moves while Windows keeps
        // another app active (no keyboard focus events then).
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(FocusManager.FocusedElementProperty, typeof(Window))
            .AddValueChanged(this, (_, _) => FollowSelection(FocusManager.GetFocusedElement(this)));
        // A list rebuilt under the selection (running apps, Wi-Fi, devices): select what's in the
        // same spot now. After a page swap the new page has already chosen (RestoreFocus runs first).
        _lens.Lost += spot => Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible || IsSelection(FocusManager.GetFocusedElement(this))) return;
            var view = ShownPage.View;
            var centre = LensLayer.TransformToVisual(view).Transform(new Point(spot.X + spot.Width / 2, spot.Y + spot.Height / 2));
            if (SpatialNavigator.Nearest(view, centre) is FrameworkElement near) FocusControl(near);
        }, DispatcherPriority.ContextIdle);
        // A new resolution (Graphics tab) moves the screen's edges: keep the panel in its corner
        // and its share of the screen (also while closed, so captures match).
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (Left < -5000) return;  // still parked off-screen (Prewarm)
            PlaceInCorner();
            if (!IsVisible) return;
            UpdateLayout();
            ShowGlass();
        });
    }

    IPage CurrentPage => _pages[_pageIndex];

    /// <summary>A control in the panel (not the window itself, not one removed from it).</summary>
    bool IsSelection(object? focus) => focus is FrameworkElement control && control != this && control.IsDescendantOf(Card);

    void FollowSelection(object? focus)
    {
        if (IsVisible && IsSelection(focus)) _lens.Follow((FrameworkElement)focus!);
    }

    /// <summary>A screen opened from a page (like Sound from the volume slider); B goes back.</summary>
    IPage? _subPage;

    IPage ShownPage => _subPage ?? CurrentPage;

    /// <summary>Space between the floating panel and the screen edges.</summary>
    const double ScreenGap = 12;

    /// <summary>Panel width in design units (ScreenFit): the same share of the screen at any
    /// resolution and Windows scaling.</summary>
    const double PanelWidth = 420;

    /// <summary>Physical pixels per design unit, set each time the panel is placed.</summary>
    double _unitPx = 1.5;

    /// <summary>Panel corner radius; inner tiles use smaller, matching curves.</summary>
    const double CornerRadiusDip = 26;

    readonly GlassBackdrop _glass;
    readonly SelectionLens _lens;

    static readonly Brush LowBatteryBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x5C));

    /// <summary>Lay out and render once off-screen at startup so the first real opening is instant.</summary>
    public void Prewarm()
    {
        ShowPage(_pageIndex);
        PlaceInCorner();
        Left = -10000;
        Top = 0;
        ShowActivated = false;
        Show();
        Hide();
        ShowActivated = true;
        CurrentPage.OnHidden();  // nothing listens while the panel is closed
        _ = _net.RefreshAsync();  // so the first opening already shows Wi-Fi and Bluetooth
        ShowFrontApp();
    }

    /// <summary>The app switcher left of the panel (shown with it; D-pad left from the page's left edge).</summary>
    internal AppSwitcher? Switcher
    {
        get => _switcher;
        init
        {
            _switcher = value;
            if (value == null) return;
            SwitcherHost.Child = value;
            value.Preview = ShowPreview;
            // Its glass follows it (also while it grows or shrinks), and so does the drop on it.
            value.GlassChanged += () =>
            {
                if (!IsVisible) return;
                ShowGlass();
                _lens.Nudge();
            };
            value.SelectionChanged += spot => { if (IsVisible && value.Active) _lens.Follow(spot); };
            // Back from the switcher: the drop comes back on the control that was selected.
            value.Exited += () =>
            {
                FollowSelection(FocusManager.GetFocusedElement(this));
                ShowFooter();
            };
            value.Done += HidePanel;
        }
    }

    readonly AppSwitcher? _switcher;

    public void Toggle()
    {
        if (_hiding) return;  // closing: the next press opens it
        if (IsVisible) HidePanel();
        else ShowPanel();
    }

    /// <summary>The Power menu (header chip, footer Y hint, Y button).</summary>
    public AppsPowerScreen? AppsMenu { get; set; }

    RunningApp? _frontApp;
    IntPtr _frontWindow;

    async Task DescribeFront(IntPtr window)
    {
        var app = await Task.Run(() => RunningApps.Describe(window));
        if (!IsVisible || window != _frontWindow) return;
        _frontApp = app;
        Switcher?.SetFront(app);
    }

    /// <summary>The chip is the Power menu's sign (the app in front is on the switcher's tile).</summary>
    void ShowFrontApp()
    {
        AppChipIcon.Visibility = AppChipName.Visibility = Visibility.Collapsed;
        AppChipGlyph.Text = "\uE7E8";
        AppChipGlyph.FontSize = 14;
        AppChipGlyph.Margin = new Thickness(0, 1, 0, 0);
    }

    /// <summary>The footer: what A does on the selected control (else "Select"), or in the app
    /// switcher its A, X (End) and B (Back).</summary>
    void ShowFooter()
    {
        bool switcher = Switcher is { Active: true };
        var focus = FocusManager.GetFocusedElement(this) as DependencyObject;
        AcceptHint.Text = switcher ? Switcher!.AcceptHint : (focus != null ? PanelInput.GetHint(focus) : null) ?? "Select";
        string? action = switcher ? "End" : focus != null && PanelInput.GetAction(focus) != null ? PanelInput.GetActionHint(focus) : null;
        ActionHint.Text = action ?? "";
        ActionKey.Visibility = ActionHint.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
        BackHint.Text = switcher ? "Back" : ShownPage.BackHint ?? (_subPage != null ? "Back" : "Close");
    }

    void AppChip_Click(object sender, RoutedEventArgs e) => OpenAppsMenu();

    /// <summary>Y / the chip: opens Apps &amp; power over whatever is shown (again: back out).</summary>
    void OpenAppsMenu()
    {
        if (AppsMenu == null) return;
        bool inMenu = _subPage == AppsMenu || _parentSubs.Any(s => s.Page == AppsMenu);
        CloseAllSubPages(restoreFocus: inMenu);
        if (!inMenu) OpenSubPage(AppsMenu);
    }

    /// <summary>When the MSI button was pressed (Stopwatch ticks), for the opening timing.</summary>
    public long PressedAt { get; set; }

    public void ShowPanel()
    {
        StopHiding();  // opened again while still closing
        var timing = System.Diagnostics.Stopwatch.StartNew();
        double queued = PressedAt != 0 ? System.Diagnostics.Stopwatch.GetElapsedTime(PressedAt).TotalMilliseconds : -1;
        PressedAt = 0;
        // Before the panel takes focus: the window in front (the switcher's tile). Saying whose it
        // is can take most of a second for a game (Windows asks the busy window), so the last
        // answer for the same window shows at once and a fresh one follows from the background.
        var frontWindow = RunningApps.FrontWindow();
        if (frontWindow != _frontWindow || _frontApp == null)
        {
            // Who it is right away (window calls only), so the app is on the tile from the first
            // frame instead of on the arc first and then jumping over.
            try { _frontApp = RunningApps.Recall(frontWindow); }
            catch (Exception ex) { _frontApp = null; Log.Error("Finding the app in front", ex); }
        }
        _frontWindow = frontWindow;
        _ = DescribeFront(frontWindow);
        double front = timing.Elapsed.TotalMilliseconds;
        PlaceInCorner();
        ShowPage(_pageIndex);
        UpdateHeader();
        _clock.Start();
        _net.StartWatching();
        _ = _net.RefreshAsync();
        _shownAt = DateTime.UtcNow;
        double page = timing.Elapsed.TotalMilliseconds;
        // Mini again before the first frame (it may have been full size when the panel closed).
        Switcher?.Open(_frontApp);
        double switcher = timing.Elapsed.TotalMilliseconds;
        Show();
        UpdateLayout();
        ShowGlass();
        double shown = timing.Elapsed.TotalMilliseconds;
        // Opening cost, to the first frame drawn after Show (detailed log).
        int frames = 0;
        EventHandler? firstFrame = null;
        firstFrame = (_, _) =>
        {
            if (++frames < 2) return;
            CompositionTarget.Rendering -= firstFrame;
            Log.Detail($"Open timing: queued {queued:0} ms, front app {front:0}, page {page - front:0}, switcher {switcher - page:0}, show {shown - switcher:0}, first frame at {timing.Elapsed.TotalMilliseconds:0} ms");
        };
        CompositionTarget.Rendering += firstFrame;
        if (!NativeWindow.BringToFront(new WindowInteropHelper(this).Handle))
            Log.Info("Panel shown but Windows kept another app in the foreground");
        Activate();
        RestoreFocus(opening: true);
        ShowFooter();
        if (SaveSnapshots)
            Dispatcher.BeginInvoke(SaveSnapshot, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Floats in the top-right corner, as tall as its content (scrolls if that's taller). Worked
    /// out in the screen's real pixels and scaled to a fixed share of the screen, so a new
    /// resolution or Windows scaling changes neither where it sits nor how big it looks.
    /// </summary>
    void PlaceInCorner()
    {
        var screen = ScreenFit.Primary();
        // The panel's own size (Edit on the Quick tab) scales everything in it alike; the gap to
        // the screen's edges stays.
        double size = Math.Clamp(_settings.PanelSize, MinPanelSize, MaxPanelSize) / 100.0;
        bool left = _settings.PanelSide == "Left";
        _unitPx = screen.UnitPx * size;
        double scale = ScreenFit.Scale(this, screen) * size;
        // A new transform (even the same) makes the whole panel lay itself out again.
        if (Root.LayoutTransform is not ScaleTransform current || Math.Abs(current.ScaleX - scale) > 1e-9)
        {
            var zoom = new ScaleTransform(scale, scale);
            zoom.Freeze();
            Root.LayoutTransform = zoom;
        }
        Card.BorderThickness = new Thickness(1 / _unitPx);  // the rim stays one device pixel
        // The window also holds the card's shadow: it reaches past the card (and past the
        // screen's edges, where the shadow is simply cut off) by the card's margin.
        // Left of that, the app switcher, its tile level with the card's first row.
        var room = Card.Margin;
        double gap = ScreenGap * screen.UnitPx, width = Math.Round(PanelWidth * _unitPx);
        double switcher = Switcher != null ? Math.Round((AppSwitcher.SwitcherWidth - SwitcherOverlap) * _unitPx) : 0;
        Width = ScreenFit.ToWindowUnits(this, width + Math.Round((room.Left + room.Right) * _unitPx) + switcher);
        MaxHeight = ScreenFit.ToWindowUnits(this, screen.Work.Height - 2 * gap + (room.Top + room.Bottom) * _unitPx);
        Top = ScreenFit.ToWindowUnits(this, screen.Work.Y + gap - room.Top * _unitPx);
        Left = ScreenFit.ToWindowUnits(this, left
            ? screen.Work.X + gap - Math.Round(room.Left * _unitPx)
            : screen.Work.X + screen.Work.Width - width - gap - Math.Round(room.Left * _unitPx) - switcher);
        // On the left the card comes first and the switcher sits right of it, mirrored.
        Root.ColumnDefinitions[0].Width = left ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Root.ColumnDefinitions[1].Width = left ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(Frame, left ? 0 : 1);
        Grid.SetColumn(SwitcherHost, left ? 1 : 0);
        double top = room.Top + FirstRow - AppSwitcher.TileCentre;
        SwitcherHost.Margin = left ? new Thickness(-SwitcherOverlap, top, 0, 0) : new Thickness(0, top, -SwitcherOverlap, 0);
        if (Switcher != null)
        {
            Switcher.Mirrored = left;
            Switcher.SetUnit(_unitPx);
            Switcher.SetAnchor(left ? SwitcherOverlap - room.Right : AppSwitcher.SwitcherWidth - SwitcherOverlap + room.Left);
        }
    }

    /// <summary>The panel's size range in percent (Edit on the Quick tab).</summary>
    public const int MinPanelSize = 80, MaxPanelSize = 130;

    /// <summary>The panel's size or side changed (Edit on the Quick tab): placed again at once.</summary>
    public void ApplyPlacement()
    {
        if (Left < -5000 && !IsVisible) return;  // parked off-screen; placed when it opens
        PlaceInCorner();
        if (!IsVisible) return;
        UpdateLayout();
        ClipShadow();
        ShowGlass();
        _lens.Nudge();
    }

    /// <summary>How far below the card's top its first row of controls sits (the Quick tab's
    /// mode circle and Wi-Fi / Bluetooth tiles): the app switcher's tile lines up with it.</summary>
    const double FirstRow = 197;

    /// <summary>How far the app switcher reaches into the card's shadow room, so its arc sits close
    /// to the card.</summary>
    const double SwitcherOverlap = 40;

    /// <summary>The glass under the card and the app switcher's pieces, in one glass window right
    /// under this one (the same size and place).</summary>
    void ShowGlass()
    {
        if (_hiding) return;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return;
        var scale = target.TransformToDevice;
        int width = (int)Math.Round(ActualWidth * scale.M11), height = (int)Math.Round(ActualHeight * scale.M22);
        if (width <= 0 || height <= 0) return;
        var area = new Int32Rect((int)Math.Round(Left * scale.M11), (int)Math.Round(Top * scale.M22), width, height);
        _glass.Show(area, GlassLayers());
    }

    /// <summary>The card's glass (in pixels) and the app switcher's pieces (in its own units,
    /// moved and grown by one transform).</summary>
    GlassBackdrop.Layer[] GlassLayers()
    {
        var card = new GlassBackdrop.Layer([new(ToGlassPixels(Card, new Rect(Card.RenderSize)), CornerRadiusDip * _unitPx)], System.Numerics.Matrix3x2.Identity);
        if (Switcher == null) return [card];
        var pieces = Switcher.GlassPieces().Select(p => new GlassBackdrop.GlassShape(p.Area, p.Radius, p.Opacity)).ToList();
        return [card, new(pieces, ToGlassMatrix(Switcher))];
    }

    /// <summary>From an element's own coordinates to physical pixels from this window's top-left.</summary>
    System.Numerics.Matrix3x2 ToGlassMatrix(Visual element)
    {
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var m = element.TransformToAncestor(this) is Transform transform ? transform.Value : Matrix.Identity;
        return new System.Numerics.Matrix3x2(
            (float)(m.M11 * scale.M11), (float)(m.M12 * scale.M22),
            (float)(m.M21 * scale.M11), (float)(m.M22 * scale.M22),
            (float)(m.OffsetX * scale.M11), (float)(m.OffsetY * scale.M22));
    }

    /// <summary>An element's area in physical pixels from this window's (and the glass window's) top-left.</summary>
    Rect ToGlassPixels(Visual element, Rect local)
    {
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var area = element.TransformToAncestor(this).TransformBounds(local);
        return new Rect(area.X * scale.M11, area.Y * scale.M22, area.Width * scale.M11, area.Height * scale.M22);
    }

    readonly WindowPreview _preview;

    /// <summary>The app switcher's live preview: a window's picture drawn in the glass window
    /// under the switcher's frame.</summary>
    bool ShowPreview(IntPtr window, Rect box, double opacity)
    {
        if (window == IntPtr.Zero || !IsVisible || Switcher == null)
        {
            _preview.Clear();
            return false;
        }
        var area = ToGlassPixels(Switcher, box);
        return _preview.Show(window, new Int32Rect((int)Math.Round(area.X), (int)Math.Round(area.Y), (int)Math.Round(area.Width), (int)Math.Round(area.Height)),
            (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255));
    }

    void ClipShadow()
    {
        var card = new Rect(Card.TranslatePoint(new Point(0, 0), ShadowLayer), Card.RenderSize);
        var outside = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(-1000, -1000, ShadowLayer.ActualWidth + 2000, ShadowLayer.ActualHeight + 2000)),
            new RectangleGeometry(card, CornerRadiusDip, CornerRadiusDip));
        outside.Freeze();
        ShadowLayer.Clip = outside;
    }

    /// <summary>Windows scaling changed: redo the sizes that cancel it.</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (Left < -5000) return;  // still parked off-screen (Prewarm)
        PlaceInCorner();
        if (IsVisible) ShowGlass();
    }

    public void HidePanel()
    {
        if (!IsVisible || _hiding) return;
        _clock.Stop();
        _net.StopWatching();
        _glass.Hide();
        _lens.Hide();
        Switcher?.Dismiss();
        // The glass opens next time with what it has now: leave it the closed look (mini
        // switcher), not the look it had when the panel closed.
        _glass.Update(GlassLayers());
        HideAfterDrawing();
        if (_subPage != null)
        {
            _subPage.OnHidden();
            _subPage = null;
        }
        _parentSubs.Clear();
        CurrentPage.OnHidden();
        _settings.LastPage = CurrentPage.Id;
        _settings.SaveSoon();
    }

    /// <summary>Closes the panel and waits until it's off the screen (before a screenshot or a
    /// recording starts, so it isn't in it).</summary>
    public async Task HideAndWaitAsync()
    {
        HidePanel();
        for (int i = 0; i < 40 && (IsVisible || _hiding); i++) await Task.Delay(15);
        await Task.Delay(80);  // the compositor's next frame no longer has it
    }

    /// <summary>The app that was in front when the panel opened (captures are named after it).</summary>
    public string? FrontAppName => _frontApp?.Name;

    /// <summary>While Claw Control records, the panel and its glass stay out of the picture
    /// (they can be opened to stop it). Other recorders still see them.</summary>
    public void ExcludeFromCapture(bool exclude)
    {
        const uint None = 0, ExcludeFromCapture = 0x11;
        foreach (var window in new[] { new WindowInteropHelper(this).Handle, _glass.Handle })
            if (window != IntPtr.Zero) SetWindowDisplayAffinity(window, exclude ? ExcludeFromCapture : None);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    bool _hiding;
    EventHandler? _hideWhenDrawn;

    /// <summary>A see-through window shows its last picture again for a moment the next time it's
    /// shown, so it moves off the screen at once, draws its reset look there (mini app switcher,
    /// no drop) and only then hides.</summary>
    void HideAfterDrawing()
    {
        _hiding = true;
        Left = -10000;
        int frames = 0;
        _hideWhenDrawn = (_, _) =>
        {
            if (++frames < 3) return;
            StopHiding();
            Hide();
        };
        CompositionTarget.Rendering += _hideWhenDrawn;
    }

    void StopHiding()
    {
        if (_hideWhenDrawn != null) CompositionTarget.Rendering -= _hideWhenDrawn;
        _hideWhenDrawn = null;
        _hiding = false;
    }

    /// <summary>Screens under the shown one (a screen opened from a screen) and what was selected there.</summary>
    readonly Stack<(IPage Page, FrameworkElement? Focus)> _parentSubs = new();

    /// <summary>Shows a screen over the current page or screen (tabs become "‹ Title"); B goes back
    /// one level, LB / RB leave them all.</summary>
    public void OpenSubPage(IPage page)
    {
        if (!IsVisible || _subPage == page) return;
        Log.Detail($"Opening {page.Title}");
        if (_subPage != null)
        {
            _parentSubs.Push((_subPage, FocusManager.GetFocusedElement(this) as FrameworkElement));
            _subPage.OnHidden();
        }
        _subPage = page;
        PageHost.Content = page.View;
        PageScroller.ScrollToTop();
        BuildTabs();
        page.OnShown();
        RestoreFocus();
    }

    /// <summary>Back one level: to the screen underneath (on the row that opened this one) or the page.</summary>
    void CloseSubPage(bool restoreFocus = true)
    {
        if (_subPage == null) return;
        _subPage.OnHidden();
        if (_parentSubs.TryPop(out var parent))
        {
            _subPage = parent.Page;
            PageHost.Content = parent.Page.View;
            BuildTabs();
            parent.Page.OnShown();
            if (!restoreFocus) return;
            if (parent.Focus is { } back) Dispatcher.BeginInvoke(() => { if (back.IsVisible) FocusControl(back); else RestoreFocus(); }, DispatcherPriority.Loaded);
            else RestoreFocus();
            return;
        }
        _subPage = null;
        PageHost.Content = CurrentPage.View;
        BuildTabs();
        CurrentPage.OnShown();  // picks up what changed there (e.g. volume)
        if (restoreFocus) RestoreFocus();
    }

    /// <summary>Back one screen, as B does (e.g. after picking a folder).</summary>
    public void Back()
    {
        if (IsVisible && _subPage != null) CloseSubPage();
        ShowFooter();
    }

    /// <summary>Leaves every screen, back to the page.</summary>
    void CloseAllSubPages(bool restoreFocus = true)
    {
        _parentSubs.Clear();  // the ones underneath were hidden when covered
        CloseSubPage(restoreFocus);
    }

    public void Handle(NavCommand command, InputSource source)
    {
        if (!IsVisible || _hiding) return;
        // In MSI's Desktop mode one press can arrive both as a gamepad button and as a key.
        var now = DateTime.UtcNow;
        bool duplicate = command == _lastCommand && source != _lastSource && now - _lastCommandAt < DuplicateWindow;
        (_lastCommand, _lastSource, _lastCommandAt) = (command, source, now);
        var focused = FocusManager.GetFocusedElement(this) as FrameworkElement;
        Log.Detail($"Command {command} from {source} on {Describe(focused)}{(duplicate ? " (same press seen twice, ignored)" : "")}");
        if (duplicate) return;
        // The app switcher has the D-pad until B / right from its tile (or LB / RB / Y, which the
        // panel then handles as usual).
        if (Switcher is { Active: true } && Switcher.Handle(command))
        {
            if (command == NavCommand.Accept && Switcher.SelectedView is { } pressed) _lens.Pulse(pressed);
            ShowFooter();
            return;
        }

        bool onTabs = focused != null && Tabs.Children.Contains(focused);
        bool onChip = focused == AppChip;
        switch (command)
        {
            case NavCommand.Menu:
                OpenAppsMenu();
                break;
            // The header chip sits above the tab row.
            case NavCommand.Down when onChip:
                FocusTabs();
                break;
            case NavCommand.Up or NavCommand.Left or NavCommand.Right when onChip:
                break;
            // On the tab row: left/right switch pages right away, down goes into the page.
            case NavCommand.Left or NavCommand.Right when onTabs:
                if (_subPage == null)
                    SwitchPage((_pageIndex + (command == NavCommand.Right ? 1 : _pages.Count - 1)) % _pages.Count,
                        $"{source} on tabs", focusTabs: true);
                break;
            case NavCommand.Down when onTabs:
                if (SpatialNavigator.First(ShownPage.View) is FrameworkElement first) FocusControl(first);
                break;
            case NavCommand.Up when onTabs:
                AppChip.Focus();
                FocusManager.SetFocusedElement(this, AppChip);
                break;
            case NavCommand.Left or NavCommand.Right when focused is FillSlider bar:
                bar.Nudge(command == NavCommand.Right ? 1 : -1);
                break;
            case NavCommand.Left or NavCommand.Right when focused is Slider slider:
                slider.Value = Math.Clamp(
                    slider.Value + (command == NavCommand.Right ? slider.SmallChange : -slider.SmallChange),
                    slider.Minimum, slider.Maximum);
                break;
            case NavCommand.Up or NavCommand.Down or NavCommand.Left or NavCommand.Right:
                if (focused != null && PanelInput.GetSteer(focused)?.Invoke(command) is FrameworkElement steered)
                    FocusControl(steered);  // e.g. the next page of a paged row
                else if (SpatialNavigator.Next(focused, ShownPage.View, command) is FrameworkElement next)
                    FocusControl(next);
                else if (command == NavCommand.Up)
                    FocusTabs();  // nothing above in the page: up to the tab row
                else if (command == (Switcher?.Mirrored == true ? NavCommand.Right : NavCommand.Left) && Switcher?.Enter() == true)
                {
                    if (Switcher.SelectedView is { } spot) _lens.Follow(spot);  // the drop flies over
                    ShowFooter();
                }
                break;
            case NavCommand.Accept:
                if (focused != null) _lens.Pulse(focused);
                if (focused != null && PanelInput.GetAccept(focused) is Action accept) accept();
                else Invoke(focused);
                ShowFooter();  // what A / X do may have changed (a tile picked up)
                break;
            // X does something only where a control says so (never by accident on a page).
            case NavCommand.Action:
                if (focused != null && PanelInput.GetAction(focused) is Action action)
                {
                    _lens.Pulse(focused);
                    action();
                    ShowFooter();
                }
                break;
            case NavCommand.Back:
                if (ShownPage.HandleBack())
                {
                    ShowFooter();
                    break;
                }
                if (_subPage != null) CloseSubPage();
                else HidePanel();
                break;
            case NavCommand.PrevPage:
                SwitchPage((_pageIndex + _pages.Count - 1) % _pages.Count, source.ToString());
                break;
            case NavCommand.NextPage:
                SwitchPage((_pageIndex + 1) % _pages.Count, source.ToString());
                break;
        }
    }

    /// <summary>Jumps to a page by id (e.g. the Quick tab's shortcut to the Controller tab).</summary>
    public void ShowPage(string id)
    {
        int index = _pages.ToList().FindIndex(p => p.Id == id);
        if (index >= 0 && IsVisible) SwitchPage(index, "shortcut");
    }

    void SwitchPage(int index, string how, bool focusTabs = false)
    {
        Log.Detail($"Page {_pages[_pageIndex].Title} -> {_pages[index].Title} ({how})");
        CloseAllSubPages(restoreFocus: false);
        CurrentPage.OnHidden();
        ShowPage(index);
        if (focusTabs) FocusTabs();
        else RestoreFocus();
    }

    /// <summary>Selects the current tab (or "‹ Sound" on a sub screen) in the tab row.</summary>
    void FocusTabs()
    {
        var tab = Tabs.Children.OfType<Button>().FirstOrDefault(b => Equals(b.Tag, "selected"));
        if (tab == null) return;
        PageScroller.ScrollToTop();
        tab.Focus();
        FocusManager.SetFocusedElement(this, tab);
    }

    void ShowPage(int index)
    {
        _pageIndex = index;
        PageHost.Content = CurrentPage.View;
        PageScroller.ScrollToTop();
        BuildTabs();
        CurrentPage.OnShown();
    }

    /// <summary>What the tab row was built for (a screen, or the page tabs with one selected).</summary>
    (IPage? Sub, int Page) _tabsFor = (null, -1);

    void BuildTabs()
    {
        // Opening the panel shows the same page again: the row it has is still right.
        var tabsFor = (_subPage, _subPage == null ? _pageIndex : -1);
        if (Tabs.Children.Count > 0 && tabsFor == _tabsFor) return;
        _tabsFor = tabsFor;
        Tabs.Children.Clear();
        if (_subPage != null)
        {
            var back = new Button
            {
                Content = $"‹  {_subPage.Title}",
                Style = (Style)FindResource("TabButton"),
                Tag = "selected",
            };
            back.Click += (_, _) => CloseSubPage();
            Tabs.Children.Add(back);
        }
        else for (int i = 0; i < _pages.Count; i++)
        {
            int target = i;
            var tab = new Button
            {
                Content = _pages[i].Title,
                Style = (Style)FindResource("TabButton"),
                Tag = i == _pageIndex ? "selected" : null,
            };
            tab.Click += (_, _) => { if (target != _pageIndex) SwitchPage(target, "tap"); };
            Tabs.Children.Add(tab);
        }
        Dispatcher.BeginInvoke(() => MoveTabPill(animate: IsVisible), DispatcherPriority.Loaded);
    }

    /// <summary>Slides the tab pill under the selected tab (or stretches it under "‹ Title").</summary>
    void MoveTabPill(bool animate)
    {
        var tab = Tabs.Children.OfType<Button>().FirstOrDefault(b => Equals(b.Tag, "selected"));
        if (tab == null || tab.ActualWidth <= 0) return;
        double x = tab.TranslatePoint(new Point(0, 0), TabRow).X, width = tab.ActualWidth;
        if (!animate)
        {
            TabPillMove.BeginAnimation(TranslateTransform.XProperty, null);
            TabPill.BeginAnimation(WidthProperty, null);
            TabPillMove.X = x;
            TabPill.Width = width;
            return;
        }
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        var time = TimeSpan.FromMilliseconds(420);
        TabPillMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, time) { EasingFunction = ease });
        TabPill.BeginAnimation(WidthProperty, new DoubleAnimation(width, time) { EasingFunction = ease });
    }

    void RestoreFocus(bool opening = false)
    {
        // The page may have just been swapped in; wait for layout so positions are known.
        Dispatcher.BeginInvoke(() =>
        {
            var page = ShownPage;
            FrameworkElement? target = null;
            if (_subPage != null) { }  // sub screens start at their first control
            else if (opening && page.DefaultFocus is { IsVisible: true } preferred) target = preferred;
            else if (!_lastFocus.TryGetValue(page, out target)
                && _settings.LastFocusByPage.TryGetValue(page.Id, out var name))
                target = page.View.FindName(name) as FrameworkElement;
            // The remembered control may be hidden now (sliders outside Custom, overlay options).
            if (target is { IsVisible: false }) target = null;
            target ??= SpatialNavigator.First(page.View);
            if (opening) _lens.Hide();  // appears right on it, not flying in from last time's spot
            if (target != null)
            {
                FocusControl(target);
                FollowSelection(target);  // already focused: no focus event to move the drop
            }
        }, DispatcherPriority.Loaded);
    }

    void FocusControl(FrameworkElement element)
    {
        element.Focus();
        FocusManager.SetFocusedElement(this, element);
        element.BringIntoView();
        if (_subPage != null) return;  // only pages remember their spot
        _lastFocus[CurrentPage] = element;
        if (!string.IsNullOrEmpty(element.Name))
            _settings.LastFocusByPage[CurrentPage.Id] = element.Name;
    }

    /// <summary>Detailed log: which control a command acted on ("FillSlider Brightness").</summary>
    static string Describe(FrameworkElement? element) => element switch
    {
        null => "nothing",
        FillSlider slider => $"FillSlider {slider.Label ?? slider.Name}",
        ContentControl { Content: string text } => $"{element.GetType().Name} {text}",
        _ => $"{element.GetType().Name} {element.Name}".TrimEnd(),
    };

    static void Invoke(FrameworkElement? element)
    {
        // A disabled control (e.g. "next track" with nothing next) throws instead of doing nothing.
        if (element is not { IsEnabled: true }) return;
        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
        if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
        else if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        NavCommand? command = e.Key switch
        {
            Key.Up => NavCommand.Up,
            Key.Down => NavCommand.Down,
            Key.Left => NavCommand.Left,
            Key.Right => NavCommand.Right,
            Key.Enter or Key.Space => NavCommand.Accept,
            Key.Escape => NavCommand.Back,
            Key.PageUp or Key.Q => NavCommand.PrevPage,
            Key.PageDown or Key.E => NavCommand.NextPage,
            Key.X => NavCommand.Action,
            _ => null,
        };
        if (command is NavCommand c)
        {
            Handle(c, InputSource.Keyboard);
            e.Handled = true;
        }
        else
        {
            // Helps map what the controller sends in MSI's Desktop mode (it acts as a keyboard there).
            Log.Detail($"Unmapped key in panel: {e.Key} (system key {e.SystemKey}, modifiers {Keyboard.Modifiers})");
        }
    }

    /// <summary>Bluetooth and Wi-Fi: bright = connected (with its name), faint = on, crossed out = off.</summary>
    void ShowConnectivity()
    {
        var net = _net.Last;
        ShowLink(BluetoothPanel, BluetoothIcon, BluetoothSlash, BluetoothName, net.Bluetooth, net.Device);
        ShowLink(WifiPanel, WifiBase, WifiSlash, WifiName, net.WiFi, net.Network);
        // Connected: the signal bars light up over a faint full icon.
        bool connected = net.WiFi == LinkState.Connected;
        WifiBars.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        WifiBars.Text = net.Bars switch { <= 1 => "", 2 => "", 3 => "", _ => "" };
        if (connected) WifiBase.Opacity = 0.35;
    }

    static void ShowLink(FrameworkElement panel, TextBlock icon, FrameworkElement slash, TextBlock name, LinkState state, string? label)
    {
        panel.Visibility = state == LinkState.Unknown ? Visibility.Collapsed : Visibility.Visible;
        icon.Opacity = state == LinkState.Connected ? 1 : 0.35;
        slash.Visibility = state == LinkState.Off ? Visibility.Visible : Visibility.Collapsed;
        name.Text = label ?? "";
        name.Visibility = state == LinkState.Connected && !string.IsNullOrEmpty(label) ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateHeader()
    {
        ShowConnectivity();
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        if (Battery.Read() is not var (percent, pluggedIn))
        {
            BatteryPanel.Visibility = Visibility.Collapsed;
            return;
        }
        BatteryPanel.Visibility = Visibility.Visible;
        BatteryText.Text = $"{percent}%";
        ChargingBolt.Visibility = pluggedIn ? Visibility.Visible : Visibility.Collapsed;
        // Inner width of the outline: 28 minus border and padding on both sides.
        BatteryFill.Width = Math.Max(1, 22 * percent / 100.0);
        BatteryFill.Background = percent <= 20 && !pluggedIn
            ? LowBatteryBrush
            : (Brush)FindResource("TextBrush");
    }

    /// <summary>Debug aid (--snapshots): saves what the panel looks like to snapshot.png.</summary>
    void SaveSnapshot()
    {
        try
        {
            SaveImage(Path.Combine(Log.Dir, "snapshot.png"), background: null);
        }
        catch (Exception ex)
        {
            Log.Error("Saving snapshot", ex);
        }
    }

    /// <summary>
    /// Debug aid (capture signal, see App): renders every page, and every screen it opens, to PNGs
    /// in <paramref name="dir"/> while the panel stays closed. Pages are shown whole, not scrolled.
    /// </summary>
    public void CaptureAll(string dir)
    {
        if (IsVisible)
        {
            Log.Info("Capture skipped: the panel is open");
            return;
        }
        int pageIndex = _pageIndex;
        double maxHeight = MaxHeight;
        int count = 0;
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir, "*.png")) File.Delete(old);
            MaxHeight = double.PositiveInfinity;
            UpdateHeader();
            var background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x20));
            for (int i = 0; i < _pages.Count; i++)
            {
                ShowPage(i);
                ShowLensOn(CurrentPage.DefaultFocus, CurrentPage);
                SaveImage(Path.Combine(dir, $"{++count:00} {CurrentPage.Title}.png"), background);
                // Its screens, and the screens they open (one level more).
                foreach (var screen in CurrentPage.Screens.SelectMany(s => s.Screens.Prepend(s)))
                {
                    _subPage = screen;
                    PageHost.Content = screen.View;
                    BuildTabs();
                    screen.OnShown();
                    ShowLensOn(null, screen);
                    SaveImage(Path.Combine(dir, $"{++count:00} {CurrentPage.Title} - {screen.Title}.png"), background);
                    screen.OnHidden();
                    _subPage = null;
                }
                PageHost.Content = CurrentPage.View;
                BuildTabs();
                // The Quick tab while it's being edited.
                if (CurrentPage is Features.Performance.PerformancePage quick)
                {
                    quick.ShowEditingForCapture(true);
                    ShowLensOn(null, quick);
                    SaveImage(Path.Combine(dir, $"{++count:00} {quick.Title} - Edit.png"), background);
                    quick.ShowEditingForCapture(false);
                }
                CurrentPage.OnHidden();
            }
            // The whole panel with the app switcher grown: on the right, on the left, and bigger.
            if (Switcher != null)
            {
                var apps = RunningApps.List();
                var front = apps.FirstOrDefault();
                var game = new SolidColorBrush(Color.FromRgb(0x5A, 0x6E, 0x8A));
                (string Side, int Size) placed = (_settings.PanelSide, _settings.PanelSize);
                try
                {
                    foreach (var (side, size) in new[] { ("Right", 100), ("Left", 100), ("Right", 130) })
                    {
                        (_settings.PanelSide, _settings.PanelSize) = (side, size);
                        ShowPage(0);
                        PlaceInCorner();
                        Switcher.ShowForCapture(front, apps);
                        UpdateLayout();
                        if (Switcher.SelectedView is { } spot) _lens.ShowNow(spot);
                        SaveWindow(Path.Combine(dir, $"{++count:00} Panel - {side} {size}%.png"), game);
                        _lens.Hide();
                        Switcher.Dismiss();
                        CurrentPage.OnHidden();
                    }
                }
                finally
                {
                    (_settings.PanelSide, _settings.PanelSize) = placed;
                    PlaceInCorner();
                    Left = -10000;  // closed: parked off-screen as usual
                }
            }
            Log.Info($"Captured {count} screens to {dir}");
        }
        catch (Exception ex)
        {
            Log.Error("Capturing screens", ex);
            if (_subPage != null) { _subPage.OnHidden(); _subPage = null; }
        }
        finally
        {
            _lens.Hide();
            MaxHeight = maxHeight;
            ShowPage(pageIndex);
            CurrentPage.OnHidden();  // nothing listens while the panel is closed
        }
    }

    /// <summary>Captures: the glass drop on the control that would be selected first.</summary>
    void ShowLensOn(FrameworkElement? preferred, IPage page)
    {
        UpdateLayout();
        // The panel is hidden during captures, so IsVisible is false everywhere: check Visibility.
        if ((preferred is { Visibility: Visibility.Visible } ? preferred : FirstShown(page.View)) is FrameworkElement control)
            _lens.ShowNow(control);
        else
            _lens.Hide();
    }

    /// <summary>The first control in the page that isn't collapsed (SpatialNavigator.First, minus IsVisible).</summary>
    static FrameworkElement? FirstShown(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (VisualTreeHelper.GetChild(parent, i) is not UIElement { Visibility: Visibility.Visible } child) continue;
            if (child is FrameworkElement { IsEnabled: true, Focusable: true } control and (ButtonBase or Slider)) return control;
            if (FirstShown(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Renders the whole panel window (app switcher, card and shadow) over a background.</summary>
    void SaveWindow(string path, Brush background)
    {
        UpdateLayout();
        MoveTabPill(animate: false);
        UpdateLayout();
        var root = (Visual)VisualTreeHelper.GetChild(this, 0);
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)(ActualWidth * dpi.DpiScaleX), height = (int)(ActualHeight * dpi.DpiScaleY);
        var panel = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        panel.Render(root);
        var layers = new DrawingVisual();
        using (var dc = layers.RenderOpen())
        {
            var area = new Rect(0, 0, width / dpi.DpiScaleX, height / dpi.DpiScaleY);
            dc.DrawRectangle(background, null, area);
            dc.DrawImage(panel, area);
        }
        var flat = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        flat.Render(layers);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(flat));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>Renders the panel's card (without the shadow around it) as laid out now; a
    /// background fills in for the glass when given.</summary>
    void SaveImage(string path, Brush? background)
    {
        UpdateLayout();
        MoveTabPill(animate: false);
        UpdateLayout();  // the pill's new width
        var root = (Visual)VisualTreeHelper.GetChild(this, 0);
        var dpi = VisualTreeHelper.GetDpi(this);
        var whole = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        whole.Render(root);
        var cardArea = Card.TransformToAncestor(this).TransformBounds(new Rect(Card.RenderSize));
        int left = (int)Math.Round(cardArea.X * dpi.DpiScaleX), top = (int)Math.Round(cardArea.Y * dpi.DpiScaleY);
        int width = Math.Min(whole.PixelWidth - left, (int)Math.Round(cardArea.Width * dpi.DpiScaleX));
        int height = Math.Min(whole.PixelHeight - top, (int)Math.Round(cardArea.Height * dpi.DpiScaleY));
        var bitmap = new CroppedBitmap(whole, new Int32Rect(left, top, width, height));
        BitmapSource image = bitmap;
        if (background != null)
        {
            var layers = new DrawingVisual();
            using (var dc = layers.RenderOpen())
            {
                var area = new Rect(0, 0, width / dpi.DpiScaleX, height / dpi.DpiScaleY);
                dc.DrawRectangle(background, null, area);
                dc.DrawImage(bitmap, area);
            }
            var flat = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            flat.Render(layers);
            image = flat;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
