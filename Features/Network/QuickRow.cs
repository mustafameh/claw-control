using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;
using ClawControl.Features.Controller;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Network;

/// <summary>
/// The Quick tab's top row: one circle that flips the controller between Xbox and mouse
/// (Desktop) mode, then Wi-Fi and Bluetooth tiles. A on a tile turns that radio on or off; its ›
/// opens the list (networks / paired devices). A connected tile lights up and shows the network
/// or device name (and the device's battery); the icons say the rest.
/// </summary>
public sealed class QuickRow : UserControl
{
    static readonly Brush ConnectedBrush = Ui.Hex("#FF0A84FF");

    readonly ControllerService _pad;
    readonly Connectivity _net;
    readonly Button _mode = new() { Tag = "selected" };

    /// <summary>The mode circle (Xbox / mouse): where the Quick tab opens.</summary>
    public FrameworkElement Mode => _mode;
    readonly Tile _wifi, _bluetooth;

    public QuickRow(ControllerService pad, Connectivity net, IPage wifiScreen, IPage bluetoothScreen, Action<IPage> openSubPage)
    {
        _pad = pad;
        _net = net;
        _mode.Style = Ui.Style("ModeButton");
        _mode.Click += (_, _) => _pad.SetMode(_pad.Mode == PadMode.Xbox ? PadMode.Desktop : PadMode.Xbox);
        _wifi = new Tile("", () => _ = _net.SetRadio(wifi: true, on: _net.Last.WiFi == LinkState.Off), () => openSubPage(wifiScreen));
        _bluetooth = new Tile("", () => _ = _net.SetRadio(wifi: false, on: _net.Last.Bluetooth == LinkState.Off), () => openSubPage(bluetoothScreen));

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(_mode);
        Grid.SetColumn(_wifi.View, 1);
        grid.Children.Add(_wifi.View);
        Grid.SetColumn(_bluetooth.View, 2);
        grid.Children.Add(_bluetooth.View);
        Content = grid;

        _pad.Changed += () => Dispatcher.BeginInvoke(Refresh);
        _net.Changed += () => Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    void Refresh()
    {
        var (color, _) = Ui.Look(_pad.Mode);
        _mode.Background = color;
        _mode.Content = Ui.ModeIcon(_pad.Mode, 20);

        var net = _net.Last;
        _wifi.Show(net.WiFi, net.WiFi == LinkState.Connected ? net.Network : null, null, net.WiFi == LinkState.Connected ? net.Bars : -1, "Wi-Fi");
        _bluetooth.Show(net.Bluetooth, net.Bluetooth == LinkState.Connected ? net.Device : null, net.DeviceBattery, -1, "Bluetooth");
    }

    /// <summary>A rounded tile: the main part (A flips the radio) and a › (opens the list).</summary>
    sealed class Tile
    {
        public readonly Border View;
        readonly TextBlock _icon, _bars, _title, _battery;
        readonly Path _slash;
        readonly FrameworkElement _batteryLine;
        readonly Border _batteryFill = new() { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(1), Background = Ui.Brush("TextBrush") };

        public Tile(string glyph, Action onToggle, Action onMore)
        {
            _icon = new TextBlock { Text = glyph, FontFamily = Ui.Icons, FontSize = 17, VerticalAlignment = VerticalAlignment.Center };
            _bars = new TextBlock { FontFamily = Ui.Icons, FontSize = 17, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            _slash = new Path
            {
                Data = Geometry.Parse("M0.5,0.5 L16.5,16.5"), Stroke = Ui.Brush("TextBrush"), StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Visibility = Visibility.Collapsed,
            };
            var icon = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0), Children = { _icon, _bars, _slash } };

            _title = Ui.Text("", bold: true);
            _title.FontSize = 14;
            _battery = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            var outline = new Border
            {
                Width = 17, Height = 9, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1.2),
                BorderBrush = Ui.Brush("TextBrush"), Padding = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
                Child = _batteryFill,
            };
            _batteryLine = new StackPanel { Orientation = Orientation.Horizontal, Children = { outline, _battery }, Visibility = Visibility.Collapsed };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _batteryLine } };

            var line = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            line.Children.Add(icon);
            line.Children.Add(text);
            var main = new Button { Style = Ui.Style("RowButton"), Content = line, MinHeight = 54 };
            main.Click += (_, _) => onToggle();
            var more = new Button { Style = Ui.Style("IconButton"), Content = "", FontSize = 12, Width = 30, Margin = new Thickness(0, 0, 2, 0) };
            more.Click += (_, _) => onMore();

            var tile = new DockPanel();
            DockPanel.SetDock(more, Dock.Right);
            tile.Children.Add(more);
            tile.Children.Add(main);
            View = new Border { CornerRadius = new CornerRadius(16), Margin = new Thickness(3), Child = tile };
        }

        /// <summary>bars ≥ 0: Wi-Fi signal (bright bars over a faint full icon).</summary>
        public void Show(LinkState state, string? name, int? battery, int bars, string label)
        {
            bool connected = state == LinkState.Connected;
            View.Background = connected ? ConnectedBrush : Ui.Brush("CardBrush");
            _title.Text = connected && !string.IsNullOrEmpty(name) ? name : label;
            _icon.Opacity = connected && bars < 0 ? 1 : 0.4;
            _bars.Visibility = connected && bars >= 0 ? Visibility.Visible : Visibility.Collapsed;
            _bars.Text = bars switch { <= 1 => "", 2 => "", 3 => "", _ => "" };
            _slash.Visibility = state == LinkState.Off ? Visibility.Visible : Visibility.Collapsed;
            _slash.Opacity = 0.4;
            _batteryLine.Visibility = connected && battery != null ? Visibility.Visible : Visibility.Collapsed;
            _battery.Text = $"{battery}%";
            _batteryFill.Width = Math.Max(1, 12.6 * (battery ?? 0) / 100.0);  // inner width of the outline
        }
    }
}
