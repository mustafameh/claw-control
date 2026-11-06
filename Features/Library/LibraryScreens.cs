using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClawControl.Features.Capture;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Library;

/// <summary>Add games (the Library's last tile): find them on this Claw, pick from the installed
/// apps, or pick a program.</summary>
public sealed class AddGamesScreen : UserControl, IPage
{
    public FoundGamesScreen Finder { get; }
    public AddAppScreen Installed { get; }
    public FolderScreen Programs { get; }

    public string Id => "library-add";
    public string Title => "Add games";
    public FrameworkElement View => this;
    public IEnumerable<IPage> Screens => [Finder, Installed, Programs];

    public AddGamesScreen(LibraryService library, Action<IPage> openSubPage, Action closeScreens)
    {
        Finder = new FoundGamesScreen(library, closeScreens);
        Installed = new AddAppScreen(library, asGames: true);
        Programs = new FolderScreen("library-program", () => null, path =>
        {
            library.AddGames([LibraryService.FromProgram(path)]);
            closeScreens();
        }, "Add a program", ".exe");
        var find = new ListRow(Ui.Glyph("", 16), Ui.Hex("#FF0A84FF"), "Find games on this Claw");
        find.Value.Text = "Steam · Epic · Xbox";
        find.Button.Click += (_, _) => openSubPage(Finder);
        var installed = new ListRow(Ui.Glyph("", 16), Ui.Hex("#FF34C759"), "Installed apps");
        installed.Button.Click += (_, _) => openSubPage(Installed);
        var program = new ListRow(Ui.Glyph("", 16), Ui.RowIconBrush, "Add a program");
        program.Button.Click += (_, _) => openSubPage(Programs);
        Content = ListRow.Card(find, installed, program);
    }

    public void OnShown() { }
}

/// <summary>
/// Find games on this Claw: looks in the Steam, Epic and Xbox folders (GameFinder) each time it
/// opens, lists the games not in the Library yet with nothing ticked; A ticks one (or all), and
/// Add puts the ticked ones in the Library.
/// </summary>
public sealed class FoundGamesScreen : UserControl, IPage
{
    readonly LibraryService _library;
    readonly Action _closeScreens;
    readonly StackPanel _root = new();
    readonly HashSet<string> _picked = new();
    List<LibraryItem> _found = [];
    int _inLibrary, _look;

    public string Id => "library-find";
    public string Title => "Find games";
    public FrameworkElement View => this;

    public FoundGamesScreen(LibraryService library, Action closeScreens)
    {
        _library = library;
        _closeScreens = closeScreens;
        Content = _root;
    }

    public void OnShown()
    {
        _picked.Clear();
        _root.Children.Clear();
        _root.Children.Add(Label("Looking for games…"));
        int look = ++_look;
        Task.Run(GameFinder.Find).ContinueWith(t =>
        {
            if (look != _look) return;  // left meanwhile
            List<LibraryItem> all = t.IsFaulted ? [] : t.Result;
            _found = all.Where(g => !_library.Has(g)).ToList();
            _inLibrary = all.Count - _found.Count;
            Build();
            Dispatcher.BeginInvoke(() => { if (SpatialNavigator.First(this) is { } first) Ui.Select(first); }, DispatcherPriority.Loaded);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void OnHidden() => _look++;

    void Build()
    {
        _root.Children.Clear();
        if (_found.Count == 0)
        {
            _root.Children.Add(Label(_inLibrary > 0 ? "No new games" : "No games found"));
            if (_inLibrary > 0) _root.Children.Add(Label($"In Library: {_inLibrary}"));
            return;
        }
        var add = Ui.Card(Ui.Text("", bold: true), Add, 48);
        add.HorizontalContentAlignment = HorizontalAlignment.Center;
        var all = new ListRow(null, null, "Select all", chevron: false);
        all.Value.Text = $"{_found.Count} found";
        var rows = new List<ListRow>();
        void Show()
        {
            all.Check = _picked.Count == _found.Count;
            ((TextBlock)add.Content).Text = _picked.Count switch { 0 => "Add games", 1 => "Add 1 game", var n => $"Add {n} games" };
            Ui.SetOn(add, _picked.Count > 0);
            add.Opacity = _picked.Count > 0 ? 1 : 0.5;
        }
        all.Button.Click += (_, _) =>
        {
            bool every = _picked.Count == _found.Count;
            _picked.Clear();
            if (!every) _picked.UnionWith(_found.Select(g => g.Key));
            for (int i = 0; i < rows.Count; i++) rows[i].Check = _picked.Contains(_found[i].Key);
            Show();
        };
        foreach (var game in _found)
        {
            var thumb = new Image { Width = 30, Height = 30, Stretch = Stretch.UniformToFill, ClipToBounds = true };
            var row = new ListRow(thumb, Brushes.Transparent, game.Name, chevron: false);
            row.Value.Text = game.Source;
            row.Button.Click += (_, _) =>
            {
                if (!_picked.Remove(game.Key)) _picked.Add(game.Key);
                row.Check = _picked.Contains(game.Key);
                Show();
            };
            Task.Run(() => _library.Picture(game, 64)).ContinueWith(t => { if (!t.IsFaulted) thumb.Source = t.Result; }, TaskScheduler.FromCurrentSynchronizationContext());
            rows.Add(row);
        }
        _root.Children.Add(ListRow.Card(all));
        _root.Children.Add(ListRow.Card(rows.ToArray()));
        if (_inLibrary > 0) _root.Children.Add(Label($"In Library: {_inLibrary}"));
        _root.Children.Add(add);
        Show();
    }

    void Add()
    {
        if (_picked.Count == 0) return;
        _library.AddGames(_found.Where(g => _picked.Contains(g.Key)));
        _closeScreens();
    }

    static TextBlock Label(string text)
    {
        var label = Ui.Text(text, subtle: true);
        label.Margin = new Thickness(8, 8, 8, 4);
        return label;
    }
}

/// <summary>
/// The Start menu's apps, two ways: Add an app (the Library's + circle: A pins one to the Apps band
/// or unpins it) and Installed apps (Add games: A adds one to the Games, for games that came with
/// their own installer and so aren't in Steam, Epic or Xbox). Ticked = in the Library. The list
/// shows at once from the last look and is looked at again each time (new installs come in);
/// icons fill in from the background.
/// </summary>
public sealed class AddAppScreen : UserControl, IPage
{
    /// <summary>The last look at the Start menu (both screens share it).</summary>
    static List<StartMenuApp>? _known;
    static readonly Dictionary<string, ImageSource?> Icons = new();

    readonly LibraryService _library;
    readonly bool _asGames;
    readonly StackPanel _root = new();
    int _look;

    public string Id => _asGames ? "library-installed" : "library-app";
    public string Title => _asGames ? "Installed apps" : "Add an app";
    public FrameworkElement View => this;

    /// <param name="asGames">Ticking adds the app to the Games (else pins it to the Apps band).</param>
    public AddAppScreen(LibraryService library, bool asGames)
    {
        _library = library;
        _asGames = asGames;
        Content = _root;
    }

    public void OnShown()
    {
        if (_known != null) Build();
        else
        {
            _root.Children.Clear();
            var label = Ui.Text("Looking for apps…", subtle: true);
            label.Margin = new Thickness(8, 8, 8, 4);
            _root.Children.Add(label);
        }
        int look = ++_look;
        Task.Run(StartMenuApps.List).ContinueWith(t =>
        {
            if (look != _look || t.IsFaulted) return;  // left meanwhile
            bool first = _known == null;
            if (!first && t.Result.Select(a => a.AppId).SequenceEqual(_known!.Select(a => a.AppId))) return;
            _known = t.Result;
            Build();
            if (first) Dispatcher.BeginInvoke(() => { if (SpatialNavigator.First(this) is { } top) Ui.Select(top); }, DispatcherPriority.Loaded);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void OnHidden() => _look++;

    bool Added(StartMenuApp app) => _asGames ? _library.IsGame(app) : _library.IsPinned(app);

    void Build()
    {
        _root.Children.Clear();
        var missing = new List<(StartMenuApp App, Image Icon)>();
        var rows = _known!.Select(app =>
        {
            var icon = new Image { Width = 26, Height = 26 };
            if (Icons.TryGetValue(app.AppId, out var known)) icon.Source = known;
            else missing.Add((app, icon));
            var row = new ListRow(icon, Brushes.Transparent, app.Name, chevron: false) { Check = Added(app) };
            row.Button.Click += (_, _) =>
            {
                if (_asGames)
                {
                    if (_library.IsGame(app)) _library.RemoveGame(app);
                    else _library.AddGame(app);
                }
                else if (_library.IsPinned(app)) _library.Unpin(app);
                else _library.Pin(app);
                row.Check = Added(app);
            };
            return row;
        }).ToArray();
        if (rows.Length > 0) _root.Children.Add(ListRow.Card(rows));
        // Icons one by one from the background; each shows as soon as it's there.
        if (missing.Count > 0)
            Task.Run(() =>
            {
                foreach (var (app, icon) in missing)
                {
                    var image = LibraryService.IconOf(app);
                    Dispatcher.BeginInvoke(() => { Icons[app.AppId] = image; icon.Source = image; });
                }
            });
    }
}
