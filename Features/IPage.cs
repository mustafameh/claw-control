using System.Windows;

namespace ClawControl.Features;

/// <summary>
/// One page of the panel (switched with LB / RB). Each feature area is its own page, built once
/// at startup and kept alive, so switching pages or opening the panel never waits on loading.
/// </summary>
public interface IPage
{
    /// <summary>Stable id, used to remember the last page and focused control.</summary>
    string Id { get; }

    string Title { get; }

    FrameworkElement View { get; }

    /// <summary>
    /// The page became visible. Show cached values immediately and refresh in the background;
    /// never block here.
    /// </summary>
    void OnShown();

    /// <summary>The page went away (panel closed, other page). Stop anything started in OnShown.</summary>
    void OnHidden() { }

    /// <summary>B was pressed on this page; return true if it handled it (e.g. closed a picker).</summary>
    bool HandleBack() => false;

    /// <summary>The footer's word for B when it isn't the usual Close / Back (e.g. "Done" while editing).</summary>
    string? BackHint => null;

    /// <summary>Sub screens this page opens; debug captures render them too.</summary>
    IEnumerable<IPage> Screens => [];

    /// <summary>Control to select whenever the panel opens on this page; null = the last one used.</summary>
    FrameworkElement? DefaultFocus => null;
}
