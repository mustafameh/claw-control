using System.Windows;
using ClawControl.Input;

namespace ClawControl.UI;

/// <summary>
/// What A does on a control that has no click of its own, like a slider (left/right already move
/// it): set PanelInput.Accept on it, e.g. A on the volume slider opens the Sound screen.
/// </summary>
public static class PanelInput
{
    public static readonly DependencyProperty AcceptProperty =
        DependencyProperty.RegisterAttached("Accept", typeof(Action), typeof(PanelInput));

    public static void SetAccept(DependencyObject element, Action? action) => element.SetValue(AcceptProperty, action);

    public static Action? GetAccept(DependencyObject element) => (Action?)element.GetValue(AcceptProperty);

    /// <summary>What the footer says A does while this control is selected ("End", "Connect"…); unset = "Select".</summary>
    public static readonly DependencyProperty HintProperty =
        DependencyProperty.RegisterAttached("Hint", typeof(string), typeof(PanelInput));

    public static void SetHint(DependencyObject element, string? hint) => element.SetValue(HintProperty, hint);

    public static string? GetHint(DependencyObject element) => (string?)element.GetValue(HintProperty);

    /// <summary>
    /// Where a D-pad direction goes from this control before the usual "nearest control that
    /// way" (null = the usual). Paged rows use it: right on the last chip flips to the next page.
    /// </summary>
    public static readonly DependencyProperty SteerProperty =
        DependencyProperty.RegisterAttached("Steer", typeof(Func<NavCommand, FrameworkElement?>), typeof(PanelInput));

    public static void SetSteer(DependencyObject element, Func<NavCommand, FrameworkElement?>? steer) => element.SetValue(SteerProperty, steer);

    public static Func<NavCommand, FrameworkElement?>? GetSteer(DependencyObject element) =>
        (Func<NavCommand, FrameworkElement?>?)element.GetValue(SteerProperty);

    /// <summary>What X does on this control (e.g. hide a tile while editing the Quick tab); unset = nothing.</summary>
    public static readonly DependencyProperty ActionProperty =
        DependencyProperty.RegisterAttached("Action", typeof(Action), typeof(PanelInput));

    public static void SetAction(DependencyObject element, Action? action) => element.SetValue(ActionProperty, action);

    public static Action? GetAction(DependencyObject element) => (Action?)element.GetValue(ActionProperty);

    /// <summary>The footer's word for X on this control ("Hide").</summary>
    public static readonly DependencyProperty ActionHintProperty =
        DependencyProperty.RegisterAttached("ActionHint", typeof(string), typeof(PanelInput));

    public static void SetActionHint(DependencyObject element, string? hint) => element.SetValue(ActionHintProperty, hint);

    public static string? GetActionHint(DependencyObject element) => (string?)element.GetValue(ActionHintProperty);

    /// <summary>
    /// Leaves this part of the page out of D-pad navigation (and taps), though it stays shown:
    /// the Quick tab's tiles while it's being edited, when each tile is one selectable block.
    /// </summary>
    public static readonly DependencyProperty SkipProperty =
        DependencyProperty.RegisterAttached("Skip", typeof(bool), typeof(PanelInput), new PropertyMetadata(false));

    public static void SetSkip(DependencyObject element, bool skip) => element.SetValue(SkipProperty, skip);

    public static bool GetSkip(DependencyObject element) => (bool)element.GetValue(SkipProperty);
}
