using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClawControl.Core;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Capture;

/// <summary>
/// "Save to": a folder browser made for the D-pad. It opens on the folder in use: Save here
/// first, then Up and the folders inside (A opens one). Above every drive: the usual places
/// (Captures, Pictures, Videos, Desktop, Documents, Downloads) and the drives (an SD card too).
/// </summary>
public sealed class FolderScreen : UserControl, IPage
{
    static readonly Brush HereBrush = Ui.Hex("#FF34C759"), FolderBrush = Ui.Hex("#FF0A84FF");
    const int MaxFolders = 200;

    readonly Func<string> _current;
    readonly Action<string> _pick;
    readonly StackPanel _root = new();
    /// <summary>The folder shown, or null for the places and drives.</summary>
    string? _at;

    public string Id { get; }
    public string Title => "Save to";
    public FrameworkElement View => this;

    /// <param name="current">The folder in use now.</param>
    /// <param name="pick">Save here was pressed on a folder (the screen should close).</param>
    public FolderScreen(string id, Func<string> current, Action<string> pick)
    {
        Id = id;
        _current = current;
        _pick = pick;
        Content = _root;
    }

    public void OnShown()
    {
        _at = _current();
        Build();
    }

    void Open(string? folder)
    {
        _at = folder;
        Build();
        // The list was rebuilt under the selection: start at its top.
        Dispatcher.BeginInvoke(() => { if (SpatialNavigator.First(this) is { } first) Ui.Select(first); }, DispatcherPriority.Loaded);
    }

    void Build()
    {
        _root.Children.Clear();
        if (_at == null)
        {
            _root.Children.Add(ListRow.Card(Places().Select(p => Row(p.Name, p.Path, "")).ToArray()));
            var drives = Drives().Select(d => Row(d.Name, d.Path, "")).ToArray();
            if (drives.Length > 0) _root.Children.Add(ListRow.Card(drives));
            return;
        }

        var path = new TextBlock { Text = _at, Style = Ui.Style("CardDetail"), Margin = new Thickness(8, 0, 8, 2), TextTrimming = TextTrimming.CharacterEllipsis };
        _root.Children.Add(path);
        var here = new ListRow(Ui.Glyph("", 16), HereBrush, "Save here", chevron: false);
        here.Check = string.Equals(Path.TrimEndingDirectorySeparator(_at), Path.TrimEndingDirectorySeparator(_current()), StringComparison.OrdinalIgnoreCase);
        string chosen = _at;
        here.Button.Click += (_, _) => _pick(chosen);
        string? parent = Directory.GetParent(_at)?.FullName;
        var up = new ListRow(Ui.Glyph("", 16), Ui.RowIconBrush, "Up");
        up.Value.Text = parent != null ? NameOf(parent) : "Places";
        up.Button.Click += (_, _) => Open(parent);
        _root.Children.Add(ListRow.Card(here, up));

        var inside = Inside(_at).Select(f => Row(NameOf(f), f, "")).ToArray();
        if (inside.Length > 0) _root.Children.Add(ListRow.Card(inside));
    }

    ListRow Row(string name, string path, string glyph)
    {
        var row = new ListRow(Ui.Glyph(glyph, 16), FolderBrush, name);
        row.Button.Click += (_, _) => Open(path);
        return row;
    }

    static string NameOf(string folder) => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;

    static IEnumerable<(string Name, string Path)> Places()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        (string, string)[] places =
        [
            ("Captures", ScreenCapture.Folder),
            ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
            ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
            ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("Downloads", Path.Combine(home, "Downloads")),
        ];
        return places.Where(p => p.Item2.Length > 0 && (Directory.Exists(p.Item2) || p.Item2 == ScreenCapture.Folder));
    }

    static IEnumerable<(string Name, string Path)> Drives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            string? label = null;
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                label = drive.VolumeLabel;
            }
            catch { continue; }
            string letter = drive.Name.TrimEnd('\\');
            yield return (string.IsNullOrWhiteSpace(label) ? letter : $"{label} ({letter})", drive.RootDirectory.FullName);
        }
    }

    /// <summary>The folders inside (hidden and system ones left out), by name.</summary>
    static IEnumerable<string> Inside(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .Select(d => d.FullName).Order(StringComparer.OrdinalIgnoreCase).Take(MaxFolders).ToList();
        }
        catch (Exception ex)
        {
            Log.Detail($"Listing {folder}: {ex.Message}");
            return [];
        }
    }
}
