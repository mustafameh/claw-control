using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Network;

/// <summary>
/// Wi-Fi sub screen: the on/off switch, then the networks in range (scanned each time it opens).
/// A joins a saved or open network directly; a new secured one needs a password, so it hands
/// over to Windows' own network list (which has a keyboard).
/// </summary>
public sealed class WifiScreen : UserControl, IPage
{
    readonly Connectivity _net;
    readonly Action _closePanel;
    readonly Button _switch;
    readonly ContentControl _list = new() { Focusable = false };
    readonly TextBlock _error;
    List<WifiNetwork> _networks = [];
    bool _shown;

    public string Id => "wifi";
    public string Title => "Wi-Fi";
    public FrameworkElement View => this;

    public WifiScreen(Connectivity net, Action closePanel)
    {
        _net = net;
        _closePanel = closePanel;
        _switch = Ui.Switch("Wi-Fi", () => _ = _net.SetRadio(wifi: true, on: _net.Last.WiFi == LinkState.Off));
        _error = new TextBlock { Style = Ui.Style("CardDetail"), Margin = new Thickness(8, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        Content = Ui.Stack(_switch, _list, _error);
        _net.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (!_shown) return;
            ShowState();
            if (_net.Last.WiFi != LinkState.Off && _networks.Count == 0) _ = Scan();
        });
    }

    public void OnShown()
    {
        _shown = true;
        _error.Visibility = Visibility.Collapsed;
        ShowState();
        _ = Scan();
    }

    public void OnHidden() => _shown = false;

    async Task Scan()
    {
        if (_net.Last.WiFi == LinkState.Off) return;
        // Kept for next time too, so the list shows at once while a new scan runs.
        _networks = await _net.ScanWifi();
        if (_shown) ShowState();
    }

    void ShowState()
    {
        bool on = _net.Last.WiFi != LinkState.Off;
        Ui.SetOn(_switch, on);
        string? connected = _net.Last.WiFi == LinkState.Connected ? _net.Last.Network : null;
        _list.Visibility = on && _networks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.Content = ListRow.Card(_networks.Select(n => Row(n with { Connected = n.Ssid == connected })));
    }

    FrameworkElement Row(WifiNetwork network)
    {
        // Right side: lock (needs a password), signal, check (connected).
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (network.Secured) right.Children.Add(Icon("", 13, 0.6));
        right.Children.Add(Icon(network.Bars switch { <= 1 => "", 2 => "", 3 => "", _ => "" }, 15, 1));
        var check = Icon("", 14, 1);
        check.Visibility = network.Connected ? Visibility.Visible : Visibility.Hidden;
        right.Children.Add(check);
        var line = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        line.Children.Add(right);
        var name = Ui.Text(network.Ssid, bold: network.Connected);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(4, 0, 0, 0);
        line.Children.Add(name);
        var button = new Button { Style = Ui.Style("RowButton"), Content = line };
        if (!network.Connected) PanelInput.SetHint(button, "Connect");
        button.Click += async (_, _) => await Join(network);
        return button;
    }

    static TextBlock Icon(string glyph, double size, double opacity)
    {
        var icon = Ui.Glyph(glyph, size);
        icon.Opacity = opacity;
        icon.Margin = new Thickness(10, 0, 0, 0);
        return icon;
    }

    async Task Join(WifiNetwork network)
    {
        if (network.Connected) return;
        if (network.Secured && !network.Saved)
        {
            // A password is needed: Windows' own list has a keyboard for it.
            _closePanel();
            Process.Start(new ProcessStartInfo("ms-availablenetworks:") { UseShellExecute = true });
            return;
        }
        _error.Visibility = Visibility.Collapsed;
        if (!await _net.ConnectWifi(network.Ssid))
        {
            _error.Text = $"Couldn't connect to {network.Ssid}.";
            _error.Visibility = Visibility.Visible;
        }
    }
}
