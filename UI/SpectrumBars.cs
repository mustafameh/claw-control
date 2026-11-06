using System.Windows;
using System.Windows.Media;
using ClawControl.Hardware;

namespace ClawControl.UI;

/// <summary>
/// The now-playing tile's faint bars (chosen 2026-11-06: "bars in the cover's colour, fading out
/// at the tips"): one bar per <see cref="AudioSpectrum"/> band, standing on the tile's bottom
/// edge, each a gradient from the cover's colour to clear at its top. Taller in the middle (an
/// arch), so the edges stay calm. Redrawn at most 30 times a second, and only while running;
/// stopped, the bars sink away and the frame hook goes away.
/// </summary>
sealed class SpectrumBars : FrameworkElement
{
    const int Count = AudioSpectrum.Bands;
    const double Gap = 4, Corner = 3, FrameTime = 1 / 30.0;

    readonly AudioSpectrum _spectrum;
    readonly float[] _target = new float[Count];
    readonly double[] _shown = new double[Count];
    readonly double[] _arch = new double[Count];
    Brush _fill = Brushes.Transparent;
    bool _wanted, _hooked;
    TimeSpan _last;
    double _wait;

    /// <summary>Tile heights: compact, the bars nearly fill it; open, they take 62% of it.</summary>
    public double CompactHeight { get; set; } = 64;
    public double OpenHeight { get; set; } = 230;

    public SpectrumBars(AudioSpectrum spectrum)
    {
        _spectrum = spectrum;
        IsHitTestVisible = false;
        ClipToBounds = true;
        for (int i = 0; i < Count; i++)
            _arch[i] = 0.3 + 0.7 * Math.Pow(Math.Sin(Math.PI * i / (Count - 1)), 1.3);
        Tint = Color.FromRgb(0xE6, 0xE8, 0xEE);
    }

    public Color Tint
    {
        set
        {
            var fill = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };
            fill.GradientStops.Add(new GradientStop(value, 0));
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(0xCC, value.R, value.G, value.B), 0.45));
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(0, value.R, value.G, value.B), 1));
            fill.Freeze();
            _fill = fill;
            InvalidateVisual();
        }
    }

    /// <summary>On while the panel shows a playing track; off lets the bars sink and then stops.</summary>
    public void Run(bool on)
    {
        _wanted = on;
        if (_hooked) return;
        if (!on && _shown.All(level => level < 0.005)) return;
        _hooked = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>Panel closed: straight to rest, nothing left running.</summary>
    public void StopNow()
    {
        _wanted = false;
        Array.Clear(_shown);
        Unhook();
        InvalidateVisual();
    }

    void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _last) return;
        double dt = _last == TimeSpan.Zero ? FrameTime : Math.Min(0.1, (now - _last).TotalSeconds);
        _last = now;
        _wait -= dt;
        if (_wait > 0) return;
        _wait = FrameTime;

        if (_wanted) _spectrum.Read(_target);
        else Array.Clear(_target);
        bool resting = true;
        for (int i = 0; i < Count; i++)
        {
            // Quick to rise, slow to fall, like a meter.
            double target = _target[i];
            _shown[i] += (target - _shown[i]) * (target > _shown[i] ? 0.55 : 0.2);
            if (_shown[i] > 0.005) resting = false;
        }
        InvalidateVisual();
        if (!_wanted && resting)
        {
            Array.Clear(_shown);
            Unhook();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        // As tall as the tile allows: nearly all of it compact, 62% open, in between while it opens.
        double open = OpenHeight > CompactHeight ? Math.Clamp((height - CompactHeight) / (OpenHeight - CompactHeight), 0, 1) : 0;
        double top = (CompactHeight - 6) + ((OpenHeight * 0.62) - (CompactHeight - 6)) * open;
        double slot = width / Count, bar = Math.Max(1, slot - Gap);
        for (int i = 0; i < Count; i++)
        {
            double h = top * _arch[i] * Math.Clamp(_shown[i], 0, 1);
            if (h < 0.5) continue;  // silent: no bar (a row of stubs read as a dotted line)
            // Runs past the bottom edge (clipped) so only the top corners are rounded.
            dc.DrawRoundedRectangle(_fill, null, new Rect(i * slot + (slot - bar) / 2, height - h, bar, h + Corner), Corner, Corner);
        }
    }
}
