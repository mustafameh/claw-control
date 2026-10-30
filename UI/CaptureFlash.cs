using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClawControl.UI;

/// <summary>A screenshot was saved: the screen flashes white for a moment (no text, like a
/// camera). A click-through window that exists only for the flash.</summary>
static class CaptureFlash
{
    public static void Show()
    {
        var screen = ScreenFit.Primary();
        var window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.White, Opacity = 0,
            Topmost = true, ShowInTaskbar = false, ShowActivated = false, Focusable = false, IsHitTestVisible = false,
            ResizeMode = ResizeMode.NoResize, Left = -10000, Top = -10000, Width = 1, Height = 1,
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            // Click-through, never takes focus, not in Alt + Tab.
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x20 | 0x80 | 0x08000000);
        };
        window.Show();
        window.Left = ScreenFit.ToWindowUnits(window, screen.Bounds.X);
        window.Top = ScreenFit.ToWindowUnits(window, screen.Bounds.Y);
        window.Width = ScreenFit.ToWindowUnits(window, screen.Bounds.Width);
        window.Height = ScreenFit.ToWindowUnits(window, screen.Bounds.Height);
        var flash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(380) };
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0.32, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)), new QuadraticEase()));
        flash.Completed += (_, _) => window.Close();
        window.BeginAnimation(UIElement.OpacityProperty, flash);
    }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
