using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClawControl.Core;

namespace ClawControl.UI;

internal sealed class RepairNotice
{
    Window? _window;
    readonly TextBlock _title = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _detail = new() { FontSize = 15, Foreground = Brushes.WhiteSmoke, TextWrapping = TextWrapping.Wrap };
    readonly DispatcherTimer _dismiss = new() { Interval = TimeSpan.FromSeconds(8) };

    public RepairNotice()
    {
        _dismiss.Tick += (_, _) => { _dismiss.Stop(); _window?.Hide(); };
    }

    public void Show(string title, string detail, bool finished)
    {
        try
        {
            _dismiss.Stop();
            _window ??= CreateWindow();
            _title.Text = title;
            _detail.Text = detail;
            _detail.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            var area = ScreenFit.Primary().Work;
            _window.Left = ScreenFit.ToWindowUnits(_window, area.X + area.Width) - _window.Width - 18;
            _window.Top = ScreenFit.ToWindowUnits(_window, area.Y) + 18;
            _window.Show();
            if (finished) _dismiss.Start();
        }
        catch (Exception ex) { Log.Error("Showing button repair feedback", ex); }
    }

    Window CreateWindow()
    {
        var window = new Window
        {
            Title = "Claw Control button repair", Width = 360, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            Topmost = true, ShowInTaskbar = false, ShowActivated = false, Focusable = false,
            IsHitTestVisible = false, ResizeMode = ResizeMode.NoResize,
            Content = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(16),
                Background = new SolidColorBrush(Color.FromRgb(39, 41, 45)),
                Child = Ui.Stack(_title, _detail),
            },
        };
        var handle = new WindowInteropHelper(window).EnsureHandle();
        NativeWindow.MakeClickThrough(handle);
        return window;
    }
}
