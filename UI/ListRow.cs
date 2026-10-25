using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClawControl.UI;

/// <summary>
/// A row of a grouped card (iOS Settings style): colour square, name, value on the right, then
/// either a switch (A flips it; the › beside the row opens its screen) or a › (A opens the
/// screen). Without an icon it's a plain choice row (pickers), where <see cref="Check"/> marks
/// the chosen one.
/// </summary>
public sealed class ListRow
{
    public readonly Button Button;
    public readonly Button? More;
    public readonly TextBlock Value = Ui.Text("", subtle: true);
    public readonly FrameworkElement View;
    readonly TextBlock? _check;

    public ListRow(FrameworkElement? icon, Brush? color, string name, bool withSwitch = false, bool chevron = true)
    {
        var line = new DockPanel();
        if (icon != null)
        {
            var square = Ui.IconSquare(icon, color ?? Ui.RowIconBrush);
            DockPanel.SetDock(square, Dock.Left);
            line.Children.Add(square);
        }
        if (!withSwitch)
        {
            // A grey › for rows that open a screen; a check mark (in the text colour) for choices.
            var end = Ui.Glyph(chevron ? "" : "", chevron ? 12 : 15);
            end.Margin = new Thickness(10, 0, 0, 0);
            if (chevron) end.Foreground = Ui.Brush("SubtleTextBrush");
            else
            {
                _check = end;
                end.Visibility = Visibility.Hidden;
            }
            DockPanel.SetDock(end, Dock.Right);
            line.Children.Add(end);
        }
        Value.FontSize = 14;
        Value.VerticalAlignment = VerticalAlignment.Center;
        Value.Margin = new Thickness(8, 0, withSwitch ? 10 : 0, 0);
        DockPanel.SetDock(Value, Dock.Right);
        line.Children.Add(Value);
        var title = Ui.Text(name, bold: true);
        title.VerticalAlignment = VerticalAlignment.Center;
        if (icon == null) title.Margin = new Thickness(4, 0, 0, 0);
        line.Children.Add(title);

        Button = new Button { Style = Ui.Style(withSwitch ? "RowSwitchButton" : "RowButton"), Content = line };
        if (!withSwitch)
        {
            View = Button;
            return;
        }
        More = new Button { Style = Ui.Style("IconButton"), Content = "", FontSize = 12, Margin = new Thickness(0, 0, 4, 0) };
        var row = new DockPanel();
        DockPanel.SetDock(More, Dock.Right);
        row.Children.Add(More);
        row.Children.Add(Button);
        View = row;
    }

    /// <summary>Choice rows: shows or hides the check mark.</summary>
    public bool Check
    {
        set { if (_check != null) _check.Visibility = value ? Visibility.Visible : Visibility.Hidden; }
    }

    /// <summary>Rows in one rounded card, split by hairlines (starting after the icons, if any).</summary>
    public static Border Card(params ListRow[] rows)
    {
        bool icons = rows.Length > 0 && rows[0].Button.Content is DockPanel { Children.Count: > 0 } line && line.Children[0] is Border;
        return Card(rows.Select(r => r.View), icons ? 56 : 14);
    }

    /// <summary>Any rows (RowButton style) in one rounded card, split by hairlines.</summary>
    public static Border Card(IEnumerable<FrameworkElement> rows, double indent = 14)
    {
        var stack = new StackPanel();
        foreach (var row in rows)
        {
            if (stack.Children.Count > 0)
                stack.Children.Add(new Border { Height = 1, Background = Ui.Brush("TrackBrush"), Margin = new Thickness(indent, 0, 14, 0) });
            stack.Children.Add(row);
        }
        return new Border
        {
            Background = Ui.Brush("CardBrush"), CornerRadius = new CornerRadius(16),
            Padding = new Thickness(2), Margin = new Thickness(4, 6, 4, 4), Child = stack,
        };
    }
}
