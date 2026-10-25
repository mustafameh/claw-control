using System.Windows;
using System.Windows.Controls;
using ClawControl.UI;

namespace ClawControl.Features.Graphics;

/// <summary>
/// A sub screen of choices in grouped cards; a check marks the current one, A picks. The
/// choices are rebuilt each time it opens, so they follow what the hardware offers.
/// </summary>
public sealed class PickerScreen : UserControl, IPage
{
    readonly Func<IEnumerable<IEnumerable<(string Label, object Value)>>> _groups;
    readonly Func<object?> _current;
    readonly Action<object> _pick;
    readonly StackPanel _root = new();
    readonly List<(ListRow Row, object Value)> _rows = new();

    public string Id { get; }
    public string Title { get; }
    public FrameworkElement View => this;

    /// <summary>Shown under the choices (graphics memory's Restart button).</summary>
    public FrameworkElement? Footer { get; init; }

    public PickerScreen(string id, string title, Func<IEnumerable<IEnumerable<(string Label, object Value)>>> groups,
        Func<object?> current, Action<object> pick)
    {
        Id = id;
        Title = title;
        _groups = groups;
        _current = current;
        _pick = pick;
        Content = _root;
    }

    public void OnShown()
    {
        _root.Children.Clear();
        _rows.Clear();
        foreach (var group in _groups())
        {
            var rows = group.Select(choice =>
            {
                var row = new ListRow(null, null, choice.Label, chevron: false);
                row.Button.Click += (_, _) => _pick(choice.Value);
                _rows.Add((row, choice.Value));
                return row;
            }).ToArray();
            if (rows.Length > 0) _root.Children.Add(ListRow.Card(rows));
        }
        if (Footer != null) _root.Children.Add(Footer);
        Refresh();
    }

    public void Refresh()
    {
        var current = _current();
        foreach (var (row, value) in _rows) row.Check = Equals(value, current);
    }
}
