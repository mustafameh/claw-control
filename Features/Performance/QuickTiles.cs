using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClawControl.Core;
using ClawControl.Input;
using ClawControl.UI;

namespace ClawControl.Features.Performance;

/// <summary>How much of a row a Quick tab tile takes.</summary>
public enum TileSize
{
    /// <summary>The whole row (volume, brightness, power modes, now playing).</summary>
    Full,
    /// <summary>Half a row, like Wi-Fi (two share a row).</summary>
    Half,
    /// <summary>A circle at the row's start (the controller mode).</summary>
    Circle,
}

/// <summary>
/// The Quick tab's tiles (now playing, mode, Wi-Fi, Bluetooth, volume, brightness, power modes,
/// Screenshot, Record, Overlay) in the order the user chose, and editing them in place (chosen
/// design "A", 2026-10-30). Small tiles share rows: up to two half tiles and a circle per row, in
/// order. Edit at the bottom of the tab makes each tile one selectable block with the glass drop
/// around it; A picks it up (it lifts), the D-pad moves it (left / right past its neighbour, up /
/// down to the row above or below), A drops it; X hides or shows it (hidden ones stay dimmed while
/// editing); B or Done leaves. Above the tiles while editing: the panel's size and side, and the
/// Library tab. The order is saved in settings.
/// </summary>
sealed class QuickTiles
{
    /// <summary>The usual order; a tile missing from the saved order (a new one) goes back in its usual place.</summary>
    static readonly string[] DefaultOrder = ["media", "mode", "wifi", "bluetooth", "volume", "brightness", "power", "screenshot", "record", "overlay"];

    /// <summary>Tiles that used to be one (before 0.1.12), saved under their old id.</summary>
    static readonly Dictionary<string, string[]> SplitTiles = new()
    {
        ["connect"] = ["mode", "wifi", "bluetooth"],
        ["capture"] = ["screenshot", "record"],
    };

    readonly Panel _host;
    readonly Settings _settings;
    readonly List<Tile> _tiles = new();
    readonly Button _edit;
    /// <summary>While editing, at the top: the panel's size and side, and the Library tab.</summary>
    readonly StackPanel _panelOptions;
    readonly Button[] _sides;
    readonly Button _library;
    Tile? _picked;

    public bool Editing { get; private set; }

    /// <summary>Editing started or stopped (the page shows its media strip while editing).</summary>
    public event Action? EditingChanged;

    /// <summary>The panel's size or side changed: the panel places itself again.</summary>
    public event Action? PlacementChanged;

    /// <summary>The Library tab was turned on or off.</summary>
    public event Action? LibraryTabChanged;

    public QuickTiles(Panel host, Settings settings)
    {
        _host = host;
        _settings = settings;

        var size = Ui.Slider("", "Panel size", UI.OverlayWindow.MaxPanelSize, 5);
        size.Minimum = UI.OverlayWindow.MinPanelSize;
        size.Value = Math.Clamp(settings.PanelSize, UI.OverlayWindow.MinPanelSize, UI.OverlayWindow.MaxPanelSize);
        size.ValueText = $"{(int)size.Value}%";
        size.ValueChanged += (_, e) =>
        {
            int percent = (int)Math.Round(e.NewValue);
            size.ValueText = $"{percent}%";
            if (percent == _settings.PanelSize) return;
            _settings.PanelSize = percent;
            _settings.SaveSoon();
            PlacementChanged?.Invoke();
        };
        (var sides, _sides) = Ui.Segment(["Left", "Right"], i =>
        {
            _settings.PanelSide = i == 0 ? "Left" : "Right";
            _settings.SaveSoon();
            Ui.Choose(_sides!, i);
            PlacementChanged?.Invoke();
        });
        Ui.Choose(_sides, settings.PanelSide == "Left" ? 0 : 1);
        _library = Ui.Switch("Library tab", () =>
        {
            _settings.LibraryTab = !_settings.LibraryTab;
            _settings.SaveSoon();
            Ui.SetOn(_library!, _settings.LibraryTab);
            LibraryTabChanged?.Invoke();
        });
        Ui.SetOn(_library, settings.LibraryTab);
        _panelOptions = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
        _panelOptions.Children.Add(size);
        _panelOptions.Children.Add(ListRow.Card([Ui.Labelled("Side", sides), _library]));

        _edit = new Button
        {
            Style = Ui.Style("ChipButton"), Content = Ui.Text("Edit", bold: true), MinHeight = 34, MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 2),
        };
        _edit.Click += (_, _) => { if (Editing) Stop(); else Start(); };
    }

    public void Add(string id, FrameworkElement content, TileSize size = TileSize.Full) => _tiles.Add(new Tile(this, id, content, size));

    /// <summary>The tile's content if it's on the tab (not hidden), else null.</summary>
    public FrameworkElement? Shown(string id) => _tiles.Find(t => t.Id == id) is { } tile && !Hidden(tile) ? tile.Content : null;

    /// <summary>Puts the tiles in the saved order (call once all are added).</summary>
    public void Arrange()
    {
        // Ids saved before a tile was split stand for its parts.
        IEnumerable<string> Expand(IEnumerable<string> ids) => ids.SelectMany(id => SplitTiles.GetValueOrDefault(id, [id]));
        if (_settings.QuickHidden.Any(SplitTiles.ContainsKey)) _settings.QuickHidden = Expand(_settings.QuickHidden).Distinct().ToList();
        var saved = Expand(_settings.QuickOrder).Where(id => _tiles.Any(t => t.Id == id)).Distinct().ToList();
        foreach (var id in DefaultOrder.Concat(_tiles.Select(t => t.Id)).Distinct())
        {
            if (saved.Contains(id) || _tiles.All(t => t.Id != id)) continue;
            // After the tile that comes before it usually, else first.
            int usual = Array.IndexOf(DefaultOrder, id);
            int after = usual <= 0 ? -1 : DefaultOrder.Take(usual).Select(before => saved.IndexOf(before)).DefaultIfEmpty(-1).Max();
            saved.Insert(after + 1, id);
        }
        _tiles.Sort((a, b) => saved.IndexOf(a.Id).CompareTo(saved.IndexOf(b.Id)));
        Layout();
    }

    /// <summary>The tiles on the tab (all of them while editing), in rows.</summary>
    List<List<Tile>> Rows()
    {
        var rows = new List<List<Tile>>();
        List<Tile>? row = null;
        int halves = 0;
        bool circle = false;
        foreach (var tile in _tiles.Where(t => Editing || !Hidden(t)))
        {
            if (tile.Size == TileSize.Full)
            {
                rows.Add([tile]);
                row = null;
                continue;
            }
            if (row == null || (tile.Size == TileSize.Half && halves == 2) || (tile.Size == TileSize.Circle && circle))
            {
                rows.Add(row = new List<Tile>());
                (halves, circle) = (0, false);
            }
            row.Add(tile);
            if (tile.Size == TileSize.Half) halves++;
            else circle = true;
        }
        return rows;
    }

    /// <summary>Builds the tab from the rows: a full tile as it is, small ones side by side (a
    /// lone half tile keeps its half, it doesn't stretch).</summary>
    void Layout()
    {
        foreach (var tile in _tiles) (tile.View.Parent as Panel)?.Children.Remove(tile.View);
        _host.Children.Clear();
        _host.Children.Add(_panelOptions);
        foreach (var row in Rows())
        {
            if (row is [{ Size: TileSize.Full } full])
            {
                _host.Children.Add(full.View);
                continue;
            }
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            foreach (var tile in row)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = tile.Size == TileSize.Circle ? new GridLength(62) : new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(tile.View, grid.ColumnDefinitions.Count - 1);
                grid.Children.Add(tile.View);
            }
            int halves = row.Count(t => t.Size == TileSize.Half);
            for (int i = halves; i < 2 && i < (halves == 0 ? 1 : 2); i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _host.Children.Add(grid);
        }
        _host.Children.Add(_edit);
        foreach (var tile in _tiles) tile.Show();
    }

    bool Hidden(Tile tile) => _settings.QuickHidden.Contains(tile.Id);

    /// <param name="select">False for debug captures (the panel is closed).</param>
    public void Start(bool select = true)
    {
        Editing = true;
        _panelOptions.Visibility = Visibility.Visible;
        Layout();
        ((TextBlock)_edit.Content).Text = "Done";
        EditingChanged?.Invoke();
        if (select) Select(_tiles.First(t => t.View.Visibility == Visibility.Visible).Handle);
    }

    /// <summary>Done (B, Done, the panel closing, another page): saves the order.</summary>
    public void Stop(bool select = true)
    {
        if (!Editing) return;
        Drop();
        Editing = false;
        _panelOptions.Visibility = Visibility.Collapsed;
        Layout();
        ((TextBlock)_edit.Content).Text = "Edit";
        _settings.QuickOrder = _tiles.Select(t => t.Id).ToList();
        _settings.SaveSoon();
        EditingChanged?.Invoke();
        if (select) _host.Dispatcher.BeginInvoke(() => Select(_edit), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>B: drops a picked-up tile, else leaves editing; false when not editing.</summary>
    public bool Back()
    {
        if (!Editing) return false;
        if (_picked != null) Drop();
        else Stop();
        return true;
    }

    void Pick(Tile tile)
    {
        if (_picked == tile)
        {
            Drop();
            return;
        }
        Drop();
        _picked = tile;
        tile.Lift(true);
    }

    void Drop()
    {
        _picked?.Lift(false);
        _picked = null;
    }

    /// <summary>
    /// The D-pad while a tile is picked up. Left / right: past its neighbour (a small tile can
    /// cross into the next row). Up / down: a full tile goes past the whole row above / below; a
    /// small one joins that row in the same place (or gets a row of its own next to a full tile).
    /// </summary>
    FrameworkElement? Move(Tile tile, NavCommand command)
    {
        if (_picked != tile) return null;
        var rows = Rows();
        int r = rows.FindIndex(row => row.Contains(tile)), column = rows[r].IndexOf(tile);
        int from = _tiles.IndexOf(tile);
        switch (command)
        {
            case NavCommand.Left or NavCommand.Right when tile.Size != TileSize.Full:
                int to = from + (command == NavCommand.Left ? -1 : 1);
                if (to < 0 || to >= _tiles.Count) return tile.Handle;
                _tiles.RemoveAt(from);
                _tiles.Insert(to, tile);
                break;
            case NavCommand.Up or NavCommand.Down:
                bool up = command == NavCommand.Up;
                int target = r + (up ? -1 : 1);
                if (target < 0 || target >= rows.Count) return tile.Handle;
                var next = rows[target];
                _tiles.Remove(tile);
                if (tile.Size == TileSize.Full || next is [{ Size: TileSize.Full }])
                    _tiles.Insert(up ? _tiles.IndexOf(next[0]) : _tiles.IndexOf(next[^1]) + 1, tile);
                else
                    _tiles.Insert(column < next.Count ? _tiles.IndexOf(next[column]) : _tiles.IndexOf(next[^1]) + 1, tile);
                break;
            default:
                return null;
        }
        Layout();
        return tile.Handle;
    }

    void ToggleHidden(Tile tile)
    {
        if (!_settings.QuickHidden.Remove(tile.Id)) _settings.QuickHidden.Add(tile.Id);
        _settings.SaveSoon();
        tile.Show();
    }

    static void Select(FrameworkElement element)
    {
        element.Focus();
        if (Window.GetWindow(element) is { } window) FocusManager.SetFocusedElement(window, element);
        element.BringIntoView();
    }

    /// <summary>One tile: its content, and while editing a selectable block over it.</summary>
    sealed class Tile
    {
        static readonly Dictionary<double, ControlTemplate> BlockTemplates = new();

        readonly QuickTiles _owner;
        public readonly string Id;
        public readonly TileSize Size;
        public readonly FrameworkElement Content;
        public readonly Grid View;
        public readonly Button Handle;
        readonly FrameworkElement _hiddenMark;
        readonly ScaleTransform _lift = new();

        public Tile(QuickTiles owner, string id, FrameworkElement content, TileSize size)
        {
            _owner = owner;
            Id = id;
            Size = size;
            Content = content;
            Handle = new Button { Template = BlockTemplate(size == TileSize.Circle ? 31 : size == TileSize.Half ? 11 : 18), Visibility = Visibility.Collapsed, FocusVisualStyle = null };
            Handle.Click += (_, _) => _owner.Pick(this);
            PanelInput.SetSteer(Handle, command => _owner.Move(this, command));
            PanelInput.SetAction(Handle, () => _owner.ToggleHidden(this));
            _hiddenMark = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = UI.Glass.Tint,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, size == TileSize.Full ? 6 : 0, 0),
                Child = new TextBlock { Text = "", FontFamily = Ui.Icons, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            };
            View = new Grid { Children = { content, Handle, _hiddenMark }, RenderTransform = _lift, RenderTransformOrigin = new Point(0.5, 0.5) };
        }

        /// <summary>Shown, hidden or (editing) selectable, as things are now.</summary>
        public void Show()
        {
            bool editing = _owner.Editing, hidden = _owner.Hidden(this);
            View.Visibility = hidden && !editing ? Visibility.Collapsed : Visibility.Visible;
            Handle.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            PanelInput.SetSkip(Content, editing);
            Content.IsHitTestVisible = !editing;
            Content.Opacity = editing && hidden ? 0.35 : 1;
            _hiddenMark.Visibility = editing && hidden ? Visibility.Visible : Visibility.Collapsed;
            PanelInput.SetHint(Handle, "Move");
            PanelInput.SetActionHint(Handle, hidden ? "Show" : "Hide");
        }

        /// <summary>Picked up: the tile lifts a little (springs back when dropped).</summary>
        public void Lift(bool up)
        {
            PanelInput.SetHint(Handle, up ? "Drop" : "Move");
            Panel.SetZIndex(View, up ? 1 : 0);
            var to = new DoubleAnimation(up ? 1.035 : 1, Motion.Reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(up ? 320 : 220))
            {
                EasingFunction = up ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } : new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            _lift.BeginAnimation(ScaleTransform.ScaleXProperty, to);
            _lift.BeginAnimation(ScaleTransform.ScaleYProperty, to);
        }

        /// <summary>A clear block as big as the tile, rounded like it (the drop follows its corners).</summary>
        static ControlTemplate BlockTemplate(double radius)
        {
            if (BlockTemplates.TryGetValue(radius, out var known)) return known;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            template.Seal();
            return BlockTemplates[radius] = template;
        }
    }
}
