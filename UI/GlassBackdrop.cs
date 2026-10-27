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
/// The panel's glass: rounded, blurred pieces of whatever is behind it (the card and the app
/// switcher's pieces), drawn by Windows' compositor on the GPU in a separate click-through
/// window right under the panel (the same size and place; the panel works out the pieces).
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
    readonly CompositionBrush? _backdrop;
    /// <summary>One blurred rounded piece each (the panel uses one; the app switcher several).</summary>
    readonly List<(SpriteVisual Sprite, CompositionRoundedRectangleGeometry Shape)> _pieces = new();

    public IntPtr Handle => _window.Handle;

    public GlassBackdrop()
    {
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
            _backdrop = _compositor.CreateHostBackdropBrush();
            target.Root = _root;
        }
        catch (Exception ex)
        {
            // No blur (very old Windows or composition unavailable): the panel's own tint still shows.
            Log.Error("Setting up the glass backdrop", ex);
        }
    }

    /// <summary>A rounded piece of glass, in physical pixels from the glass area's top-left.
    /// The panel passes WPF's own sizes: during SizeChanged the window itself (GetWindowRect)
    /// still has the old size.</summary>
    public readonly record struct GlassShape(Rect Area, double Radius, double Opacity = 1);

    /// <summary>Shows the glass over <paramref name="area"/> (screen pixels) as these pieces. Pieces
    /// may overlap: each shows the same blurred backdrop, so they join seamlessly (a ring is a
    /// row of overlapping circles).</summary>
    public void Show(Int32Rect area, IReadOnlyList<GlassShape> shapes)
    {
        if (_compositor != null && _root != null)
        {
            while (_pieces.Count < shapes.Count)
            {
                var sprite = _compositor.CreateSpriteVisual();
                sprite.Brush = _backdrop;
                var shape = _compositor.CreateRoundedRectangleGeometry();
                sprite.Clip = _compositor.CreateGeometricClip(shape);
                _root.Children.InsertAtTop(sprite);
                _pieces.Add((sprite, shape));
            }
            for (int i = 0; i < _pieces.Count; i++)
            {
                var (sprite, shape) = _pieces[i];
                sprite.IsVisible = i < shapes.Count;
                if (i >= shapes.Count) continue;
                var piece = shapes[i];
                var size = new Vector2((float)piece.Area.Width, (float)piece.Area.Height);
                sprite.Offset = new Vector3((float)piece.Area.X, (float)piece.Area.Y, 0);
                sprite.Opacity = (float)piece.Opacity;
                sprite.Size = size;
                shape.Size = size;
                shape.CornerRadius = new Vector2((float)piece.Radius);
            }
        }
        SetWindowPos(_window.Handle, HWND_TOPMOST, area.X, area.Y, area.Width, area.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
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
