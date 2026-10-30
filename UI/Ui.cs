using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ClawControl.Features.Controller;

namespace ClawControl.UI;

/// <summary>Small builders for code-built pages (Controller, Graphics), all on top of the Theme building blocks.</summary>
static class Ui
{
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static Style Style(string key) => (Style)Application.Current.FindResource(key);
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static SolidColorBrush Hex(string hex) => Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));
    static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    // Mode colours and icons, shared by the Controller tab and the Quick tab row.
    public static readonly Brush XboxBrush = Hex("#FF34C759"), PlayStationBrush = Hex("#FF0A84FF"),
        DesktopBrush = Hex("#FFFF9F0A"), OffBrush = Hex("#FF8E8E93"), GyroBrush = Hex("#FF5E5CE6"),
        ChangedBrush = Hex("#FFFF9F0A"), RowIconBrush = Hex("#FF636366");

    public static (Brush Color, string Name) Look(PadMode mode) => mode switch
    {
        PadMode.PlayStation => (PlayStationBrush, "PlayStation"),
        PadMode.Desktop => (DesktopBrush, "Desktop"),
        PadMode.Off => (OffBrush, "Off"),
        _ => (XboxBrush, "Xbox"),
    };

    /// <summary>The Xbox / PlayStation logo, a mouse for Desktop, a power sign for Off. Takes the text colour.</summary>
    public static FrameworkElement ModeIcon(PadMode mode, double size = 18) => mode switch
    {
        PadMode.Xbox => Logo("XboxLogo", size),
        PadMode.PlayStation => Logo("PlayStationLogo", size),
        PadMode.Desktop => Glyph("", size),
        _ => Glyph("", size),
    };

    static System.Windows.Shapes.Path Logo(string key, double size) => new()
    {
        Style = Style("Logo"),
        Data = (Geometry)Application.Current.FindResource(key),
        Width = size,
        Height = size,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Glyph(string glyph, double size = 18) => new()
    {
        Text = glyph,
        FontFamily = Icons,
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A line icon from Theme.xaml (24×24 geometry, LineIcon style) in the text colour.</summary>
    public static FrameworkElement LineIcon(string key, double size = 18) => new Viewbox
    {
        Width = size,
        Height = size,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new Canvas
        {
            Width = 24,
            Height = 24,
            Children = { new System.Windows.Shapes.Path { Style = Style("LineIcon"), Data = (Geometry)Application.Current.FindResource(key) } },
        },
    };

    /// <summary>A white icon on a small rounded colour square (the Controller tab's rows).</summary>
    public static Border IconSquare(FrameworkElement icon, Brush color)
    {
        TextElement.SetForeground(icon, Brushes.White);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        return new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = color, Child = icon,
            Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static TextBlock Text(string text, bool bold = false, bool subtle = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontSize = subtle ? 13 : 15,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Leave normal text unset so it follows its card (white, or dark on a selected card).
        if (subtle) block.Foreground = Brush("SubtleTextBrush");
        return block;
    }

    public static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    public static FillSlider Slider(string? glyph, string? label, double max, double step) => new()
    {
        Glyph = glyph, Label = label, Minimum = 0, Maximum = max, SmallChange = step,
    };

    /// <summary>A one-line row with an on/off switch; call SetOn to show its state.</summary>
    public static Button Switch(string text, Action onClick)
    {
        var button = new Button { Style = Style("SwitchButton"), Content = Text(text, bold: true) };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static void SetOn(Button button, bool on) => button.Tag = on ? "selected" : null;

    /// <summary>A row of chips where one is chosen (white).</summary>
    public static (UniformGrid Grid, Button[] Buttons) Segment(string[] labels, Action<int> onPick)
    {
        var grid = new UniformGrid { Rows = 1, Margin = new Thickness(1, 3, 1, 3) };
        var buttons = new Button[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            buttons[i] = new Button { Style = Style("ChipButton"), Content = Text(labels[i], bold: true) };
            buttons[i].Click += (_, _) => onPick(index);
            grid.Children.Add(buttons[i]);
        }
        return (grid, buttons);
    }

    /// <summary>Selects a control in the panel (the drop follows), e.g. after a list was rebuilt.</summary>
    public static void Select(FrameworkElement element)
    {
        element.Focus();
        if (Window.GetWindow(element) is { } window) FocusManager.SetFocusedElement(window, element);
        element.BringIntoView();
    }

    /// <summary>A card row: a name on the left, its chips (a Segment) on the right.</summary>
    public static FrameworkElement Labelled(string name, FrameworkElement chips)
    {
        var title = Text(name, bold: true);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(12, 0, 8, 0);
        chips.Width = 190;
        DockPanel.SetDock(chips, Dock.Right);
        return new DockPanel { MinHeight = 50, Children = { chips, title } };
    }

    public static void Choose(Button[] buttons, int index)
    {
        for (int i = 0; i < buttons.Length; i++) buttons[i].Tag = i == index ? "selected" : null;
    }

    /// <summary>A tile / card (CardButton) with any content, optionally in a colour.</summary>
    public static Button Card(UIElement content, Action onClick, double minHeight = 62)
    {
        var button = new Button { Style = Style("CardButton"), Content = content, MinHeight = minHeight };
        button.Click += (_, _) => onClick();
        return button;
    }
}
