using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using ClawControl.Core;

namespace ClawControl.UI;

/// <summary>
/// Liquid Glass's adaptive tint: the panel's glass gets darker over a bright picture (a white web
/// page, a snowy game) so white text stays readable, and stays light over dark ones. When the
/// panel opens it looks once, from the background, at how bright the screen is where the card
/// will be (a tiny copy of that area, about 8 ms of Windows copying the screen) and sets the tint.
/// </summary>
public static class Glass
{
    /// <summary>The usual tint (the theme's PanelBrush) and the deepest one, over a white screen.</summary>
    static readonly Color Light = Color.FromArgb(0x66, 0x26, 0x20, 0x1E), Deep = Color.FromArgb(0xB0, 0x1C, 0x18, 0x16);

    /// <summary>The panel's tint, used directly by everything glass (the card, the app switcher's
    /// pieces, tile marks). Never put it in Application.Resources: WPF freezes a Freezable the
    /// first time it's read from there, and a frozen tint made Apply throw while the panel was
    /// opening, so the panel didn't show over bright pictures (0.1.12 before release).</summary>
    public static readonly SolidColorBrush Tint = new(Light);

    /// <summary>Sets the tint for how bright the picture behind is (Brightness; null: the usual).
    /// Never throws: the panel opens whatever happens to the tint.</summary>
    public static void Apply(double? brightness)
    {
        // Dark and mid-tone pictures keep the usual glass; from there up it deepens.
        double t = brightness is double b ? Math.Clamp((b - 0.35) / 0.4, 0, 1) : 0;
        var color = Color.FromArgb(
            (byte)Math.Round(Light.A + (Deep.A - Light.A) * t), (byte)Math.Round(Light.R + (Deep.R - Light.R) * t),
            (byte)Math.Round(Light.G + (Deep.G - Light.G) * t), (byte)Math.Round(Light.B + (Deep.B - Light.B) * t));
        if (Tint.Color == color) return;
        if (Tint.IsFrozen)
        {
            Log.Detail("The glass tint is frozen; keeping the usual tint");
            return;
        }
        Tint.Color = color;
    }

    const int SampleWidth = 12, SampleHeight = 18;

    /// <summary>The area's average brightness (0 black … 1 white), from 12 × 18 points. Any thread.</summary>
    public static double? Brightness(Int32Rect area)
    {
        if (area.Width <= 0 || area.Height <= 0) return null;
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, SampleWidth, SampleHeight);
        try
        {
            IntPtr old = SelectObject(memory, bitmap);
            SetStretchBltMode(memory, ColorOnColor);
            SetBrushOrgEx(memory, 0, 0, IntPtr.Zero);
            bool copied = StretchBlt(memory, 0, 0, SampleWidth, SampleHeight, screen, area.X, area.Y, area.Width, area.Height, SrcCopy);
            SelectObject(memory, old);
            if (!copied) return null;
            var header = new BitmapInfoHeader { Size = 40, Width = SampleWidth, Height = -SampleHeight, Planes = 1, BitCount = 32 };
            var pixels = new byte[SampleWidth * SampleHeight * 4];
            if (GetDIBits(memory, bitmap, 0, SampleHeight, pixels, ref header, 0) == 0) return null;
            double sum = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                sum += 0.0722 * pixels[i] + 0.7152 * pixels[i + 1] + 0.2126 * pixels[i + 2];  // BGRA
            return sum / (SampleWidth * SampleHeight * 255.0);
        }
        catch (Exception ex)
        {
            Log.Detail($"Looking at the screen's brightness: {ex.Message}");
            return null;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    const int Halftone = 4, ColorOnColor = 3, SrcCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr gdiObject);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int operation);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bitmap, int start, int lines, byte[] bits, ref BitmapInfoHeader info, int usage);
}
