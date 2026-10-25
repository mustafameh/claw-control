using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Sound;

/// <summary>
/// Sound screen, opened with A on the volume slider (B goes back): the output devices Windows has
/// right now, master volume, and one slider per app with sound. Everything is read when the
/// screen opens; app changes are only watched while it's open.
/// </summary>
public sealed class SoundPage : UserControl, IPage
{
    readonly AudioService _audio;
    readonly UniformGrid _devices = new() { Columns = 3 };
    readonly FillSlider _master;
    readonly Border _appsDivider;
    readonly StackPanel _apps = new();
    readonly Dictionary<string, AppRow> _appRows = new();
    bool _refreshing;

    public string Id => "sound";
    public string Title => "Sound";
    public FrameworkElement View => this;

    public SoundPage(AudioService audio)
    {
        _audio = audio;
        _audio.AppsChanged += () => Dispatcher.BeginInvoke(() => _ = LoadApps());

        var root = new StackPanel();
        // Device buttons only show when there's more than one to pick from.
        _devices.Visibility = Visibility.Collapsed;
        _devices.Margin = new Thickness(0, 0, 0, 6);
        root.Children.Add(_devices);

        _master = NewSlider(2);
        _master.Glyph = "";
        _master.Label = "All";
        _master.ValueChanged += (_, e) =>
        {
            int percent = (int)Math.Round(e.NewValue);
            _master.ValueText = $"{percent}%";
            _master.Glyph = VolumeGlyph(percent, false);
            if (!_refreshing) _audio.SetMaster(percent);
        };
        root.Children.Add(_master);

        _appsDivider = new Border { Style = (Style)FindResource("Divider"), Visibility = Visibility.Collapsed };
        root.Children.Add(_appsDivider);
        root.Children.Add(_apps);
        Content = root;
    }

    public void OnShown()
    {
        _ = LoadMaster();
        _ = LoadDevices();
        _ = OpenApps();
    }

    public void OnHidden() => _audio.CloseApps();

    async Task LoadMaster()
    {
        if (await _audio.OpenAsync() is not var (percent, muted)) return;
        _refreshing = true;
        _master.Value = percent;
        _refreshing = false;
        _master.ValueText = $"{percent}%";
        _master.Glyph = VolumeGlyph(percent, muted);
    }

    async Task LoadDevices()
    {
        var devices = await _audio.ReadDevicesAsync();
        _devices.Children.Clear();
        _devices.Visibility = devices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var device in devices)
        {
            var button = new Button
            {
                Style = (Style)FindResource("ChipButton"),
                Content = new TextBlock { Text = device.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                Tag = device.IsDefault ? "selected" : null,
            };
            button.Click += async (_, _) =>
            {
                if (device.IsDefault) return;
                foreach (Button other in _devices.Children) other.Tag = other == button ? "selected" : null;
                await _audio.SetDefaultDeviceAsync(device.Id);
                // Volume and apps belong to the device: show the new one's.
                _ = LoadMaster();
                _ = LoadDevices();
                _ = LoadApps();
            };
            _devices.Children.Add(button);
        }
    }

    async Task OpenApps()
    {
        await _audio.OpenAppsAsync();
        await LoadApps();
    }

    /// <summary>Updates rows in place, so the selected slider stays selected when an app appears.</summary>
    async Task LoadApps()
    {
        var apps = await _audio.ReadAppsAsync();
        foreach (var gone in _appRows.Keys.Except(apps.Select(a => a.Key)).ToList())
        {
            _apps.Children.Remove(_appRows[gone].Panel);
            _appRows.Remove(gone);
        }
        for (int i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            if (!_appRows.TryGetValue(app.Key, out var row))
            {
                _appRows[app.Key] = row = new AppRow(this, app);
                _apps.Children.Insert(Math.Min(i, _apps.Children.Count), row.Panel);
            }
            row.Show(app.Percent);
        }
        _appsDivider.Visibility = apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One app: a fill bar with the app's icon and name inside it.</summary>
    sealed class AppRow
    {
        readonly FillSlider _slider;
        bool _refreshing;
        public FrameworkElement Panel => _slider;

        public AppRow(SoundPage page, AppVolume app)
        {
            _slider = NewSlider(5);
            _slider.Icon = app.Icon;
            _slider.Label = app.Name;
            if (app.Icon == null) _slider.Glyph = "";
            _slider.ValueChanged += (_, e) =>
            {
                int percent = (int)Math.Round(e.NewValue);
                _slider.ValueText = $"{percent}%";
                if (!_refreshing) page._audio.SetApp(app.Key, percent);
            };
        }

        public void Show(int percent)
        {
            _refreshing = true;
            _slider.Value = percent;
            _refreshing = false;
            _slider.ValueText = $"{percent}%";
        }
    }

    /// <summary>Speaker icon for a volume level (Segoe Fluent Icons).</summary>
    public static string VolumeGlyph(int percent, bool muted) =>
        muted || percent == 0 ? "" : percent < 34 ? "" : percent < 67 ? "" : "";

    static FillSlider NewSlider(double step) => new()
    {
        Minimum = 0,
        Maximum = 100,
        SmallChange = step,
    };
}
