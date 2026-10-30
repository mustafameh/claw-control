using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClawControl.Core;
using ClawControl.Input;
using ClawControl.UI;

namespace ClawControl.Features.Performance;

/// <summary>
/// The Quick tab's tiles (now playing, mode / Wi-Fi / Bluetooth, volume, brightness, power modes,
/// capture, overlay) in the order the user chose, and editing them in place (chosen design "A",
/// 2026-10-30): Edit at the bottom of the tab makes each tile one selectable block with the glass
/// drop around it; A picks it up (it lifts), up / down move it, A drops it; X hides or shows it
/// (hidden ones stay dimmed while editing); B or Done leaves. The order is saved in settings.
/// </summary>
sealed class QuickTiles
{
    /// <summary>The usual order; a tile missing from the saved order (a new one) goes back in its usual place.</summary>
    static readonly string[] DefaultOrder = ["media", "connect", "volume", "brightness", "power", "capture", "overlay"];

    readonly Panel _host;
    readonly Settings _settings;
    readonly List<Tile> _tiles = new();
    readonly Button _edit;
    /// <summary>While editing, at the top: the panel's size and side.</summary>
    readonly StackPanel _panelOptions;
    readonly Button[] _sides;
    Tile? _picked;

    public bool Editing { get; private set; }

    /// <summary>Editing started or stopped (the page shows its media strip while editing).</summary>
    public event Action? EditingChanged;

    /// <summary>The panel's size or side changed: the panel places itself again.</summary>
    public event Action? PlacementChanged;

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
        _panelOptions = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
        _panelOptions.Children.Add(size);
        _panelOptions.Children.Add(ListRow.Card([Ui.Labelled("Side", sides)]));

        _edit = new Button
        {
            Style = Ui.Style("ChipButton"), Content = Ui.Text("Edit", bold: true), MinHeight = 34, MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 2),
        };
        _edit.Click += (_, _) => { if (Editing) Stop(); else Start(); };
    }

    public void Add(string id, FrameworkElement content) => _tiles.Add(new Tile(this, id, content));

    /// <summary>Puts the tiles in the saved order (call once all are added).</summary>
    public void Arrange()
    {
        var saved = _settings.QuickOrder.Where(id => _tiles.Any(t => t.Id == id)).Distinct().ToList();
        foreach (var id in DefaultOrder.Concat(_tiles.Select(t => t.Id)).Distinct())
        {
            if (saved.Contains(id) || _tiles.All(t => t.Id != id)) continue;
            // After the tile that comes before it usually, else first.
            int usual = Array.IndexOf(DefaultOrder, id);
            int after = usual <= 0 ? -1 : DefaultOrder.Take(usual).Select(before => saved.IndexOf(before)).DefaultIfEmpty(-1).Max();
            saved.Insert(after + 1, id);
        }
        _tiles.Sort((a, b) => saved.IndexOf(a.Id).CompareTo(saved.IndexOf(b.Id)));
        _host.Children.Clear();
        _host.Children.Add(_panelOptions);
        foreach (var tile in _tiles) _host.Children.Add(tile.View);
        _host.Children.Add(_edit);
        foreach (var tile in _tiles) tile.Show();
    }

    bool Hidden(Tile tile) => _settings.QuickHidden.Contains(tile.Id);

    /// <param name="select">False for debug captures (the panel is closed).</param>
    public void Start(bool select = true)
    {
        Editing = true;
        _panelOptions.Visibility = Visibility.Visible;
        foreach (var tile in _tiles) tile.Show();
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
        foreach (var tile in _tiles) tile.Show();
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

    /// <summary>Up / down while a tile is picked up: it swaps with its neighbour.</summary>
    FrameworkElement? Move(Tile tile, NavCommand command)
    {
        if (_picked != tile || command is not (NavCommand.Up or NavCommand.Down)) return null;
        int from = _tiles.IndexOf(tile), to = from + (command == NavCommand.Up ? -1 : 1);
        if (to < 0 || to >= _tiles.Count) return tile.Handle;
        _tiles.RemoveAt(from);
        _tiles.Insert(to, tile);
        _host.Children.Remove(tile.View);
        _host.Children.Insert(to + 1, tile.View);  // after the panel options
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
        static ControlTemplate? _blockTemplate;

        readonly QuickTiles _owner;
        public readonly string Id;
        public readonly FrameworkElement Content;
        public readonly Grid View;
        public readonly Button Handle;
        readonly FrameworkElement _hiddenMark;
        readonly ScaleTransform _lift = new();

        public Tile(QuickTiles owner, string id, FrameworkElement content)
        {
            _owner = owner;
            Id = id;
            Content = content;
            Handle = new Button { Template = BlockTemplate(), Visibility = Visibility.Collapsed, FocusVisualStyle = null };
            Handle.Click += (_, _) => _owner.Pick(this);
            PanelInput.SetSteer(Handle, command => _owner.Move(this, command));
            PanelInput.SetAction(Handle, () => _owner.ToggleHidden(this));
            _hiddenMark = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Ui.Brush("PanelBrush"),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 6, 0),
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
            var to = new DoubleAnimation(up ? 1.035 : 1, TimeSpan.FromMilliseconds(up ? 320 : 220))
            {
                EasingFunction = up ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } : new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            _lift.BeginAnimation(ScaleTransform.ScaleXProperty, to);
            _lift.BeginAnimation(ScaleTransform.ScaleYProperty, to);
        }

        /// <summary>A clear block as big as the tile, rounded like the cards (the drop follows its corners).</summary>
        static ControlTemplate BlockTemplate()
        {
            if (_blockTemplate != null) return _blockTemplate;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(18));
            _blockTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
            _blockTemplate.Seal();
            return _blockTemplate;
        }
    }
}
