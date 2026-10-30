using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using ClawControl.Core;
using ClawControl.UI;

namespace ClawControl.Features.Apps;

/// <summary>
/// A new version: its number, what's new (the release's notes) and Update now, which downloads
/// and installs it (the button shows how far the download is). Shown by itself once per new
/// version when the panel opens; after that the header's update icon opens it.
/// </summary>
public sealed class UpdateScreen : UserControl, IPage
{
    readonly Updates _updates;
    readonly TextBlock _title, _notes;
    readonly Button _install;
    readonly TextBlock _installText;
    bool _installing;

    public string Id => "update";
    public string Title => "Update";
    public FrameworkElement View => this;

    public UpdateScreen(Updates updates)
    {
        _updates = updates;
        _title = Ui.Text("", bold: true);
        _title.FontSize = 20;
        _title.Margin = new Thickness(8, 4, 8, 2);
        _notes = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21, Margin = new Thickness(14, 10, 14, 12) };
        _installText = Ui.Text("Update now", bold: true);
        _install = Ui.Card(_installText, Install, minHeight: 52);
        _install.Tag = "selected";
        _install.HorizontalContentAlignment = HorizontalAlignment.Center;
        _install.Margin = new Thickness(4, 4, 4, 8);
        var notes = new Border { Background = Ui.Brush("CardBrush"), CornerRadius = new CornerRadius(16), Margin = new Thickness(4, 6, 4, 4), Child = _notes };
        Content = Ui.Stack(_title, _install, notes);
    }

    public FrameworkElement? DefaultFocus => _install;

    public void OnShown()
    {
        if (_updates.Available is not { } update) return;
        _title.Text = $"Claw Control {update.Version}";
        _notes.Text = Plain(update.Notes);
        if (!_installing) _installText.Text = "Update now";
    }

    async void Install()
    {
        if (_installing || _updates.Available is not { } update) return;
        _installing = true;
        _installText.Text = "Downloading";
        var progress = new Progress<double>(done => _installText.Text = $"Downloading {done:P0}");
        var (ok, problem) = await _updates.InstallAsync(update, progress);
        // The setup closes Claw Control and starts the new version; until then it says so.
        _installText.Text = ok ? "Installing" : $"{problem} · Try again";
        _installing = ok;
    }

    /// <summary>The release notes as plain lines (they're written in Markdown on GitHub).</summary>
    static string Plain(string notes)
    {
        var lines = notes.Replace("\r", "").Split('\n')
            .Select(l => Regex.Replace(l, @"\*\*|`|^#+\s*|\[([^\]]+)\]\([^)]+\)", "$1").Trim())
            .Where(l => !l.StartsWith("Checksums", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("Install:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.StartsWith("- ") ? "•  " + l[2..] : l);
        return Regex.Replace(string.Join("\n", lines).Trim(), @"\n{3,}", "\n\n");
    }
}
