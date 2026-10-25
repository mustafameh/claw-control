using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ClawControl.Core;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace ClawControl.UI;

/// <summary>
/// The panel's glass: a rounded, blurred copy of whatever is behind it, drawn by Windows'
/// compositor on the GPU in a separate click-through window right under the panel.
///
/// Why a separate window: Windows' built-in window backdrops (acrylic/Mica) are always square
/// and ignore window shapes, and its own corner rounding is small. This window has no surface
/// of its own (WS_EX_NOREDIRECTIONBITMAP), so it's clear everywhere except the rounded blur.
/// The panel on top draws its own matching rounded edge and tint. Only shown with the panel.
/// </summary>
public sealed class GlassBackdrop
{
    readonly HwndSource _window;
    readonly Compositor? _compositor;
    readonly object? _target;           // keeps the composition target alive
    readonly ContainerVisual? _root;
    readonly SpriteVisual? _blur;
    readonly CompositionRoundedRectangleGeometry? _shape;
    readonly double _radius;

    public IntPtr Handle => _window.Handle;

    public GlassBackdrop(double radiusDip)
    {
        _radius = radiusDip;
        _window = new HwndSource(new HwndSourceParameters("Claw Control glass")
        {
            WindowStyle = unchecked((int)0x80000000),  // WS_POPUP
            // No surface of its own | never activates | not in Alt+Tab | clicks pass through | on top
            ExtendedWindowStyle = 0x00200000 | 0x08000000 | 0x80 | 0x20 | 0x8,
            Width = 1,
            Height = 1,
        });
        try
        {
            int on = 1;
            DwmSetWindowAttribute(_window.Handle, DWMWA_USE_HOSTBACKDROPBRUSH, ref on, sizeof(int));
            EnsureDispatcherQueue();
            _compositor = new Compositor();
            var interop = (ICompositorDesktopInterop)Marshal.GetObjectForIUnknown(((IWinRTObject)_compositor).NativeObject.ThisPtr);
            interop.CreateDesktopWindowTarget(_window.Handle, false, out IntPtr targetPtr);
            var target = DesktopWindowTarget.FromAbi(targetPtr);
            _target = target;

            _root = _compositor.CreateContainerVisual();
            _shape = _compositor.CreateRoundedRectangleGeometry();
            _root.Clip = _compositor.CreateGeometricClip(_shape);
            _blur = _compositor.CreateSpriteVisual();
            _blur.Brush = _compositor.CreateHostBackdropBrush();
            _root.Children.InsertAtTop(_blur);
            target.Root = _root;
        }
        catch (Exception ex)
        {
            // No blur (very old Windows or composition unavailable): the panel's own tint still shows.
            Log.Error("Setting up the glass backdrop", ex);
        }
    }

    /// <summary>Puts the glass exactly under the panel (physical pixels) and shows it.</summary>
    /// <param name="unitPx">Physical pixels per panel unit (the corner radius is in panel units).</param>
    /// <remarks>Uses WPF's own size and position: during SizeChanged the window itself
    /// (GetWindowRect) still has the old size, so the glass would keep the last page's height.</remarks>
    public void ShowAt(Window panel, double unitPx)
    {
        var source = PresentationSource.FromVisual(panel);
        if (source?.CompositionTarget == null) return;
        var scale = source.CompositionTarget.TransformToDevice;
        int x = (int)Math.Round(panel.Left * scale.M11), y = (int)Math.Round(panel.Top * scale.M22);
        int width = (int)Math.Round(panel.ActualWidth * scale.M11), height = (int)Math.Round(panel.ActualHeight * scale.M22);
        if (width <= 0 || height <= 0) return;

        if (_root != null && _blur != null && _shape != null)
        {
            var size = new Vector2(width, height);
            _root.Size = size;
            _blur.Size = size;
            _shape.Size = size;
            _shape.CornerRadius = new Vector2((float)(_radius * unitPx));
        }
        SetWindowPos(_window.Handle, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void Hide() => ShowWindow(_window.Handle, SW_HIDE);

    static object? _queueController;

    /// <summary>Windows' compositor needs a dispatcher queue on the UI thread (one per thread).</summary>
    static void EnsureDispatcherQueue()
    {
        if (_queueController != null || global::Windows.System.DispatcherQueue.GetForCurrentThread() != null) return;
        var options = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2, apartmentType = 2 };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out IntPtr controller));
        _queueController = controller;
    }

    const int DWMWA_USE_HOSTBACKDROPBRUSH = 17;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    const int SW_HIDE = 0;

    [StructLayout(LayoutKind.Sequential)] struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }
    [DllImport("CoreMessaging.dll")] static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);

    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr target);
    }
}
