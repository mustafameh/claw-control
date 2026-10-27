using System.Runtime.InteropServices;
using System.Windows;

namespace ClawControl.UI;

/// <summary>
/// A live picture of another app's window, drawn by Windows' compositor into one of our windows
/// (what Alt + Tab uses): it costs us nothing to draw and keeps moving with the app. Only an
/// upright rectangle with square corners, drawn on top of the window it's in. Minimised windows
/// show their last picture or nothing; some exclusive-fullscreen games show black.
/// </summary>
sealed class WindowPreview(IntPtr destination)
{
    IntPtr _thumbnail, _source;

    /// <summary>Shows <paramref name="source"/>'s window fitted inside <paramref name="box"/>
    /// (physical pixels in the destination window) at an opacity; false if Windows can't picture it.</summary>
    public bool Show(IntPtr source, Int32Rect box, byte opacity = 255)
    {
        if (source != _source)
        {
            Clear();
            if (source == IntPtr.Zero || DwmRegisterThumbnail(destination, source, out _thumbnail) != 0)
            {
                _thumbnail = IntPtr.Zero;
                return false;
            }
            _source = source;
        }
        if (DwmQueryThumbnailSourceSize(_thumbnail, out var size) != 0 || size.Width <= 0 || size.Height <= 0)
        {
            Clear();
            return false;
        }
        double scale = Math.Min(box.Width / (double)size.Width, box.Height / (double)size.Height);
        int width = (int)Math.Round(size.Width * scale), height = (int)Math.Round(size.Height * scale);
        int left = box.X + (box.Width - width) / 2, top = box.Y + (box.Height - height) / 2;
        var properties = new ThumbnailProperties
        {
            Flags = DestinationFlag | OpacityFlag | VisibleFlag | ClientOnlyFlag,
            Destination = new Rect32 { Left = left, Top = top, Right = left + width, Bottom = top + height },
            Opacity = opacity,
            Visible = 1,
            ClientAreaOnly = 0,
        };
        return DwmUpdateThumbnailProperties(_thumbnail, ref properties) == 0;
    }

    public void Clear()
    {
        if (_thumbnail != IntPtr.Zero) DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = _source = IntPtr.Zero;
    }

    const int DestinationFlag = 0x1, OpacityFlag = 0x4, VisibleFlag = 0x8, ClientOnlyFlag = 0x10;

    [StructLayout(LayoutKind.Sequential)] struct Rect32 { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Size32 { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    struct ThumbnailProperties
    {
        public int Flags;
        public Rect32 Destination, Source;
        public byte Opacity;
        public int Visible, ClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out Size32 size);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);
}
