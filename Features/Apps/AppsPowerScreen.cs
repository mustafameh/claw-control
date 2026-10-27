using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Apps;

/// <summary>
/// The Power menu (header chip or Y): Sleep, Hibernate, Restart and Shut down (Restart / Shut
/// down need a second A). Apps are in the app switcher left of the panel (AppSwitcher).
/// </summary>
public sealed class AppsPowerScreen : UserControl, IPage
{
    public static readonly Brush EndBrush = Ui.Hex("#FFFF453A");
    public static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(4);

    readonly Confirmations _confirm = new();

    public string Id => "apps-power";
    public string Title => "Power";
    public FrameworkElement View => this;

    public AppsPowerScreen()
    {
        var power = new UniformGrid { Rows = 1, Margin = new Thickness(4, 4, 4, 2) };
        power.Children.Add(PowerButton("", "Sleep", RunningApps.Sleep, confirm: false));
        if (Device.CanHibernate)  // hibernation can be switched off in Windows (powercfg /h off)
            power.Children.Add(PowerButton(Ui.LineIcon("HibernateIcon", 20), "Hibernate", RunningApps.Hibernate, confirm: false));
        power.Children.Add(PowerButton("", "Restart", RunningApps.Restart, confirm: true));
        power.Children.Add(PowerButton("", "Shut down", RunningApps.ShutDown, confirm: true));

        Content = power;
    }

    public void OnShown() => _confirm.Disarm();

    public void OnHidden() => _confirm.Disarm();

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
