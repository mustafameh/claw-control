using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClawControl.Features.Controller;
using ClawControl.Features.Network;
using ClawControl.Features.Sound;
using ClawControl.Hardware;
using ClawControl.UI;

namespace ClawControl.Features.Performance;

public partial class PerformancePage : UserControl, IPage
{
    readonly PerformanceService _perf;
    readonly Brightness _brightness;
    readonly AudioService _audio;
    readonly MediaService _media;
    readonly Dictionary<Button, PerfMode> _modeButtons;
    bool _refreshing;
    bool _brightnessRefreshing;
    bool _volumeRefreshing;

    public string Id => "performance";
    public string Title => "Quick";
    public FrameworkElement View => this;

    /// <summary>Opens on the mode circle (Xbox / mouse), or the volume bar when that's hidden.</summary>
    public FrameworkElement DefaultFocus => _tiles.Shown("mode") ?? VolumeSlider;

    /// <summary>A on the volume slider (or tapping its arrow) asks for the Sound screen.</summary>
    public event Action? OpenSoundRequested;

    readonly QuickTiles _tiles;
    readonly NowPlayingTile _nowPlaying;

    public PerformancePage(PerformanceService perf, Brightness brightness, AudioService audio, MediaService media, Core.Settings settings)
    {
        InitializeComponent();
        // Each part of the tab is a tile that can be moved or hidden (Edit, QuickTiles).
        _tiles = new QuickTiles(Root, settings);
        _nowPlaying = new NowPlayingTile(media);
        var parts = new (string, FrameworkElement)[] { ("media", _nowPlaying), ("volume", VolumeSlider), ("brightness", BrightnessSlider), ("power", PowerBlock) };
        Root.Children.Clear();
        foreach (var (id, part) in parts) _tiles.Add(id, part);
        _tiles.EditingChanged += ShowMedia;
        _perf = perf;
        _brightness = brightness;
        _audio = audio;
        _media = media;
        PanelInput.SetAccept(VolumeSlider, () => OpenSoundRequested?.Invoke());
        VolumeSlider.MoreRequested += () => OpenSoundRequested?.Invoke();
        _audio.MasterChanged += (percent, muted) => Dispatcher.BeginInvoke(() => ShowVolume(percent, muted));
        _media.Changed += () => Dispatcher.BeginInvoke(ShowMedia);
        _modeButtons = new()
        {
            [SuperBatteryButton] = PerfMode.SuperBattery,
            [BalancedButton] = PerfMode.Balanced,
            [PerformanceButton] = PerfMode.Performance,
            [CustomButton] = PerfMode.Custom,
        };
        // The caption under the mode bar previews whichever mode the D-pad is on.
        foreach (var button in _modeButtons.Keys)
        {
            button.GotFocus += (_, _) => UpdateCaption();
            button.LostFocus += (_, _) => UpdateCaption();
        }

        // Setting ranges moves the sliders, which must not count as the user choosing Custom.
        _refreshing = true;
        (SplSlider.Minimum, SplSlider.Maximum) = (PerformanceService.MinSpl, PerformanceService.MaxSpl);
        (SpptSlider.Minimum, SpptSlider.Maximum) = (PerformanceService.MinSppt, PerformanceService.MaxSppt);
        (FpptSlider.Minimum, FpptSlider.Maximum) = (PerformanceService.MinFppt, PerformanceService.MaxFppt);

        _perf.Changed += Refresh;
        Refresh();
    }

    public void OnShown()
    {
        _perf.Refresh();
        _ = RefreshBrightness();
        // Last known track shows at once; Windows tells us about changes only while we're open.
        ShowMedia();
        _nowPlaying.OnShown();
        _media.StartListening();
        _ = RefreshVolume();
    }

    public void OnHidden()
    {
        _tiles.Stop(select: false);
        _nowPlaying.OnHidden();
        _media.StopListening();
        _audio.Close();
    }

    /// <summary>B while editing the tiles: drops the one picked up, else Done.</summary>
    public bool HandleBack() => _tiles.Back();

    public string? BackHint => _tiles.Editing ? "Done" : null;

    async Task RefreshVolume()
    {
        if (await _audio.OpenAsync() is var (percent, muted)) ShowVolume(percent, muted);
    }

    void ShowVolume(int percent, bool muted)
    {
        _volumeRefreshing = true;
        VolumeSlider.Value = percent;
        _volumeRefreshing = false;
        VolumeSlider.ValueText = $"{percent}%";
        VolumeSlider.Glyph = SoundPage.VolumeGlyph(percent, muted);
    }

    void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_audio == null) return;  // during InitializeComponent
        int percent = (int)Math.Round(e.NewValue);
        VolumeSlider.ValueText = $"{percent}%";
        VolumeSlider.Glyph = SoundPage.VolumeGlyph(percent, false);
        if (!_volumeRefreshing) _audio.SetMaster(percent);
    }

    // While editing the tiles it shows even with nothing playing, so it can be moved too.
    void ShowMedia() => _nowPlaying.Show(_media.Current, _tiles.Editing);

    /// <summary>Picks up changes made elsewhere (Windows' slider, brightness keys) each time the panel opens.</summary>
    async Task RefreshBrightness()
    {
        var value = await _brightness.ReadAsync();
        if (value is not int percent || _brightness.RecentlySet) return;
        _brightnessRefreshing = true;
        BrightnessSlider.Value = percent;
        _brightnessRefreshing = false;
        BrightnessSlider.ValueText = $"{percent}%";
    }

    void Brightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires during InitializeComponent too, before _brightness is set.
        if (_brightness == null) return;
        int percent = (int)Math.Round(e.NewValue);
        BrightnessSlider.ValueText = $"{percent}%";
        if (!_brightnessRefreshing) _brightness.Set(percent);
    }

    /// <summary>A tile from elsewhere (mode, Wi-Fi, capture, the overlay...); ArrangeTiles once all are in.</summary>
    public void AddTile(string id, FrameworkElement tile, TileSize size = TileSize.Full) => _tiles.Add(id, tile, size);

    /// <summary>Puts the tiles in the order the user chose.</summary>
    public void ArrangeTiles() => _tiles.Arrange();

    /// <summary>Debug captures: the tab as it looks while editing (true), or back.</summary>
    public void ShowEditingForCapture(bool editing)
    {
        if (editing) _tiles.Start(select: false);
        else _tiles.Stop(select: false);
    }

    /// <summary>Debug captures: the now-playing tile open (returns what to select; null when nothing plays), or closed.</summary>
    public FrameworkElement? ShowNowPlayingForCapture(bool open) => _nowPlaying.ShowOpenForCapture(open);

    /// <summary>Edit changed the panel's size or side.</summary>
    public event Action? PanelPlacementChanged
    {
        add => _tiles.PlacementChanged += value;
        remove => _tiles.PlacementChanged -= value;
    }

    /// <summary>Edit turned the Library tab on or off.</summary>
    public event Action? LibraryTabChanged
    {
        add => _tiles.LibraryTabChanged += value;
        remove => _tiles.LibraryTabChanged -= value;
    }

    readonly List<IPage> _screens = new();

    /// <summary>Sub screens opened from this tab (Sound, Wi-Fi, Bluetooth); debug captures use this.</summary>
    public IEnumerable<IPage> Screens => _screens;

    public void AddScreens(params IPage[] screens) => _screens.AddRange(screens);

    void Mode_Click(object sender, RoutedEventArgs e) => _perf.SelectMode(_modeButtons[(Button)sender]);

    void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is FillSlider slider) slider.ValueText = $"{(int)e.NewValue} W";
        if (_refreshing) return;
        int moved = sender == SplSlider ? 0 : sender == SpptSlider ? 1 : 2;
        _perf.SetCustom(PerformanceService.Normalize(
            (int)SplSlider.Value, (int)SpptSlider.Value, (int)FpptSlider.Value, moved));
    }

    static string ModeName(PerfMode mode) => mode switch
    {
        PerfMode.SuperBattery => "Super Battery",
        var other => other.ToString(),
    };

    /// <summary>"Balanced · 20 / 33 / 43 W" for the mode under the D-pad, else the active one.</summary>
    void UpdateCaption()
    {
        var mode = _modeButtons.FirstOrDefault(pair => pair.Key.IsFocused).Key is Button focused
            ? _modeButtons[focused]
            : _perf.Mode;
        var limits = mode == PerfMode.Custom ? _perf.CustomLimits : PerformanceService.Presets[mode];
        ModeCaption.Text = $"{ModeName(mode)} · {limits}";
    }

    void Refresh()
    {
        _refreshing = true;
        try
        {
            foreach (var (button, mode) in _modeButtons)
                button.Tag = mode == _perf.Mode ? "selected" : null;
            CustomLimits.Visibility = _perf.Mode == PerfMode.Custom ? Visibility.Visible : Visibility.Collapsed;
            UpdateCaption();

            var target = _perf.Target;
            SplSlider.Value = target.Spl;
            SpptSlider.Value = target.Sppt;
            FpptSlider.Value = target.Fppt;
            SplSlider.ValueText = $"{target.Spl} W";
            SpptSlider.ValueText = $"{target.Sppt} W";
            FpptSlider.ValueText = $"{target.Fppt} W";
            ApplyStatus.Text = _perf.Status;
            ApplyStatus.Visibility = _perf.Status is "" or "Applied" or "Applying…" or "Using your current limits"
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        finally
        {
            _refreshing = false;
        }
    }
}
