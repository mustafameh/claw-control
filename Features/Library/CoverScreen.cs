using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClawControl.Core;
using ClawControl.Features.Capture;
using ClawControl.UI;

namespace ClawControl.Features.Library;

/// <summary>
/// A game's Cover screen (X on a cover; chosen 2026-11-16, mockup "A"): the cover big at the top,
/// the choices in a row under it (the current one, Steam's suggestions found by the game's name,
/// Your pictures, Icon only), each previewed as soon as it's selected; A keeps one and goes back
/// to the Library, B goes back unchanged. Remove from Library is here too (A twice).
/// </summary>
public sealed class CoverScreen : UserControl, IPage
{
    const double PreviewWidth = 176, TileWidth = 74;
    static readonly Brush RemoveBrush = Ui.Hex("#FFFF453A"), PicturesBrush = Ui.Hex("#FF0A84FF");

    readonly LibraryService _library;
    readonly Action<IPage> _openSubPage;
    readonly Action _closeScreens;
    readonly PicturesScreen _pictures;
    readonly StackPanel _root = new();
    readonly Border _preview = new() { Width = PreviewWidth, Height = PreviewWidth * 1.5, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center };
    Image _previewIcon = new();
    readonly TextBlock _label = Ui.Text("", subtle: true);
    readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 0) };
    readonly ScrollViewer _stripView;
    readonly ListRow _remove;
    List<CoverChoice> _suggestions = [];
    bool _looking, _busy, _armed;
    int _look;
    /// <summary>The choice the preview shows (its picture loads in the background).</summary>
    string? _showing;

    /// <summary>The game this screen is for (set before it opens).</summary>
    public LibraryItem? Game { get; set; }

    public string Id => "library-cover";
    public string Title => Game?.Name ?? "Cover";
    public FrameworkElement View => this;
    public IEnumerable<IPage> Screens => [_pictures];

    public CoverScreen(LibraryService library, Action<IPage> openSubPage, Action closeScreens)
    {
        _library = library;
        _openSubPage = openSubPage;
        _closeScreens = closeScreens;
        _pictures = new PicturesScreen(() => Game, Apply, openSubPage);
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        _label.Margin = new Thickness(8, 10, 8, 2);
        _stripView = new ScrollViewer
        {
            Content = _strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false, Focusable = false, PanningMode = PanningMode.HorizontalOnly, Padding = new Thickness(0, 6, 0, 8),
        };
        _remove = new ListRow(Ui.Glyph("", 16), RemoveBrush, "Remove from Library", chevron: false);
        _remove.Button.Click += (_, _) => Remove();
        _remove.Button.LostFocus += (_, _) => Disarm();
        PanelInput.SetHint(_remove.Button, "Remove");

        // Search Steam: a row (the D-pad reaches buttons only) that becomes a text box with the
        // touch keyboard when pressed; Enter searches.
        _search = new ListRow(Ui.Glyph("", 16), PicturesBrush, "Search Steam", chevron: false);
        _search.Button.Click += (_, _) => StartTyping();
        PanelInput.SetHint(_search.Button, "Type");
        _box = new TextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Ui.Brush("TextBrush"), CaretBrush = Brushes.White,
            FontSize = 15, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0), Margin = new Thickness(12, 0, 8, 0),
        };
        var placeholder = Ui.Text("Game name", subtle: true);
        placeholder.IsHitTestVisible = false;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.Margin = new Thickness(14, 0, 0, 0);
        _box.TextChanged += (_, _) => placeholder.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Search(_box.Text);
        };
        _box.LostKeyboardFocus += (_, _) => StopTyping();
        var square = Ui.IconSquare(Ui.Glyph("", 16), PicturesBrush);
        DockPanel.SetDock(square, Dock.Left);
        _typing = new DockPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(12, 0, 12, 0), Children = { square, new Grid { Children = { placeholder, _box } } } };
        var searchCell = new Grid { Children = { _search.View, _typing } };

        // Laid out once; each opening fills it in for the game.
        _root.Children.Add(new Border { Child = _preview, Margin = new Thickness(0, 6, 0, 0), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.5 } });
        _root.Children.Add(_label);
        _root.Children.Add(_stripView);
        _root.Children.Add(ListRow.Card([searchCell, _remove.View], 56));
        Content = _root;
    }

    // ---- Search Steam (the touch keyboard) ----

    readonly ListRow _search;
    readonly TextBox _box;
    readonly DockPanel _typing;
    /// <summary>What was searched for (the row shows the results of it instead of the game's name).</summary>
    string? _query;

    void StartTyping()
    {
        if (_typing.Visibility == Visibility.Visible) return;
        _search.View.Visibility = Visibility.Hidden;
        _typing.Visibility = Visibility.Visible;
        _box.Text = _query ?? "";
        _box.CaretIndex = _box.Text.Length;
        (Window.GetWindow(this) as OverlayWindow)?.AllowTyping(true);
        Dispatcher.BeginInvoke(() =>
        {
            _box.Focus();
            Keyboard.Focus(_box);
            TouchKeyboard.Show();
        }, DispatcherPriority.Input);
    }

    void StopTyping()
    {
        if (_typing.Visibility != Visibility.Visible) return;
        _typing.Visibility = Visibility.Collapsed;
        _search.View.Visibility = Visibility.Visible;
        (Window.GetWindow(this) as OverlayWindow)?.AllowTyping(false);
        TouchKeyboard.Hide();
    }

    public bool HandleBack()
    {
        if (_typing.Visibility != Visibility.Visible) return false;
        StopTyping();
        Ui.Select(_search.Button);
        return true;
    }

    /// <summary>Steam's matches for what was typed take the suggestions' place in the row.</summary>
    void Search(string text)
    {
        text = text.Trim();
        StopTyping();
        Ui.Select(_search.Button);
        if (text.Length == 0) return;
        _query = text;
        _search.Value.Text = text;
        _suggestions = [];
        _looking = true;
        BuildStrip(null);
        int look = _look;
        CoverFinder.SearchAsync(text).ContinueWith(t =>
        {
            if (look != _look || _query != text) return;
            _looking = false;
            _suggestions = t.IsFaulted ? [] : t.Result;
            BuildStrip(null);
            // On the first result (the row's first picture after the cover in use).
            if (_strip.Children.OfType<Button>().FirstOrDefault(b => b.Tag is Tile { Choice: not null, Current: false }) is { } first)
                Dispatcher.BeginInvoke(() => Ui.Select(first), DispatcherPriority.Loaded);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void OnShown()
    {
        // Debug captures open it without a game picked: one without a cover shows the most.
        Game ??= _library.Games.FirstOrDefault(g => g.Cover == null) ?? _library.Games.FirstOrDefault();
        _busy = _armed = false;
        _root.Visibility = Game == null ? Visibility.Collapsed : Visibility.Visible;
        if (Game is not { } game) return;
        _remove.Value.Text = "";
        _query = null;
        _search.Value.Text = "";
        _previewIcon = new Image();
        _preview.Child = LibraryPage.Face(game.Name, _previewIcon, PreviewWidth / 140);  // a grid cover is about 140 wide
        _preview.Background = Ui.Brush("CardBrush");
        _stripView.ScrollToHorizontalOffset(0);

        // Steam's suggestions: at once if they were found before, else from the background.
        _suggestions = [];
        int look = ++_look;
        var asked = CoverFinder.SuggestAsync(game);
        _looking = !asked.IsCompleted;
        if (!_looking) _suggestions = asked.Result;
        BuildStrip(null);
        Preview(Tiles().First(t => t.Current));
        // After the panel's own pick (the first control): on the choice in use.
        Dispatcher.BeginInvoke(() => Dispatcher.BeginInvoke(() =>
        {
            if (look == _look && _strip.Children.OfType<Button>().FirstOrDefault(b => b.Tag is Tile { Current: true }) is { } current) Ui.Select(current);
        }, DispatcherPriority.ContextIdle), DispatcherPriority.Loaded);
        if (_looking)
            asked.ContinueWith(t =>
            {
                if (look != _look || _query != null) return;  // left, or a search took the row
                _looking = false;
                _suggestions = t.IsFaulted ? [] : t.Result;
                // The row again, the selection staying on the same choice.
                string? focused = (Focused() as Button)?.Tag is Tile tile ? tile.Key : null;
                BuildStrip(focused);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void OnHidden()
    {
        _look++;
        StopTyping();
    }

    IInputElement? Focused() =>Window.GetWindow(this) is { } window ? System.Windows.Input.FocusManager.GetFocusedElement(window) : null;

    /// <summary>One choice in the row: a picture (Current, Steam's), Your pictures or Icon only.</summary>
    sealed record Tile(string Key, string Label, CoverChoice? Choice, bool Current);

    List<Tile> Tiles()
    {
        var game = Game!;
        var tiles = new List<Tile>();
        bool hasCover = game.Cover != null && File.Exists(game.Cover) && !game.IconOnly;
        if (hasCover) tiles.Add(new Tile("current", "Current", new CoverChoice("Current", game.Cover!, game.Cover!), true));
        tiles.AddRange(_suggestions.Select(s => new Tile(s.Full, s.Label, s, false)));
        tiles.Add(new Tile("pictures", "Your pictures", null, false));
        tiles.Add(new Tile("icon", "Icon only", null, !hasCover));
        return tiles;
    }

    void BuildStrip(string? keep)
    {
        _strip.Children.Clear();
        Button? keepButton = null;
        foreach (var tile in Tiles())
        {
            var button = TileButton(tile);
            _strip.Children.Add(button);
            if (tile.Key == keep) keepButton = button;
        }
        ShowLabel(null);
        if (keepButton != null) Dispatcher.BeginInvoke(() => Ui.Select(keepButton), DispatcherPriority.Loaded);
    }

    Button TileButton(Tile tile)
    {
        var face = new Grid();
        var button = new Button
        {
            Template = LibraryPage.CoverTemplate(), Background = Ui.Brush("CardBrush"), Foreground = Ui.Brush("TextBrush"), Content = face, Tag = tile,
            Width = TileWidth, Height = TileWidth * 1.5, Margin = new Thickness(4, 0, 4, 0), FocusVisualStyle = null,
        };
        if (tile.Choice is { } choice)
        {
            _ = Load(choice.Preview, 170, picture => { if (picture != null) button.Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill }; });
            PanelInput.SetHint(button, tile.Current ? "Keep" : "Use this");
        }
        else
        {
            FrameworkElement mark;
            if (tile.Key == "icon")
            {
                var icon = new Image { Width = 34, Height = 34 };
                var game = Game!;
                Task.Run(() => Hardware.RunningApps.ShellIcon(game.AppId, game.Program, 64)).ContinueWith(t => { if (!t.IsFaulted) icon.Source = t.Result; }, TaskScheduler.FromCurrentSynchronizationContext());
                mark = icon;
            }
            else mark = new TextBlock { Text = "", FontFamily = Ui.Icons, FontSize = 22, Foreground = PicturesBrush, HorizontalAlignment = HorizontalAlignment.Center };
            face.Children.Add(new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    mark,
                    new TextBlock { Text = tile.Label, FontSize = 11.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 0) },
                },
            });
            PanelInput.SetHint(button, tile.Key == "pictures" ? "Open" : tile.Current ? "Keep" : "Use this");
        }
        if (tile.Current)
            face.Children.Add(new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = PicturesBrush, Margin = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = "", FontFamily = Ui.Icons, FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        PanelInput.SetStayInRow(button, true);
        button.GotFocus += (_, _) => Preview(tile);
        button.Click += (_, _) => Pick(tile);
        return button;
    }

    /// <summary>The preview shows the selected choice (Your pictures: the cover in use).</summary>
    void Preview(Tile tile)
    {
        ShowLabel(tile);
        var game = Game!;
        string? source = tile.Choice?.Preview ?? (tile.Key == "pictures" && !game.IconOnly ? game.Cover : null);
        _showing = source;
        if (source == null)
        {
            ShowIcon();
            return;
        }
        _ = Load(source, 420, picture =>
        {
            if (_showing != source) return;  // moved on meanwhile
            if (picture == null) ShowIcon();
            else
            {
                _preview.Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
                _previewIcon.Visibility = Visibility.Collapsed;
            }
        });
    }

    void ShowIcon()
    {
        _preview.Background = Ui.Brush("CardBrush");
        _previewIcon.Visibility = Visibility.Visible;
        if (_previewIcon.Source == null)
        {
            var (game, icon) = (Game!, _previewIcon);
            Task.Run(() => Hardware.RunningApps.ShellIcon(game.AppId, game.Program, 96)).ContinueWith(t => { if (!t.IsFaulted) icon.Source = t.Result; }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    void ShowLabel(Tile? tile)
    {
        string text = tile == null ? "" : tile.Choice != null && !tile.Current ? $"{tile.Label} · Steam" : tile.Label;
        string none = _query != null ? $"Nothing found for “{_query}”" : "Nothing on Steam";
        if (_looking) text = text.Length > 0 ? $"{text}   ·   Looking on Steam…" : "Looking on Steam…";
        else if (_suggestions.Count == 0 && tile?.Key is "current" or "icon" or null) text = text.Length > 0 ? $"{text}   ·   {none}" : none;
        _label.Text = text;
    }

    async Task Load(string source, int height, Action<ImageSource?> show)
    {
        int look = _look;
        var picture = await CoverFinder.PreviewAsync(source, height);
        if (look == _look) show(picture);
    }

    void Pick(Tile tile)
    {
        if (_busy || Game is not { } game) return;
        switch (tile.Key)
        {
            case "pictures":
                _openSubPage(_pictures);
                return;
            case "icon":
                if (game.Cover != null) _library.SetIconOnly(game);
                _closeScreens();
                return;
            case "current":
                _closeScreens();
                return;
        }
        _ = Apply(tile.Choice!.Full);
    }

    /// <summary>Makes the picture the cover and goes back to the Library (Your pictures uses it too).</summary>
    async Task Apply(string source)
    {
        if (_busy || Game is not { } game) return;
        _busy = true;
        _label.Text = "Saving…";
        try
        {
            await _library.SetCoverAsync(game, source);
            _closeScreens();
        }
        catch (Exception ex)
        {
            Log.Error($"Saving a cover for {game.Name}", ex);
            _label.Text = "Couldn't use that picture";
        }
        finally
        {
            _busy = false;
        }
    }

    void Remove()
    {
        if (Game is not { } game) return;
        if (!_armed)
        {
            _armed = true;
            _remove.Value.Text = "Press A again";
            return;
        }
        _library.Remove(game);
        Game = null;
        _closeScreens();
    }

    void Disarm()
    {
        _armed = false;
        _remove.Value.Text = "";
    }
}

/// <summary>
/// Your pictures (from a game's Cover screen): the newest pictures in Downloads, Pictures, the
/// captures folder and the desktop, as covers would show them; A uses one. Browse folders opens
/// any folder (an SD card too).
/// </summary>
public sealed class PicturesScreen : UserControl, IPage
{
    const int MaxPictures = 60;
    const string Types = ".jpg;.jpeg;.png;.webp;.bmp";

    readonly Func<LibraryItem?> _game;
    readonly Func<string, Task> _use;
    readonly Action<IPage> _openSubPage;
    readonly FolderScreen _browse;
    readonly StackPanel _root = new();
    readonly UniformGrid _grid = new() { Columns = 3, Margin = new Thickness(0, 2, 0, 0) };
    readonly TextBlock _label = Ui.Text("", subtle: true);
    int _look;

    public string Id => "library-cover-pictures";
    public string Title => "Your pictures";
    public FrameworkElement View => this;
    public IEnumerable<IPage> Screens => [_browse];

    public PicturesScreen(Func<LibraryItem?> game, Func<string, Task> use, Action<IPage> openSubPage)
    {
        _game = game;
        _use = use;
        _openSubPage = openSubPage;
        _browse = new FolderScreen("library-cover-browse", () => null, path => _ = _use(path), "Browse folders", Types);
        _label.Margin = new Thickness(8, 6, 8, 2);
        _label.TextTrimming = TextTrimming.CharacterEllipsis;
        var browse = new ListRow(Ui.Glyph("", 16), Ui.Hex("#FF0A84FF"), "Browse folders");
        browse.Button.Click += (_, _) => _openSubPage(_browse);
        _root.Children.Add(ListRow.Card(browse));
        _root.Children.Add(_label);
        _root.Children.Add(_grid);
        Content = _root;
    }

    public void OnShown()
    {
        _grid.Children.Clear();
        _label.Text = "Looking for pictures…";
        int look = ++_look;
        Task.Run(Find).ContinueWith(t =>
        {
            if (look != _look) return;
            var files = t.IsFaulted ? [] : t.Result;
            _label.Text = files.Count == 0 ? "No pictures in Downloads, Pictures or Captures" : "Newest first";
            foreach (var file in files) _grid.Children.Add(Tile(file, look));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void OnHidden() => _look++;

    Button Tile(string file, int look)
    {
        var button = new Button
        {
            Template = LibraryPage.CoverTemplate(), Background = Ui.Brush("CardBrush"), Margin = new Thickness(4), FocusVisualStyle = null, Tag = file,
        };
        button.SizeChanged += (_, e) => { if (e.WidthChanged) button.Height = Math.Round(e.NewSize.Width * 1.5); };
        button.GotFocus += (_, _) => _label.Text = $"{Path.GetFileName(file)}  ·  {Path.GetFileName(Path.GetDirectoryName(file))}";
        button.Click += (_, _) => _ = _use(file);
        PanelInput.SetHint(button, "Use this");
        _ = Load();
        return button;

        async Task Load()
        {
            var picture = await CoverFinder.PreviewAsync(file, 220);
            if (look == _look && picture != null) button.Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
        }
    }

    /// <summary>The newest pictures in the usual places (and the folders right inside them).</summary>
    static List<string> Find()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] places =
        [
            Path.Combine(home, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Hardware.ScreenCapture.Folder,Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ];
        var types = Types.Split(';');
        var found = new List<FileInfo>();
        foreach (var place in places.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var dir = new DirectoryInfo(place);
                if (!dir.Exists) continue;
                var folders = dir.EnumerateDirectories().Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0).Take(40).Prepend(dir);
                foreach (var folder in folders)
                    try
                    {
                        found.AddRange(folder.EnumerateFiles().Where(f => types.Contains(f.Extension.ToLowerInvariant()) && (f.Attributes & FileAttributes.Hidden) == 0 && f.Length > 8 * 1024));
                    }
                    catch { }  // no access: skipped
            }
            catch (Exception ex)
            {
                Log.Detail($"Pictures in {place}: {ex.Message}");
            }
        }
        return found.DistinctBy(f => f.FullName.ToLowerInvariant()).OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxPictures).Select(f => f.FullName).ToList();
    }
}
