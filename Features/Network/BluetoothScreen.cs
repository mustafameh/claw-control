using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Network;

/// <summary>
/// Bluetooth sub screen: the on/off switch, then paired devices (connected first, with their
/// battery). A connects or disconnects headphones and speakers; for other devices (controllers,
/// mice) Windows has no connect call, so A opens Windows' Bluetooth settings.
/// </summary>
public sealed class BluetoothScreen : UserControl, IPage
{
    readonly Connectivity _net;
    readonly Action _closePanel;
    readonly Button _switch;
    readonly ContentControl _list = new() { Focusable = false };
    readonly TextBlock _error;
    List<PairedDevice> _devices = [];
    bool _shown, _busy;

    public string Id => "bluetooth";
    public string Title => "Bluetooth";
    public FrameworkElement View => this;

    public BluetoothScreen(Connectivity net, Action closePanel)
    {
        _net = net;
        _closePanel = closePanel;
        _switch = Ui.Switch("Bluetooth", () => _ = _net.SetRadio(wifi: false, on: _net.Last.Bluetooth == LinkState.Off));
        _error = new TextBlock { Style = Ui.Style("CardDetail"), Margin = new Thickness(8, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        Content = Ui.Stack(_switch, _list, _error);
        _net.Changed += () => Dispatcher.BeginInvoke(() => { if (_shown) _ = Load(); });
    }

    public void OnShown()
    {
        _shown = true;
        _error.Visibility = Visibility.Collapsed;
        ShowState();
        _ = Load();
    }

    public void OnHidden() => _shown = false;

    async Task Load()
    {
        // Kept for next time too, so the list shows at once.
        _devices = _net.Last.Bluetooth == LinkState.Off ? [] : await _net.PairedDevices();
        if (_shown) ShowState();
    }

    void ShowState()
    {
        bool on = _net.Last.Bluetooth != LinkState.Off;
        Ui.SetOn(_switch, on);
        _list.Visibility = on && _devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.Content = ListRow.Card(_devices.Select(Row));
    }

    FrameworkElement Row(PairedDevice device)
    {
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (device.Connected && device.Battery is int battery)
            right.Children.Add(new TextBlock { Text = $"{battery}%", FontSize = 13, Foreground = Ui.Brush("SubtleTextBrush"), VerticalAlignment = VerticalAlignment.Center });
        var check = Ui.Glyph("", 14);
        check.Margin = new Thickness(10, 0, 0, 0);
        check.Visibility = device.Connected ? Visibility.Visible : Visibility.Hidden;
        right.Children.Add(check);
        var line = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        line.Children.Add(right);
        var name = Ui.Text(device.Name, bold: device.Connected);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(4, 0, 0, 0);
        line.Children.Add(name);
        var button = new Button { Style = Ui.Style("RowButton"), Content = line };
        PanelInput.SetHint(button, device.Connected ? "Disconnect" : "Connect");
        button.Click += async (_, _) => await Toggle(device);
        return button;
    }

    async Task Toggle(PairedDevice device)
    {
        if (_busy) return;
        if (!device.Audio)
        {
            _closePanel();
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
            return;
        }
        _busy = true;
        _error.Visibility = Visibility.Collapsed;
        bool done = await _net.SetDeviceConnected(device.Container, !device.Connected);
        _busy = false;
        if (!done)
        {
            _error.Text = $"Couldn't {(device.Connected ? "disconnect" : "connect")} {device.Name}.";
            _error.Visibility = Visibility.Visible;
        }
        await Load();
    }
}
