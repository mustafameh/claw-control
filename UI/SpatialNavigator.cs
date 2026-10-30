using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ClawControl.Input;

namespace ClawControl.UI;

/// <summary>
/// Moves controller focus to the nearest control in the pressed direction, based on where
/// controls actually are on screen. Works the same whether or not Windows considers the
/// panel the active window, unlike WPF's built-in keyboard navigation.
/// </summary>
public static class SpatialNavigator
{
    public static FrameworkElement? First(FrameworkElement root) => Controls(root).FirstOrDefault();

    /// <summary>The control whose centre is closest to a point in <paramref name="root"/>
    /// (e.g. the same row again after a list was rebuilt under the selection).</summary>
    public static FrameworkElement? Nearest(FrameworkElement root, Point point) =>
        Controls(root).MinBy(control => (Mid(Bounds(control, root)) - point).LengthSquared);

    public static FrameworkElement? Next(FrameworkElement? current, FrameworkElement root, NavCommand direction)
    {
        var controls = Controls(root).ToList();
        if (current == null || !root.IsAncestorOf(current)) return controls.FirstOrDefault();
        if (!controls.Contains(current))
        {
            // Focus sits inside a control (e.g. a slider's thumb) or on a plain element: carry on
            // from the control around it, else the one nearest to it, never jump to the top.
            var owner = OwnerControl(current, root, controls);
            if (owner == null) return Nearest(root, Mid(Bounds(current, root)));
            current = owner;
        }

        var from = Bounds(current, root);
        FrameworkElement? best = null;
        double bestScore = double.MaxValue;
        foreach (var candidate in controls)
        {
            if (candidate == current) continue;
            var to = Bounds(candidate, root);
            double dx = Mid(to).X - Mid(from).X, dy = Mid(to).Y - Mid(from).Y;
            double centreAlong = direction switch
            {
                NavCommand.Up => -dy,
                NavCommand.Down => dy,
                NavCommand.Left => -dx,
                _ => dx,
            };
            if (centreAlong <= 1) continue;

            // Measure edge to edge, not centre to centre: a wide control (like the overlay switch)
            // directly above a narrow one counts as straight ahead, even though its centre is off
            // to the side. Centre distance only breaks ties (e.g. which of three buttons below).
            var (along, gap, centreAcross) = direction switch
            {
                NavCommand.Up => (from.Top - to.Bottom, Gap(from.Left, from.Right, to.Left, to.Right), Math.Abs(dx)),
                NavCommand.Down => (to.Top - from.Bottom, Gap(from.Left, from.Right, to.Left, to.Right), Math.Abs(dx)),
                NavCommand.Left => (from.Left - to.Right, Gap(from.Top, from.Bottom, to.Top, to.Bottom), Math.Abs(dy)),
                _ => (to.Left - from.Right, Gap(from.Top, from.Bottom, to.Top, to.Bottom), Math.Abs(dy)),
            };
            // A control that overlaps this one in the direction of travel isn't really ahead of it
            // (e.g. a full-width slider in the row above, seen from a mode circle when pressing
            // right): count how far its centre is instead of zero.
            if (along < 0) along = centreAlong;
            // Prefer controls straight ahead over ones that are closer but off to the side.
            double score = along + gap * 2 + centreAcross * 0.001;
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    static IEnumerable<FrameworkElement> Controls(DependencyObject node)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (PanelInput.GetSkip(child)) continue;
            if (child is FrameworkElement { IsVisible: true, IsEnabled: true, Focusable: true } element
                && element is ButtonBase or Slider)
            {
                yield return element;
                continue;
            }
            foreach (var nested in Controls(child))
                yield return nested;
        }
    }

    static FrameworkElement? OwnerControl(DependencyObject element, FrameworkElement root, List<FrameworkElement> controls)
    {
        for (var node = VisualTreeHelper.GetParent(element); node != null && node != root; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement parent && controls.Contains(parent)) return parent;
        return null;
    }

    static Rect Bounds(FrameworkElement element, FrameworkElement root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    static Point Mid(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    /// <summary>Distance between two ranges on one axis; 0 when they overlap.</summary>
    static double Gap(double aStart, double aEnd, double bStart, double bEnd) =>
        Math.Max(0, Math.Max(aStart, bStart) - Math.Min(aEnd, bEnd));
}
