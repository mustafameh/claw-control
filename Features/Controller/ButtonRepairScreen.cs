using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Controller;

internal sealed class ButtonRepairScreen : UserControl, IPage
{
    readonly ControllerService _pad;
    readonly Action _close;
    readonly TextBlock _status = Ui.Text("");
    readonly UniformGrid _choices = new() { Columns = 2 };
    readonly TextBlock _result = Ui.Text("");
    readonly TextBlock _applyLabel = Ui.Text("", bold: true);
    readonly Button _apply, _scan, _choose;
    bool _manual, _waiting;
    readonly HashSet<PanelButton> _selected = new();
    int _scanVersion;

    public string Id => "controller-button-repair";
    public string Title => "Repair game buttons";
    public FrameworkElement View => this;

    public ButtonRepairScreen(ControllerService pad, Action close)
    {
        _pad = pad;
        _close = close;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(6);
        _result.TextWrapping = TextWrapping.Wrap;
        _result.Margin = new Thickness(6);
        _apply = Ui.Card(_applyLabel, ConfirmRepair);
        _scan = Ui.Card(Ui.Text("Scan again"), () => _ = ScanAsync());
        _choose = Ui.Card(Ui.Text("Choose buttons manually", bold: true), ChooseManually);
        Content = Ui.Stack(_result, _status, _choices, _apply, _choose, _scan);
        pad.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (_waiting && !pad.RepairBusy) _ = ScanAsync();
        });
    }

    public void OnShown() => _ = ScanAsync();
    public void OnHidden() { _scanVersion++; _waiting = false; }

    void ConfirmRepair()
    {
        if (_selected.Count == 0 || _pad.RepairBusy) return;
        _apply.IsEnabled = false;
        _pad.QueueButtonRepair(_selected.ToArray(), _manual);
        _close();
    }

    async Task ScanAsync()
    {
        int version = ++_scanVersion;
        _waiting = false;
        _result.Text = _pad.RepairDetail;
        _result.Visibility = _result.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _manual = false;
        _choose.Visibility = Visibility.Collapsed;
        _choices.Children.Clear();
        _selected.Clear();
        _apply.Visibility = Visibility.Collapsed;
        _apply.IsEnabled = false;
        _scan.IsEnabled = false;
        _status.Text = "Checking game button mappings…";
        try
        {
            var buttons = await _pad.ScanPanelMappingsAsync();
            if (version != _scanVersion) return;
            if (_pad.RepairBusy)
            {
                _waiting = true;
                _status.Text = "Waiting for the controller…";
                return;
            }
            _result.Text = _pad.RepairDetail;
            _result.Visibility = _result.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _choose.Visibility = Visibility.Visible;
            if (buttons.Length == 0)
            {
                _status.Text = "No buttons appear stuck in panel mode. If a button still behaves incorrectly in games, you can restore its default mapping manually.";
                return;
            }
            _status.Text = "Select the buttons to repair, then press Restore. Going back or closing this panel cancels your selection.";
            ShowChoices(buttons);
        }
        catch (Exception ex)
        {
            Core.Log.Error("Scanning game buttons", ex);
            if (version == _scanVersion) _status.Text = "Couldn’t read the controller. Reconnect it and scan again.";
        }
        finally
        {
            if (version == _scanVersion) _scan.IsEnabled = true;
        }
    }

    void ChooseManually()
    {
        _manual = true;
        _choose.Visibility = Visibility.Collapsed;
        _status.Text = "Select buttons, then press Restore to clear their custom mapping and turbo. Going back or closing this panel cancels your selection.";
        ShowChoices(PanelButton.All);
        ((Button)_choices.Children[0]).Focus();
    }

    void ShowChoices(PanelButton[] buttons)
    {
        _choices.Children.Clear();
        _selected.Clear();
        _apply.IsEnabled = false;
        _applyLabel.Text = "Restore selected buttons";
        foreach (var button in buttons)
        {
            var row = new ListRow(null, null, button.Name, chevron: false);
            row.Button.Click += (_, _) =>
            {
                if (!_selected.Add(button)) _selected.Remove(button);
                row.Check = _selected.Contains(button);
                int count = _selected.Count;
                _apply.IsEnabled = count > 0;
                _applyLabel.Text = count == 0 ? "Restore selected buttons"
                    : $"Restore {count} {(count == 1 ? "button" : "buttons")} and close panel";
            };
            _choices.Children.Add(row.Button);
        }
        _apply.Visibility = Visibility.Visible;
    }
}
