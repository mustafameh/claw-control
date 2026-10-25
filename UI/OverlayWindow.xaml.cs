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
        _glass = new GlassBackdrop(CornerRadiusDip);
        new WindowInteropHelper(this).Owner = _glass.Handle;
        SourceInitialized += (_, _) => NativeWindow.HideFromAltTab(new WindowInteropHelper(this).Handle);
        // The panel's height follows its content; the glass follows the panel.
        SizeChanged += (_, _) => { if (IsVisible) _glass.ShowAt(this, _unitPx); };
        // Tapping outside the panel closes it (ignore the focus shuffle right after opening).
        Deactivated += (_, _) =>
        {
            if (IsVisible && DateTime.UtcNow - _shownAt > TimeSpan.FromMilliseconds(500)) HidePanel();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        // The footer says what A does on the selected control (PanelInput.Hint), else "Select".
        PreviewGotKeyboardFocus += (_, e) =>
            AcceptHint.Text = (e.NewFocus is DependencyObject focus ? PanelInput.GetHint(focus) : null) ?? "Select";
        // The glass drop follows the selected control (focus passing through the window itself,
        // as during a page swap, leaves it where it is until the next control).
        _lens = new SelectionLens(LensLayer, PageScroller);
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
            _glass.ShowAt(this, _unitPx);
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
        ShowFrontApp();  // the chip's power sign until an opening knows the app in front
    }

    public void Toggle()
    {
        if (IsVisible) HidePanel();
        else ShowPanel();
    }

    /// <summary>The Apps &amp; power menu (header chip, footer Y hint, Y button).</summary>
    public AppsPowerScreen? AppsMenu { get; set; }

    RunningApp? _frontApp;

    /// <summary>The chip shows the app that was in front, or a power sign when there was none.</summary>
    void ShowFrontApp()
    {
        AppChipIcon.Source = _frontApp?.Icon;
        AppChipIcon.Visibility = _frontApp?.Icon != null ? Visibility.Visible : Visibility.Collapsed;
        AppChipName.Text = _frontApp?.Name ?? "";
        AppChipName.Visibility = _frontApp != null ? Visibility.Visible : Visibility.Collapsed;
        AppChipGlyph.Text = _frontApp != null ? "" : "";  // ▾ or power
        AppChipGlyph.FontSize = _frontApp != null ? 10 : 14;
        AppChipGlyph.Margin = new Thickness(_frontApp != null ? 5 : 0, 1, 0, 0);
        if (AppsMenu != null) AppsMenu.Front = _frontApp;
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

    public void ShowPanel()
    {
        // Before the panel takes focus: which app the chip and End are about.
        _frontApp = RunningApps.Front();
        ShowFrontApp();
        PlaceInCorner();
        ShowPage(_pageIndex);
        UpdateHeader();
        _clock.Start();
        _net.StartWatching();
        _ = _net.RefreshAsync();
        _shownAt = DateTime.UtcNow;
        Show();
        UpdateLayout();
        _glass.ShowAt(this, _unitPx);
        if (!NativeWindow.BringToFront(new WindowInteropHelper(this).Handle))
            Log.Info("Panel shown but Windows kept another app in the foreground");
        Activate();
        RestoreFocus(opening: true);
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
        _unitPx = screen.UnitPx;
        double scale = ScreenFit.Scale(this, screen);
        var zoom = new ScaleTransform(scale, scale);
        zoom.Freeze();
        Card.LayoutTransform = zoom;
        Card.BorderThickness = new Thickness(1 / _unitPx);  // the rim stays one device pixel
        double gap = ScreenGap * _unitPx, width = Math.Round(PanelWidth * _unitPx);
        Width = ScreenFit.ToWindowUnits(this, width);
        MaxHeight = ScreenFit.ToWindowUnits(this, screen.Work.Height - 2 * gap);
        Top = ScreenFit.ToWindowUnits(this, screen.Work.Y + gap);
        Left = ScreenFit.ToWindowUnits(this, screen.Work.X + screen.Work.Width - width - gap);
    }

    /// <summary>Windows scaling changed: redo the sizes that cancel it.</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (Left < -5000) return;  // still parked off-screen (Prewarm)
        PlaceInCorner();
        if (IsVisible) _glass.ShowAt(this, _unitPx);
    }

    public void HidePanel()
    {
        if (!IsVisible) return;
        _clock.Stop();
        _net.StopWatching();
        Hide();
        _glass.Hide();
        _lens.Hide();
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

    /// <summary>Leaves every screen, back to the page.</summary>
    void CloseAllSubPages(bool restoreFocus = true)
    {
        _parentSubs.Clear();  // the ones underneath were hidden when covered
        CloseSubPage(restoreFocus);
    }

    public void Handle(NavCommand command, InputSource source)
    {
        if (!IsVisible) return;
        // In MSI's Desktop mode one press can arrive both as a gamepad button and as a key.
        var now = DateTime.UtcNow;
        bool duplicate = command == _lastCommand && source != _lastSource && now - _lastCommandAt < DuplicateWindow;
        (_lastCommand, _lastSource, _lastCommandAt) = (command, source, now);
        Log.Detail($"Command {command} from {source}{(duplicate ? " (same press seen twice, ignored)" : "")}");
        if (duplicate) return;

        var focused = FocusManager.GetFocusedElement(this) as FrameworkElement;
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
                break;
            case NavCommand.Accept:
                if (focused != null) _lens.Pulse(focused);
                if (focused != null && PanelInput.GetAccept(focused) is Action accept) accept();
                else Invoke(focused);
                break;
            case NavCommand.Back:
                if (ShownPage.HandleBack()) break;
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

    void BuildTabs()
    {
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

    static void Invoke(FrameworkElement? element)
    {
        if (element == null) return;
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
                foreach (var screen in CurrentPage.Screens)
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
                CurrentPage.OnHidden();
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

    /// <summary>Renders the panel as laid out now; a background fills in for the glass when given.</summary>
    void SaveImage(string path, Brush? background)
    {
        UpdateLayout();
        MoveTabPill(animate: false);
        UpdateLayout();  // the pill's new width
        var root = (Visual)VisualTreeHelper.GetChild(this, 0);
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)(ActualWidth * dpi.DpiScaleX), height = (int)(ActualHeight * dpi.DpiScaleY);
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(root);
        BitmapSource image = bitmap;
        if (background != null)
        {
            var layers = new DrawingVisual();
            using (var dc = layers.RenderOpen())
            {
                var area = new Rect(0, 0, ActualWidth, ActualHeight);
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
