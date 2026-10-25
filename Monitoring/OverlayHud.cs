using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClawControl.UI;

namespace ClawControl.Monitoring;

/// <summary>
/// The on-screen numbers. A see-through, click-through, always-on-top window (the same kind MSI's
/// real-time monitor uses) on its own UI thread, so drawing it can never slow the panel down and
/// a busy panel can never freeze it. Corners stack the numbers; top and bottom centre lay them in a row.
/// </summary>
public sealed class OverlayHud : IDisposable
{
    const double ScreenMargin = 12;

    static readonly Brush PanelBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x12, 0x16)));
    static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)));
    static readonly Brush ValueBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8)));
    static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");

    readonly Thread _thread;
    Dispatcher? _dispatcher;
    Window? _window;
    Border? _panel;
    readonly List<TextBlock> _values = new();
    string[] _labels = Array.Empty<string>();
    string _position;

    public OverlayHud(string position)
    {
        _position = position;
        _thread = new Thread(Run) { IsBackground = true, Name = "Overlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Called from the metrics thread. Readings that arrive before the window exists are skipped.</summary>
    public void Update(IReadOnlyList<MetricReading> readings) =>
        _dispatcher?.BeginInvoke(() => Show(readings));

    public void SetPosition(string position)
    {
        _position = position;
        _dispatcher?.BeginInvoke(() =>
        {
            _labels = Array.Empty<string>();  // relayout: corners stack, centres use a row
            Place();
        });
    }

    public void Dispose()
    {
        lock (_startGate)
        {
            _disposed = true;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            // Not started yet: Run() sees the flag and shuts down by itself.
            _dispatcher?.BeginInvoke(() =>
            {
                _window?.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            });
        }
    }

    readonly object _startGate = new();
    bool _disposed;

    void Run()
    {
        _panel = new Border { Background = PanelBrush, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6) };
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            IsHitTestVisible = false,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = _panel,
            Left = -10000,
            Top = -10000,
        };
        _window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            // Click-through, never takes focus, kept out of Alt+Tab.
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        _window.SizeChanged += (_, _) => Place();
        _window.DpiChanged += (_, _) => Place();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        _window.Show();
        lock (_startGate)
        {
            if (_disposed)
            {
                _window.Close();
                return;
            }
            _dispatcher = Dispatcher.CurrentDispatcher;
        }
        Dispatcher.Run();
    }

    /// <summary>A new resolution moves the screen's edges and its share.</summary>
    void OnDisplayChanged(object? sender, EventArgs e) => _dispatcher?.BeginInvoke(Place);

    void Show(IReadOnlyList<MetricReading> readings)
    {
        if (_window == null || _panel == null) return;
        var labels = readings.Select(r => r.Label).ToArray();
        if (!labels.SequenceEqual(_labels)) Rebuild(labels);
        for (int i = 0; i < readings.Count; i++)
            if (_values[i].Text != readings[i].Value) _values[i].Text = readings[i].Value;
        KeepOnTop();
    }

    void Rebuild(string[] labels)
    {
        _labels = labels;
        _values.Clear();
        bool row = _position is "TopCenter" or "BottomCenter";
        var grid = new Grid();
        for (int i = 0; i < labels.Length; i++)
        {
            var label = Text(labels[i], LabelBrush, FontWeights.Normal);
            var value = Text("–", ValueBrush, FontWeights.SemiBold);
            _values.Add(value);
            if (row)
            {
                // label value · label value · ...
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                label.Margin = new Thickness(i == 0 ? 0 : 14, 0, 5, 0);
                Grid.SetColumn(label, i * 2);
                Grid.SetColumn(value, i * 2 + 1);
            }
            else
            {
                // A column of labels and a column of values, so the numbers line up.
                if (i == 0)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                }
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                label.Margin = new Thickness(0, 0, 10, 0);
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
            }
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        _panel!.Child = grid;
    }

    static TextBlock Text(string text, Brush brush, FontWeight weight) =>
        new() { Text = text, Foreground = brush, FontFamily = Font, FontSize = 14, FontWeight = weight };

    /// <summary>In the screen's real pixels and a fixed share of it, like the panel (UI/ScreenFit).</summary>
    void Place()
    {
        if (_window == null || _panel == null || _window.ActualWidth == 0) return;
        var screen = ScreenFit.Primary();
        double scale = ScreenFit.Scale(_window, screen);
        if (_panel.LayoutTransform is not ScaleTransform zoom || Math.Abs(zoom.ScaleX - scale) > 0.001)
        {
            _panel.LayoutTransform = new ScaleTransform(scale, scale);
            _window.UpdateLayout();  // the window's new size
        }
        double px = VisualTreeHelper.GetDpi(_window).DpiScaleX, margin = ScreenMargin * screen.UnitPx;
        double width = _window.ActualWidth * px, height = _window.ActualHeight * px;
        var bounds = screen.Bounds;
        double left = _position.EndsWith("Left") ? bounds.X + margin
            : _position.EndsWith("Right") ? bounds.X + bounds.Width - width - margin
            : bounds.X + (bounds.Width - width) / 2;
        double top = _position.StartsWith("Top") ? bounds.Y + margin : bounds.Y + bounds.Height - height - margin;
        _window.Left = ScreenFit.ToWindowUnits(_window, left);
        _window.Top = ScreenFit.ToWindowUnits(_window, top);
    }

    /// <summary>Full-screen games and other always-on-top windows can cover us; step back in front.</summary>
    void KeepOnTop()
    {
        var hwnd = new WindowInteropHelper(_window!).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
