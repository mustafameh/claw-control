using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClawControl.Core;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Library;

/// <summary>
/// The Library tab (chosen 2026-11-05, layout "A"): pinned apps as circles on their own lighter
/// band, then the games the user added as covers, three a row, last played first, with the
/// selected one's name and source under them. A running game says "Playing" (A switches to it);
/// A on another shows "Starting…" until its window appears, then the panel closes. X removes a
/// game or unpins an app. Games are only ever added by the user (Add games: find them on this
/// Claw and tick the ones wanted, or pick a program). Edit on the Quick tab can turn the tab off.
/// </summary>
public sealed class LibraryPage : UserControl, IPage
{
    const int CoverPixels = 260;
    static readonly TimeSpan StartWait = TimeSpan.FromSeconds(45);
    static readonly Brush BandBrush = Ui.Hex("#12FFFFFF"), BandRimBrush = Ui.Hex("#24FFFFFF"), AddBrush = Ui.Hex("#FF0A84FF"),
        PlayingBrush = Ui.Hex("#FF34C759"), BadgeBrush = Ui.Hex("#A6000000"), StartingBrush = Ui.Hex("#99000000");
    /// <summary>A tall, bold poster face that every Windows has (the look of the mockup's covers).</summary>
    static readonly FontFamily PosterFont = new("Impact, Arial Narrow, Segoe UI");
    /// <summary>Darkens a cover's bottom so its name reads over any art.</summary>
    static readonly Brush FadeBrush = Fade();

    static Brush Fade()
    {
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0xB8, 0, 0, 0), 1));
        fade.Freeze();
        return fade;
    }

    readonly LibraryService _library;
    readonly Action<IPage> _openSubPage;
    readonly Action _closePanel;
    readonly AddGamesScreen _addGames;
    readonly AddAppScreen _addApp;
    readonly UniformGrid _apps = new() { Columns = 5 };
    readonly UniformGrid _games = new() { Columns = 3 };
    readonly ScrollViewer _gamesView;
    readonly TextBlock _count = Ui.Text("", subtle: true), _name = Ui.Text("", bold: true), _detail = Ui.Text("", subtle: true);
    readonly FrameworkElement _empty, _caption;
    readonly Dictionary<string, (Button Button, FrameworkElement Mark, FrameworkElement? Starting)> _views = new();
    readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(700) };
    Dictionary<string, RunningApp> _running = new();
    LibraryItem? _starting;
    DateTime _startedAt;
    bool _shown, _checking;

    public string Id => "library";
    public string Title => "Library";
    public FrameworkElement View => this;
    public IEnumerable<IPage> Screens => [_addGames, _addApp];

    public LibraryPage(LibraryService library, Action<IPage> openSubPage, Action closeScreens, Action closePanel)
    {
        _library = library;
        _openSubPage = openSubPage;
        _closePanel = closePanel;
        _addGames = new AddGamesScreen(library, openSubPage, closeScreens);
        _addApp = new AddAppScreen(library, asGames: false);

        var appsBand = new Border
        {
            Background = BandBrush, BorderBrush = BandRimBrush, BorderThickness = new Thickness(0.67), CornerRadius = new CornerRadius(11),
            Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(4, 0, 4, 6),
            Child = Ui.Stack(Heading("Apps", null), _apps),
        };
        _gamesView = new ScrollViewer
        {
            Content = _games, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false, PanningMode = PanningMode.VerticalOnly,
        };
        ScrollFade.Attach(_gamesView, 34);
        // The D-pad's rows glide into place instead of jumping (the glass drop rides along).
        _games.RequestBringIntoView += (_, e) =>
        {
            if (e.TargetObject is not FrameworkElement cover || cover == _games) return;
            e.Handled = true;
            GlideTo(cover);
            _gamesView.BringIntoView();  // the games themselves on screen in the page
        };
        var find = new Button { Style = Ui.Style("ChipButton"), Content = Ui.Text("Find games on this Claw", bold: true), MinHeight = 40, Margin = new Thickness(0, 10, 0, 0) };
        find.Click += (_, _) => _openSubPage(_addGames.Finder);
        var installed = new Button { Style = Ui.Style("ChipButton"), Content = Ui.Text("Installed apps", bold: true), MinHeight = 40, Margin = new Thickness(0, 6, 0, 0) };
        installed.Click += (_, _) => _openSubPage(_addGames.Installed);
        var program = new Button { Style = Ui.Style("ChipButton"), Content = Ui.Text("Add a program", bold: true), MinHeight = 40, Margin = new Thickness(0, 6, 0, 0) };
        program.Click += (_, _) => _openSubPage(_addGames.Programs);
        var none = Ui.Text("No games yet", bold: true);
        none.HorizontalAlignment = HorizontalAlignment.Center;
        _empty = new Border
        {
            CornerRadius = new CornerRadius(11), Background = Ui.Brush("CardBrush"), Padding = new Thickness(16, 18, 16, 14), Margin = new Thickness(4),
            Child = Ui.Stack(none, find, installed, program),
        };
        _name.HorizontalAlignment = _detail.HorizontalAlignment = HorizontalAlignment.Center;
        _caption = new StackPanel { Margin = new Thickness(0, 4, 0, 0), Children = { _name, _detail } };
        Content = Ui.Stack(appsBand, Heading("Games", _count), _gamesView, _empty, _caption);

        _watch.Tick += (_, _) => Watch();
        // Laid out the first time it's shown, not at startup (nothing to load until it's opened).
    }

    static FrameworkElement Heading(string text, TextBlock? value)
    {
        var title = Ui.Text(text, subtle: true);
        var line = new DockPanel { Margin = new Thickness(8, 0, 8, 4) };
        if (value != null)
        {
            DockPanel.SetDock(value, Dock.Right);
            line.Children.Add(value);
        }
        line.Children.Add(title);
        return line;
    }

    public void OnShown()
    {
        _shown = true;
        // The same games and apps in the same order: the tab as it is, only its marks refreshed.
        if (Arrangement() != _built) Build(FocusedItem());
        else ShowStates();
        FitGamesSoon();
        FetchCovers();
        // Steam's own record of what was played, and what's running: from the background (the
        // tab is laid out again only if the order changed).
        _ = Task.Run(() =>
        {
            _library.ReadLastPlayed();
            return _library.Running(RunningApps.List());
        }).ContinueWith(t =>
        {
            if (t.IsFaulted || !_shown) return;
            _running = t.Result;
            if (Arrangement() != _built) Build(FocusedItem());
            else ShowStates();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>What the tab was last laid out for: the games in order, then the apps.</summary>
    string? _built;

    string Arrangement() => string.Join("|", _library.Games.Select(g => g.Key)) + "#" + string.Join("|", _library.Apps.Select(a => a.Key));

    /// <summary>Covers a game is missing (Epic's) come in quietly; the tab shows them when they do.</summary>
    async void FetchCovers()
    {
        try
        {
            if ((await _library.FetchCoversAsync()).Count > 0 && _shown) Build(FocusedItem());
        }
        catch (Exception ex)
        {
            Log.Error("Downloading covers", ex);
        }
    }

    public void OnHidden()
    {
        _shown = false;
        _watch.Stop();
        _starting = null;
    }

    LibraryItem? FocusedItem()
    {
        if (Window.GetWindow(this) is not { } window || FocusManager.GetFocusedElement(window) is not Button focused) return null;
        return focused.Tag as LibraryItem;
    }

    /// <summary>Lays the tab out from the Library (cheap: a few dozen buttons, pictures kept), the
    /// selection staying on <paramref name="keep"/>.</summary>
    void Build(LibraryItem? keep)
    {
        _built = Arrangement();
        FitGamesSoon();
        _views.Clear();
        _apps.Children.Clear();
        foreach (var app in _library.Apps) _apps.Children.Add(AppButton(app));
        _apps.Children.Add(AddButton());

        _games.Children.Clear();
        var games = _library.Games;
        foreach (var game in games) _games.Children.Add(Cover(game));
        _games.Children.Add(AddTile());
        _count.Text = games.Count > 0 ? games.Count.ToString() : "";
        _gamesView.Visibility = games.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = games.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowStates();

        if (keep != null && _views.TryGetValue(keep.Key, out var view))
            Dispatcher.BeginInvoke(() => Ui.Select(view.Button), DispatcherPriority.Loaded);
    }

    Button AppButton(LibraryItem app)
    {
        var icon = new Image { Width = 30, Height = 30 };
        var dot = new Ellipse
        {
            Width = 11, Height = 11, Fill = PlayingBrush, Stroke = UI.Glass.Tint, StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed,
        };
        var button = new Button
        {
            Style = Ui.Style("ModeButton"), Tag = app, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2),
            Content = new Grid { Width = 44, Height = 44, Children = { icon, dot } },
        };
        button.Click += (_, _) => Open(app);
        button.GotFocus += (_, _) => Describe(app);
        PanelInput.SetAction(button, () => { _library.Remove(app); Build(null); });
        PanelInput.SetActionHint(button, "Unpin");
        LoadPicture(app, 64, picture => icon.Source = picture);
        _views[app.Key] = (button, dot, null);
        return button;
    }

    Button AddButton()
    {
        var button = new Button
        {
            Style = Ui.Style("ModeButton"), Background = AddBrush, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2),
            Content = "", FontSize = 18,
        };
        Ui.SetOn(button, true);
        button.Click += (_, _) => _openSubPage(_addApp);
        button.GotFocus += (_, _) => ShowCaption("Add an app", "Start menu");
        PanelInput.SetHint(button, "Add");
        return button;
    }

    /// <summary>A game's cover: its art (Steam's, Epic's), else its icon on a card; its name along
    /// the bottom in a poster face over a soft dark fade; Playing / Starting… over it.</summary>
    Button Cover(LibraryItem game)
    {
        var face = new Grid();
        var icon = new Image { Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 40) };
        face.Children.Add(icon);
        face.Children.Add(new Border { CornerRadius = new CornerRadius(0, 0, 10, 10), Background = FadeBrush, VerticalAlignment = VerticalAlignment.Bottom, Height = 90 });
        face.Children.Add(new TextBlock
        {
            Text = game.Name.ToUpperInvariant(), FontFamily = PosterFont, FontSize = 17, Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 17, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = 3 * 17, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(9, 0, 9, 9),
        });

        var playing = new Border
        {
            Background = BadgeBrush, CornerRadius = new CornerRadius(9), Padding = new Thickness(6, 2, 8, 2), Margin = new Thickness(7),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Ellipse { Width = 7, Height = 7, Fill = PlayingBrush, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "Playing", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
                },
            },
        };
        face.Children.Add(playing);
        var startingText = new TextBlock { Text = "Starting…", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var starting = new Border { Background = StartingBrush, CornerRadius = new CornerRadius(10), Visibility = Visibility.Collapsed, Child = startingText };
        startingText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        face.Children.Add(starting);

        var button = new Button { Template = CoverTemplate(), Background = Ui.Brush("CardBrush"), Foreground = Ui.Brush("TextBrush"), Content = face, Tag = game, Margin = new Thickness(4), FocusVisualStyle = null };
        Square(button);
        button.Click += (_, _) => Open(game);
        button.GotFocus += (_, _) => Describe(game);
        PanelInput.SetAction(button, () => { _library.Remove(game); Build(null); });
        PanelInput.SetActionHint(button, "Remove");
        LoadPicture(game, CoverPixels, picture =>
        {
            if (picture == null) return;
            if (game.Cover != null && !game.SquareCover && picture is System.Windows.Media.Imaging.BitmapImage)
            {
                button.Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
                icon.Visibility = Visibility.Collapsed;
            }
            else icon.Source = picture;
        });
        _views[game.Key] = (button, playing, starting);
        return button;
    }

    /// <summary>The last tile: Add games (a dashed outline).</summary>
    Button AddTile()
    {
        var content = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "", FontFamily = Ui.Icons, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) },
                new TextBlock { Text = "Add games", FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            },
        };
        var button = new Button { Template = AddTemplate(), Foreground = Ui.Brush("TextBrush"), Content = content, Margin = new Thickness(4), FocusVisualStyle = null };
        Square(button);
        button.Click += (_, _) => _openSubPage(_addGames);
        button.GotFocus += (_, _) => ShowCaption("Add games", "Steam · Epic Games · Xbox · Program");
        PanelInput.SetHint(button, "Add");
        return button;
    }

    /// <summary>Covers are 2 : 3 (a new width, so a new row height: the games' height again).</summary>
    void Square(Button button) => button.SizeChanged += (_, e) =>
    {
        if (!e.WidthChanged) return;
        button.Height = Math.Round(e.NewSize.Width * 1.5);
        FitGamesSoon();
    };

    // ---- How tall the games are: down to just above the taskbar ----

    ScrollViewer? _pageScroller;

    /// <summary>
    /// The games get all the height the panel has left down to just above the taskbar: what the
    /// page shows now plus how much taller the panel may still grow (its window's MaxHeight), less
    /// the rest of the tab. So as many rows show as fit and the next one peeks in; the rest
    /// scroll. Worked out once each time the tab is shown or laid out, after that layout (never from
    /// a layout event: setting the height there resized the panel, which laid it out again, in a loop).
    /// </summary>
    void FitGamesSoon() => Dispatcher.BeginInvoke(FitGames, DispatcherPriority.Loaded);

    void FitGames()
    {
        if (!IsVisible || _games.Children.Count == 0 || _games.Children[0] is not FrameworkElement first || first.ActualHeight <= 0) return;
        _pageScroller ??= Ancestor<ScrollViewer>(this);
        if (_pageScroller == null || Window.GetWindow(this) is not { } window || double.IsInfinity(window.MaxHeight)) return;
        // The panel is scaled (its size, the screen): window units → this page's units.
        double scale = _pageScroller.TransformToAncestor(window).TransformBounds(new Rect(0, 0, 1, 1)).Height;
        if (scale <= 0) return;
        double growth = Math.Max(0, window.MaxHeight - window.ActualHeight) / scale;
        double others = ActualHeight - _gamesView.ActualHeight;
        double pitch = first.ActualHeight + first.Margin.Top + first.Margin.Bottom;
        double height = Math.Floor(Math.Max(pitch * 1.25, _pageScroller.ViewportHeight + growth - others));
        if (Math.Abs(_gamesView.MaxHeight - height) > 1) _gamesView.MaxHeight = height;
    }

    static T? Ancestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var at = VisualTreeHelper.GetParent(start); at != null; at = VisualTreeHelper.GetParent(at))
            if (at is T found) return found;
        return null;
    }

    // ---- Gliding between rows ----

    static readonly TimeSpan GlideTime = TimeSpan.FromMilliseconds(260);
    double _glideFrom, _glideTo;
    TimeSpan _glideStart = TimeSpan.MinValue;
    bool _gliding;

    /// <summary>Scrolls so the selected cover's row is the second one shown (a row above it, the
    /// top of the next below), but always with its whole row in view; easing out, and a new target
    /// mid-way starts from where it is.</summary>
    void GlideTo(FrameworkElement cover)
    {
        double pitch = cover.ActualHeight + cover.Margin.Top + cover.Margin.Bottom;
        double top = cover.TranslatePoint(new Point(0, 0), _games).Y - cover.Margin.Top;
        double target = Math.Max(top - pitch, top + pitch - _gamesView.ViewportHeight);  // whole row in view
        target = Math.Clamp(Math.Min(target, top), 0, _gamesView.ScrollableHeight);
        if (Math.Abs(target - (_gliding ? _glideTo : _gamesView.VerticalOffset)) < 0.5) return;
        if (Motion.Reduced && !_gliding)
        {
            _gamesView.ScrollToVerticalOffset(target);
            return;
        }
        (_glideFrom, _glideTo, _glideStart) = (_gamesView.VerticalOffset, target, TimeSpan.MinValue);
        if (_gliding) return;
        _gliding = true;
        CompositionTarget.Rendering += Glide;
    }

    void Glide(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_glideStart == TimeSpan.MinValue) _glideStart = now;
        double t = Math.Min(1, (now - _glideStart).TotalMilliseconds / GlideTime.TotalMilliseconds);
        double eased = 1 - Math.Pow(1 - t, 3);
        _gamesView.ScrollToVerticalOffset(_glideFrom + (_glideTo - _glideFrom) * eased);
        if (t < 1) return;
        _gliding = false;
        CompositionTarget.Rendering -= Glide;
    }

    /// <summary>Loads a picture from the background and shows it (built at startup too, before the
    /// dispatcher runs: no synchronization context to continue on yet).</summary>
    void LoadPicture(LibraryItem item, int width, Action<ImageSource?> show)
    {
        if (_library.TryKnownPicture(item, width, out var known)) show(known);
        else LoadPictureLater(item, width, show);
    }

    void LoadPictureLater(LibraryItem item, int width, Action<ImageSource?> show) => Task.Run(() =>
    {
        try
        {
            var picture = _library.Picture(item, width);
            Dispatcher.BeginInvoke(() => show(picture));
        }
        catch (Exception ex)
        {
            Log.Error($"Picture of {item.Name}", ex);
        }
    });

    /// <summary>Playing marks, Starting…, and what A says, as things are now.</summary>
    void ShowStates()
    {
        foreach (var item in _library.Games.Concat(_library.Apps))
        {
            if (!_views.TryGetValue(item.Key, out var view)) continue;
            bool running = _running.ContainsKey(item.Key), starting = _starting?.Key == item.Key;
            view.Mark.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (view.Starting != null) view.Starting.Visibility = starting && !running ? Visibility.Visible : Visibility.Collapsed;
            PanelInput.SetHint(view.Button, running ? "Switch" : item.Source == "App" ? "Open" : "Play");
        }
        if (FocusedItem() is { } focused) Describe(focused);
    }

    /// <summary>The name, and under it what's happening (Playing, Starting…) or where it's from.</summary>
    void Describe(LibraryItem item)
    {
        bool running = _running.ContainsKey(item.Key);
        string detail = running ? (item.Source == "App" ? "Running" : "Playing")
            : _starting?.Key == item.Key ? "Starting…"
            : item.Source == "App" ? "App" : item.Source;
        ShowCaption(item.Name, detail);
    }

    void ShowCaption(string name, string detail)
    {
        _name.Text = name;
        _detail.Text = detail;
    }

    /// <summary>A: back to it if it's running, else open it and wait (a few seconds at most) for
    /// its window; the panel closes once it's there.</summary>
    void Open(LibraryItem item)
    {
        if (_running.TryGetValue(item.Key, out var app))
        {
            RunningApps.SwitchTo(app);
            _closePanel();
            return;
        }
        if (_starting != null) return;
        if (!_library.Open(item))
        {
            ShowCaption(item.Name, "Couldn't open it");
            return;
        }
        _starting = item;
        _startedAt = DateTime.UtcNow;
        _watch.Start();
        Build(item);  // played now: it moves to the front
    }

    async void Watch()
    {
        if (_starting is not { } item || !_shown)
        {
            _watch.Stop();
            return;
        }
        if (DateTime.UtcNow - _startedAt > StartWait)
        {
            _watch.Stop();
            _starting = null;
            ShowStates();
            return;
        }
        if (_checking) return;
        _checking = true;
        try
        {
            var running = await Task.Run(() => _library.Running(RunningApps.List()));
            if (_starting != item || !_shown) return;
            _running = running;
            if (!running.ContainsKey(item.Key)) return;
            _watch.Stop();
            _starting = null;
            ShowStates();
            _closePanel();
        }
        catch (Exception ex)
        {
            Log.Error("Checking whether a game started", ex);
        }
        finally
        {
            _checking = false;
        }
    }

    static ControlTemplate? _coverTemplate, _addTemplate;

    /// <summary>A rounded picture card (its background is the cover); the drop follows its corners.</summary>
    static ControlTemplate CoverTemplate()
    {
        if (_coverTemplate != null) return _coverTemplate;
        var card = new FrameworkElementFactory(typeof(Border), "Card");
        card.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        card.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        card.AppendChild(content);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(OpacityProperty, 0.8, "Card"));
        _coverTemplate = new ControlTemplate(typeof(Button)) { VisualTree = card, Triggers = { pressed } };
        _coverTemplate.Seal();
        return _coverTemplate;
    }

    static ControlTemplate AddTemplate()
    {
        if (_addTemplate != null) return _addTemplate;
        var root = new FrameworkElementFactory(typeof(Border));
        root.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var grid = new FrameworkElementFactory(typeof(Grid));
        var outline = new FrameworkElementFactory(typeof(Rectangle));
        outline.SetValue(Shape.StrokeProperty, Ui.Hex("#66FFFFFF"));
        outline.SetValue(Shape.StrokeThicknessProperty, 1.5);
        outline.SetValue(Shape.StrokeDashArrayProperty, new DoubleCollection { 4, 3 });
        outline.SetValue(Rectangle.RadiusXProperty, 10.0);
        outline.SetValue(Rectangle.RadiusYProperty, 10.0);
        grid.AppendChild(outline);
        grid.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        root.AppendChild(grid);
        _addTemplate = new ControlTemplate(typeof(Button)) { VisualTree = root };
        _addTemplate.Seal();
        return _addTemplate;
    }
}
