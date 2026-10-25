using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Apps;

/// <summary>
/// The Apps &amp; power menu (header chip or Y): one card with "End [app in front]" (red icon;
/// first A arms, second ends) and "Running apps" (opens <see cref="RunningAppsScreen"/>), then
/// Sleep, Hibernate, Restart and Shut down (Restart / Shut down need a second A).
/// </summary>
public sealed class AppsPowerScreen : UserControl, IPage
{
    public static readonly Brush EndBrush = Ui.Hex("#FFFF453A");
    public static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(4);

    readonly Button _end;
    readonly TextBlock _endText = Ui.Text("", bold: true);
    readonly ListRow _running;
    readonly Confirmations _confirm = new();
    RunningApp? _front;

    public string Id => "apps-power";
    public string Title => "Apps & power";
    public FrameworkElement View => this;

    /// <summary>The list of open apps, a screen of its own.</summary>
    public RunningAppsScreen Apps { get; } = new();

    public AppsPowerScreen(Action<IPage> openSubPage)
    {
        // End: a row like the others, only its icon is red.
        _endText.VerticalAlignment = VerticalAlignment.Center;
        var endLine = new DockPanel();
        var square = Ui.IconSquare(Ui.Glyph("", 14), EndBrush);
        DockPanel.SetDock(square, Dock.Left);
        endLine.Children.Add(square);
        endLine.Children.Add(_endText);
        _end = new Button { Style = Ui.Style("RowButton"), Content = endLine };
        PanelInput.SetHint(_end, "End");
        _confirm.Add(_end, _endText, () => $"End {_front?.Name}", "Press A again to end");
        _end.Click += async (_, _) =>
        {
            if (_front == null || !_confirm.Confirm(_end)) return;
            var app = _front;
            _front = null;
            ShowFront();
            await RunningApps.Close(app, CloseWait);
        };

        _running = new ListRow(Ui.Glyph("", 15), Ui.RowIconBrush, "Running apps");
        _running.Button.Click += (_, _) => openSubPage(Apps);
        Apps.CountChanged += count => _running.Value.Text = count > 0 ? count.ToString() : "";

        var power = new UniformGrid { Rows = 1, Margin = new Thickness(4, 12, 4, 2) };
        power.Children.Add(PowerButton("", "Sleep", RunningApps.Sleep, confirm: false));
        power.Children.Add(PowerButton(Ui.LineIcon("HibernateIcon", 20), "Hibernate", RunningApps.Hibernate, confirm: false));
        power.Children.Add(PowerButton("", "Restart", RunningApps.Restart, confirm: true));
        power.Children.Add(PowerButton("", "Shut down", RunningApps.ShutDown, confirm: true));

        var card = ListRow.Card(new FrameworkElement[] { _end, _running.View }, 56);
        _endDivider = ((Panel)card.Child).Children[1];
        Content = Ui.Stack(card, power);
    }

    readonly UIElement _endDivider;

    /// <summary>The app that was in front when the panel opened (set by the panel).</summary>
    public RunningApp? Front
    {
        get => _front;
        set
        {
            _front = value;
            Apps.Front = value;
            ShowFront();
        }
    }

    public void OnShown()
    {
        _confirm.Disarm();
        ShowFront();
        Apps.Load();  // for the count
    }

    public void OnHidden() => _confirm.Disarm();

    void ShowFront()
    {
        _endText.Text = $"End {_front?.Name}";
        // With nothing in front the card is just Running apps.
        _end.Visibility = _endDivider.Visibility = _front != null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A round power button with its name under it (the name asks for a second A when armed).</summary>
    FrameworkElement PowerButton(object icon, string label, Action action, bool confirm)
    {
        var circle = new Button { Style = Ui.Style("ModeButton"), Content = icon };
        PanelInput.SetHint(circle, label);
        var caption = new TextBlock { Text = label, Style = Ui.Style("CardDetail"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        if (confirm) _confirm.Add(circle, caption, () => label, "Press A again");
        circle.Click += (_, _) => { if (!confirm || _confirm.Confirm(circle)) action(); };
        return Ui.Stack(circle, caption);
    }
}

/// <summary>
/// Buttons that need a second A within 4 s: the first press swaps their label for a "press A
/// again" one, the second confirms; anything else (or the timeout) puts the label back.
/// </summary>
sealed class Confirmations
{
    readonly Dictionary<Button, (TextBlock Label, Func<string> Text, string Armed)> _buttons = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    Button? _armed;

    public Confirmations() => _timer.Tick += (_, _) => Disarm();

    public void Add(Button button, TextBlock label, Func<string> text, string armed) => _buttons[button] = (label, text, armed);

    public bool Confirm(Button button)
    {
        if (_armed == button)
        {
            Disarm();
            return true;
        }
        Disarm();
        _armed = button;
        if (_buttons.TryGetValue(button, out var b)) b.Label.Text = b.Armed;
        _timer.Start();
        return false;
    }

    public void Disarm()
    {
        _timer.Stop();
        if (_armed != null && _buttons.TryGetValue(_armed, out var b)) b.Label.Text = b.Text();
        _armed = null;
    }
}
