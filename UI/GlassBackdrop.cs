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
    /// <summary>Groups of pieces that move together (the card; the app switcher), each in its own
    /// container so a group can grow or move with one transform instead of every piece changing.</summary>
    readonly List<Group> _groups = new();
    Int32Rect _area;
    bool _shown;

    sealed class Group(ContainerVisual container)
    {
        public readonly ContainerVisual Container = container;
        public readonly List<(SpriteVisual Sprite, CompositionRoundedRectangleGeometry Shape)> Pieces = new();
        /// <summary>What each piece shows now, so only what changed is sent to the compositor.</summary>
        public readonly List<GlassShape> Shown = new();
        public Matrix3x2 Transform = Matrix3x2.Identity;
    }

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

    /// <summary>A group of pieces in its own units, placed in the glass area's pixels by a transform.</summary>
    public readonly record struct Layer(IReadOnlyList<GlassShape> Shapes, Matrix3x2 Transform);

    /// <summary>Shows the glass over <paramref name="area"/> (screen pixels) as these layers of
    /// pieces. Pieces may overlap: each shows the same blurred backdrop, so they join seamlessly
    /// (a ring is a row of overlapping circles). Only what changed since last time is sent.</summary>
    public void Show(Int32Rect area, params Layer[] layers)
    {
        Update(layers);
        // Moving or showing a window is a round trip through Windows: only when needed.
        if (_shown && area == _area) return;
        _area = area;
        _shown = true;
        SetWindowPos(_window.Handle, HWND_TOPMOST, area.X, area.Y, area.Width, area.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Changes the pieces without showing the window. Used while hidden to leave the look the
    /// panel opens with next time: a shown window draws what it had before the compositor has
    /// the new pieces, so the last look would flash for a frame.
    /// </summary>
    public void Update(params Layer[] layers)
    {
        if (_compositor == null || _root == null) return;
        while (_groups.Count < layers.Length)
        {
            var container = _compositor.CreateContainerVisual();
            _root.Children.InsertAtTop(container);
            _groups.Add(new Group(container));
        }
        for (int g = 0; g < _groups.Count; g++)
        {
            var group = _groups[g];
            var shapes = g < layers.Length ? layers[g].Shapes : [];
            var transform = g < layers.Length ? layers[g].Transform : Matrix3x2.Identity;
            if (transform != group.Transform)
            {
                group.Transform = transform;
                group.Container.TransformMatrix = new Matrix4x4(transform);
            }
            while (group.Pieces.Count < shapes.Count)
            {
                var sprite = _compositor.CreateSpriteVisual();
                sprite.Brush = _backdrop;
                var shape = _compositor.CreateRoundedRectangleGeometry();
                sprite.Clip = _compositor.CreateGeometricClip(shape);
                group.Container.Children.InsertAtTop(sprite);
                group.Pieces.Add((sprite, shape));
                group.Shown.Add(default);
            }
            for (int i = 0; i < group.Pieces.Count; i++)
            {
                var (sprite, shape) = group.Pieces[i];
                var piece = i < shapes.Count ? shapes[i] : default;
                var was = group.Shown[i];  // what the sprite has
                if (piece == was) continue;
                bool visible = piece.Opacity > 0.005, wasVisible = was.Opacity > 0.005;
                if (!visible)
                {
                    if (wasVisible) sprite.IsVisible = false;
                    group.Shown[i] = was with { Opacity = 0 };
                    continue;
                }
                group.Shown[i] = piece;
                if (!wasVisible) sprite.IsVisible = true;
                if (piece.Opacity != was.Opacity) sprite.Opacity = (float)piece.Opacity;
                if (piece.Area != was.Area)
                {
                    var size = new Vector2((float)piece.Area.Width, (float)piece.Area.Height);
                    sprite.Offset = new Vector3((float)piece.Area.X, (float)piece.Area.Y, 0);
                    sprite.Size = size;
                    shape.Size = size;
                }
                if (piece.Radius != was.Radius) shape.CornerRadius = new Vector2((float)piece.Radius);
            }
        }
    }

    public void Hide()
    {
        _shown = false;
        ShowWindow(_window.Handle, SW_HIDE);
    }

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
