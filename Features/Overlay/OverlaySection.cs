using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ClawControl.Core;
using ClawControl.Input;
using ClawControl.Monitoring;
using ClawControl.UI;

namespace ClawControl.Features.Overlay;

/// <summary>
/// Performance overlay controls, shown at the bottom of the Performance page: on/off, one of six
/// screen positions, and any combination of the numbers to show. Blue means on/chosen, so the
/// buttons carry no extra state text.
/// </summary>
public sealed class OverlaySection : StackPanel
{
    // Where each position's spot sits on the mini screen: row, column.
    static readonly (string Id, int Row, int Column)[] Positions =
    {
        ("TopLeft", 0, 0), ("TopCenter", 0, 1), ("TopRight", 0, 2),
        ("BottomLeft", 1, 0), ("BottomCenter", 1, 1), ("BottomRight", 1, 2),
    };

    readonly Settings _settings;
    readonly OverlayController _overlay;
    readonly Button _toggle;
    readonly TextBlock _toggleHint;
    readonly StackPanel _options = new();
    readonly Dictionary<string, Button> _positions = new();
    readonly Dictionary<string, Button> _items = new();

    public OverlaySection(Settings settings, OverlayController overlay)
    {
        _settings = settings;
        _overlay = overlay;

        // One tile: the switch row, and (while on) where it goes and what it shows.
        var tile = new StackPanel();
        Children.Add(new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("CardBrush"),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(2),
            Margin = new Thickness(4, 10, 4, 4),
            Child = tile,
        });
        // Only says something when the overlay is on but has nothing to show.
        _toggleHint = new TextBlock
        {
            Text = "Pick at least one number below",
            Style = (Style)FindResource("CardDetail"),
        };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = "",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 17,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        title.Children.Add(new TextBlock { Text = "Overlay", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        _toggle = new Button
        {
            Style = (Style)FindResource("SwitchButton"),
            Background = System.Windows.Media.Brushes.Transparent,
            Margin = new Thickness(0),
            Content = Stack(title, _toggleHint),
        };
        _toggle.Click += (_, _) => Change(() => _settings.OverlayEnabled = !_settings.OverlayEnabled);
        tile.Children.Add(_toggle);
        // Position and number choices only show while the overlay is on, to keep D-pad scrolling short.
        _options.Margin = new Thickness(4, 0, 4, 4);
        tile.Children.Add(_options);

        // Mini screen on the left (tap where the overlay goes), numbers on the right.
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(136) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        _options.Children.Add(layout);

        var spots = new Grid();
        for (int i = 0; i < 2; i++) spots.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < 3; i++) spots.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (id, row, column) in Positions)
        {
            var spot = new Button
            {
                Style = (Style)FindResource("SpotButton"),
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
            BorderBrush = (System.Windows.Media.Brush)FindResource("TrackBrush"),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(3, 3, 6, 3),
            Padding = new Thickness(3),
            Child = spots,
        });

        // Numbers in pages of 6 (3 × 2), so the tile is two rows tall: D-pad right past the last
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

        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        for (int p = 0; p < _pages.Count; p++)
        {
            int target = p;
            // A tap target around each dot (touch); not in the D-pad's way.
            var dot = new Border { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(3, 0, 3, 0) };
            var hit = new Border { Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(4, 6, 4, 6), Child = dot };
            hit.MouseLeftButtonUp += (_, _) => ShowPage(target);
            hit.TouchUp += (_, e) => { ShowPage(target); e.Handled = true; };
            _dots.Add(dot);
            dots.Children.Add(hit);
        }
        _options.Children.Add(dots);

        ShowPage(0);
        Refresh();
    }

    const int Columns = 3, PageSize = 6;
    readonly List<UniformGrid> _pages = new();
    readonly List<Border> _dots = new();
    int _page;

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
        _page = page;
        for (int p = 0; p < _pages.Count; p++)
        {
            _pages[p].Visibility = p == page ? Visibility.Visible : Visibility.Hidden;
            _dots[p].Width = p == page ? 16 : 6;
            _dots[p].Background = (System.Windows.Media.Brush)FindResource(p == page ? "BarFillBrush" : "TrackBrush");
        }
    }

    void Change(Action change)
    {
        change();
        _settings.SaveSoon();
        _overlay.Apply();
        Refresh();
    }

    void Refresh()
    {
        bool on = _settings.OverlayEnabled;
        _toggle.Tag = on ? "selected" : null;
        _toggleHint.Visibility = on && _settings.OverlayItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _options.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (id, button) in _positions)
            button.Tag = id == _settings.OverlayPosition ? "selected" : null;
        foreach (var (id, button) in _items)
            button.Tag = _settings.OverlayItems.Contains(id) ? "selected" : null;
    }

    Button Chip(string label) => new()
    {
        Style = (Style)FindResource("ChipButton"),
        MinHeight = 34,
        Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis },
    };

    static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
}
