using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ClawControl.Core;
using ClawControl.Input;
using ClawControl.Monitoring;
using ClawControl.UI;

namespace ClawControl.Features.Overlay;

/// <summary>
/// The Quick tab's Overlay tile, like the Wi-Fi one: A turns the performance overlay on or off
/// (blue while on), its › opens the Overlay screen.
/// </summary>
public sealed class OverlayTile
{
    static readonly Brush OnBrush = Ui.Hex("#FF0A84FF");

    readonly Settings _settings;
    readonly OverlayController _overlay;
    readonly QuickTile _tile;

    public FrameworkElement View => _tile.View;

    public OverlayTile(Settings settings, OverlayController overlay, Action openScreen)
    {
        _settings = settings;
        _overlay = overlay;
        _tile = new QuickTile("", "Overlay", Toggle, openScreen);
        Refresh();
    }

    void Toggle()
    {
        _settings.OverlayEnabled = !_settings.OverlayEnabled;
        _settings.SaveSoon();
        _overlay.Apply();
        Refresh();
    }

    public void Refresh() => _tile.Show("Overlay", _settings.OverlayEnabled ? OnBrush : null);
}

/// <summary>
/// The Overlay screen (the tile's ›): on/off, one of six screen positions, any combination of
/// the numbers to show, and their size. Blue means on/chosen, so the buttons carry no extra
/// state text.
/// </summary>
public sealed class OverlayScreen : UserControl, IPage
{
    // Where each position's spot sits on the mini screen: row, column.
    static readonly (string Id, int Row, int Column)[] Positions =
    {
        ("TopLeft", 0, 0), ("TopCenter", 0, 1), ("TopRight", 0, 2),
        ("BottomLeft", 1, 0), ("BottomCenter", 1, 1), ("BottomRight", 1, 2),
    };

    readonly Settings _settings;
    readonly OverlayController _overlay;
    readonly Action _changed;
    readonly Button _toggle;
    readonly TextBlock _toggleHint;
    readonly Dictionary<string, Button> _positions = new();
    readonly Dictionary<string, Button> _items = new();

    public string Id => "overlay";
    public string Title => "Overlay";
    public FrameworkElement View => this;

    /// <param name="changed">On / off changed here (the Quick tab's tile follows).</param>
    public OverlayScreen(Settings settings, OverlayController overlay, Action changed)
    {
        _settings = settings;
        _overlay = overlay;
        _changed = changed;

        _toggle = Ui.Switch("Overlay", () => Change(() => _settings.OverlayEnabled = !_settings.OverlayEnabled));
        // Only says something when the overlay is on but has nothing to show.
        _toggleHint = new TextBlock { Text = "Pick at least one number", Style = Ui.Style("CardDetail"), Margin = new Thickness(12, 0, 12, 4) };

        // Mini screen on the left (tap where the overlay goes), numbers on the right.
        var layout = new Grid { Margin = new Thickness(4, 4, 4, 0) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(136) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());

        var spots = new Grid();
        for (int i = 0; i < 2; i++) spots.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < 3; i++) spots.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (id, row, column) in Positions)
        {
            var spot = new Button
            {
                Style = Ui.Style("SpotButton"),
                VerticalAlignment = row == 0 ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                HorizontalAlignment = column switch { 0 => HorizontalAlignment.Left, 1 => HorizontalAlignment.Center, _ => HorizontalAlignment.Right },
            };
            Grid.SetRow(spot, row);
            Grid.SetColumn(spot, column);
            spot.Click += (_, _) => Change(() => _settings.OverlayPosition = id);
            _positions[id] = spot;
            spots.Children.Add(spot);
        }
        layout.Children.Add(new Border
        {
            BorderBrush = Ui.Brush("TrackBrush"),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(3, 3, 6, 3),
            Padding = new Thickness(3),
            Child = spots,
        });

        // Numbers in pages of 9 (3 × 3), so the card is three rows tall: D-pad right past the last
        // column flips to the next page, left from the first column flips back; dots show the page.
        var pages = new Grid();
        Grid.SetColumn(pages, 1);
        layout.Children.Add(pages);
        var metrics = Metrics.All;
        for (int start = 0; start < metrics.Count; start += PageSize)
        {
            var page = new UniformGrid { Columns = Columns, Rows = PageSize / Columns, VerticalAlignment = VerticalAlignment.Top };
            _pages.Add(page);
            pages.Children.Add(page);
            foreach (var metric in metrics.Skip(start).Take(PageSize))
            {
                var button = Chip(metric.Label);
                button.Click += (_, _) => Change(() =>
                {
                    if (!_settings.OverlayItems.Remove(metric.Id))
                        _settings.OverlayItems = Metrics.All.Select(m => m.Id)
                            .Where(id => id == metric.Id || _settings.OverlayItems.Contains(id)).ToList();
                });
                _items[metric.Id] = button;
                page.Children.Add(button);
            }
        }
        for (int p = 0; p < _pages.Count; p++) SteerPage(p);

        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 4) };
        for (int p = 0; p < _pages.Count; p++)
        {
            int target = p;
            // A tap target around each dot (touch); not in the D-pad's way.
            var dot = new Border { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(3, 0, 3, 0) };
            var hit = new Border { Background = Brushes.Transparent, Padding = new Thickness(4, 6, 4, 6), Child = dot };
            hit.MouseLeftButtonUp += (_, _) => ShowPage(target);
            hit.TouchUp += (_, e) => { ShowPage(target); e.Handled = true; };
            _dots.Add(dot);
            dots.Children.Add(hit);
        }

        // How big the numbers are on the screen (80-200 %).
        var size = Ui.Slider("", "Size", 200, 10);
        size.Minimum = 80;
        size.Value = Math.Clamp(settings.OverlaySize, 80, 200);
        size.ValueText = $"{(int)size.Value}%";
        size.Margin = new Thickness(0, 6, 0, 0);
        size.ValueChanged += (_, e) =>
        {
            size.ValueText = $"{(int)Math.Round(e.NewValue)}%";
            Change(() => _settings.OverlaySize = (int)Math.Round(e.NewValue));
        };

        var where = new Border
        {
            Background = Ui.Brush("CardBrush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(2), Margin = new Thickness(4, 6, 4, 4),
            Child = Ui.Stack(layout, dots),
        };
        Content = Ui.Stack(_toggle, _toggleHint, where, size);

        ShowPage(0);
        Refresh();
    }

    public void OnShown() => Refresh();

    const int Columns = 3, PageSize = 9;
    readonly List<UniformGrid> _pages = new();
    readonly List<Border> _dots = new();

    /// <summary>Right from a page's last column goes to the next page, left from its first to the previous.</summary>
    void SteerPage(int p)
    {
        var chips = _pages[p].Children.OfType<Button>().ToList();
        for (int i = 0; i < chips.Count; i++)
        {
            int index = i, row = i / Columns, column = i % Columns;
            PanelInput.SetSteer(chips[i], command =>
            {
                int to = command switch
                {
                    NavCommand.Right when column == Columns - 1 || index == chips.Count - 1 => p + 1,
                    NavCommand.Left when column == 0 => p - 1,
                    _ => -1,
                };
                if (to < 0 || to >= _pages.Count) return null;
                ShowPage(to);
                var target = _pages[to].Children.OfType<Button>().ToList();
                int wanted = row * Columns + (to > p ? 0 : Columns - 1);
                return target[Math.Min(wanted, target.Count - 1)];
            });
        }
    }

    void ShowPage(int page)
    {
        for (int p = 0; p < _pages.Count; p++)
        {
            _pages[p].Visibility = p == page ? Visibility.Visible : Visibility.Hidden;
            _dots[p].Width = p == page ? 16 : 6;
            _dots[p].Background = Ui.Brush(p == page ? "BarFillBrush" : "TrackBrush");
        }
    }

    void Change(Action change)
    {
        change();
        _settings.SaveSoon();
        _overlay.Apply();
        Refresh();
        _changed();
    }

    void Refresh()
    {
        bool on = _settings.OverlayEnabled;
        Ui.SetOn(_toggle, on);
        _toggleHint.Visibility = on && _settings.OverlayItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (id, button) in _positions)
            button.Tag = id == _settings.OverlayPosition ? "selected" : null;
        foreach (var (id, button) in _items)
            button.Tag = _settings.OverlayItems.Contains(id) ? "selected" : null;
    }

    static Button Chip(string label) => new()
    {
        Style = Ui.Style("ChipButton"),
        MinHeight = 34,
        Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis },
    };
}
