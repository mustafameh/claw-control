using System.Windows.Controls;
using System.Windows.Media;

namespace ClawControl.UI;

/// <summary>
/// Apple's soft scroll edge: where a list has more above or below, its content fades out toward
/// that edge instead of being cut off. Only on a side with more to scroll; nothing at all (no
/// mask, no cost) while everything fits.
/// </summary>
public static class ScrollFade
{
    /// <param name="fade">How far the fade reaches in from the edge, in the scroller's units.</param>
    public static void Attach(ScrollViewer scroller, double fade)
    {
        (bool Top, bool Bottom, double Height) shown = (false, false, 0);
        void Update()
        {
            double height = scroller.ActualHeight;
            bool top = scroller.VerticalOffset > 0.5, bottom = scroller.VerticalOffset < scroller.ScrollableHeight - 0.5;
            if ((top, bottom, height) == shown) return;
            shown = (top, bottom, height);
            if (height <= 0 || (!top && !bottom))
            {
                scroller.OpacityMask = null;
                return;
            }
            double reach = Math.Min(fade, height / 3) / height;
            var mask = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
            mask.GradientStops.Add(new GradientStop(top ? Colors.Transparent : Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, top ? reach : 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, bottom ? 1 - reach : 1));
            mask.GradientStops.Add(new GradientStop(bottom ? Colors.Transparent : Colors.Black, 1));
            mask.Freeze();
            scroller.OpacityMask = mask;
        }
        scroller.ScrollChanged += (_, _) => Update();
        scroller.SizeChanged += (_, _) => Update();
    }
}
