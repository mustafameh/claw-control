using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClawControl.UI;

/// <summary>
/// A half-width Quick tab tile, like the Wi-Fi one: an icon and a name (A does the tile's job)
/// and a › that opens its screen. Lit in a colour while its thing is on.
/// </summary>
public sealed class QuickTile
{
    public readonly Border View;
    readonly TextBlock _title;

    public QuickTile(string glyph, string title, Action onPress, Action onMore)
    {
        var icon = Ui.Glyph(glyph, 17);
        icon.Margin = new Thickness(0, 0, 9, 0);
        _title = Ui.Text(title, bold: true);
        _title.FontSize = 14;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        var line = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        line.Children.Add(icon);
        line.Children.Add(_title);
        var main = new Button { Style = Ui.Style("RowButton"), Content = line, MinHeight = 54 };
        main.Click += (_, _) => onPress();
        var more = new Button { Style = Ui.Style("IconButton"), Content = "", FontSize = 12, Width = 30, Margin = new Thickness(0, 0, 2, 0) };
        more.Click += (_, _) => onMore();
        var tile = new DockPanel();
        DockPanel.SetDock(more, Dock.Right);
        tile.Children.Add(more);
        tile.Children.Add(main);
        View = new Border { CornerRadius = new CornerRadius(11), Margin = new Thickness(3), Child = tile, Background = Ui.Brush("CardBrush") };
    }

    public void Show(string title, Brush? lit)
    {
        _title.Text = title;
        View.Background = lit ?? Ui.Brush("CardBrush");
    }
}
