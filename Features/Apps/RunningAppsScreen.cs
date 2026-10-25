using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Apps;

/// <summary>
/// Running apps (from Apps &amp; power): every app with a window, biggest first, with its memory
/// (or "Not responding"); A ends it. Close background apps ends all but the app in front
/// (second A). Read each time it opens; the last list shows at once meanwhile.
/// </summary>
public sealed class RunningAppsScreen : UserControl, IPage
{
    readonly ContentControl _list = new() { Focusable = false };
    readonly Button _closeAll;
    readonly TextBlock _closeAllText = Ui.Text("Close background apps", bold: true);
    readonly Confirmations _confirm = new();
    List<RunningApp> _apps = [];

    public string Id => "running-apps";
    public string Title => "Running apps";
    public FrameworkElement View => this;

    /// <summary>The app that was in front when the panel opened (kept by Close background apps).</summary>
    public RunningApp? Front { get; set; }

    /// <summary>How many apps are open (Apps &amp; power shows it).</summary>
    public event Action<int>? CountChanged;

    public RunningAppsScreen()
    {
        _closeAllText.HorizontalAlignment = HorizontalAlignment.Center;
        _closeAll = new Button
        {
            Style = Ui.Style("CardButton"), Tag = "selected", MinHeight = 48, Margin = new Thickness(4, 8, 4, 4),
            HorizontalContentAlignment = HorizontalAlignment.Center, Content = _closeAllText,
            Visibility = Visibility.Collapsed,  // until the first list is in
        };
        PanelInput.SetHint(_closeAll, "End all");
        _confirm.Add(_closeAll, _closeAllText, () => "Close background apps", "Press A again to close them");
        _closeAll.Click += async (_, _) =>
        {
            if (!_confirm.Confirm(_closeAll)) return;
            var background = _apps.Where(a => a.ProcessId != Front?.ProcessId).ToList();
            await Task.WhenAll(background.Select(a => RunningApps.Close(a, AppsPowerScreen.CloseWait)));
            Load();
        };
        Content = Ui.Stack(_list, _closeAll);
    }

    public void OnShown()
    {
        _confirm.Disarm();
        Load();
    }

    public void OnHidden() => _confirm.Disarm();

    public async void Load()
    {
        _apps = await Task.Run(RunningApps.List);
        var rows = _apps.Select(Row).ToList();
        _list.Content = rows.Count > 0 ? ListRow.Card(rows, 50) : null;
        // First opening: the list arrives after the screen did, with nothing selected yet.
        if (rows.Count > 0 && IsVisible && Window.GetWindow(this) is { } window)
        {
            var focused = FocusManager.GetFocusedElement(window) as FrameworkElement;
            if (focused == null || focused == window || !focused.IsVisible)
            {
                rows[0].Focus();
                FocusManager.SetFocusedElement(window, rows[0]);
            }
        }
        _closeAll.Visibility = _apps.Any(a => a.ProcessId != Front?.ProcessId) ? Visibility.Visible : Visibility.Collapsed;
        CountChanged?.Invoke(_apps.Count);
    }

    FrameworkElement Row(RunningApp app)
    {
        var icon = new Image { Source = app.Icon, Width = 26, Height = 26, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock
        {
            Text = app.NotResponding ? "Not responding" : Size(app.MemoryBytes), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Foreground = app.NotResponding ? AppsPowerScreen.EndBrush : Ui.Brush("SubtleTextBrush"), Margin = new Thickness(8, 0, 0, 0),
        };
        var name = Ui.Text(app.Name, bold: app.ProcessId == Front?.ProcessId);
        name.VerticalAlignment = VerticalAlignment.Center;
        var line = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(value, Dock.Right);
        line.Children.Add(icon);
        line.Children.Add(value);
        line.Children.Add(name);
        var button = new Button { Style = Ui.Style("RowButton"), Content = line };
        PanelInput.SetHint(button, "End");
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            await RunningApps.Close(app, AppsPowerScreen.CloseWait);
            Load();
        };
        return button;
    }

    static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes >> 20} MB";
}
