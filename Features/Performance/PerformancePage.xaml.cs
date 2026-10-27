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

    /// <summary>The panel opens on the volume slider; press up for the music controls.</summary>
    /// <summary>Opens on the mode circle (Xbox / mouse), or the volume bar without the top row.</summary>
    public FrameworkElement DefaultFocus => (TopRow.Content as QuickRow)?.Mode ?? VolumeSlider;

    /// <summary>A on the volume slider (or tapping its arrow) asks for the Sound screen.</summary>
    public event Action? OpenSoundRequested;

    public PerformancePage(PerformanceService perf, Brightness brightness, AudioService audio, MediaService media)
    {
        InitializeComponent();
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
        _media.StartListening();
        _ = RefreshVolume();
    }

    public void OnHidden()
    {
        _media.StopListening();
        _audio.Close();
    }

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

    void ShowMedia()
    {
        var media = _media.Current;
        MediaStrip.Visibility = media == null ? Visibility.Collapsed : Visibility.Visible;
        if (media == null) return;
        MediaTitle.Text = media.Title;
        MediaArtist.Text = string.Join(" · ", new[] { media.Artist, media.App }.Where(s => s.Length > 0));
        MediaArt.Background = media.Art != null
            ? new ImageBrush(media.Art) { Stretch = Stretch.UniformToFill }
            : (Brush)FindResource("TrackBrush");
        MediaArtFallback.Visibility = media.Art == null ? Visibility.Visible : Visibility.Collapsed;
        PlayPauseButton.Content = media.Playing ? "" : "";
        PreviousButton.IsEnabled = media.CanPrevious;
        NextButton.IsEnabled = media.CanNext;
    }

    void PlayPause_Click(object sender, RoutedEventArgs e) => _media.PlayPause();
    void Previous_Click(object sender, RoutedEventArgs e) => _media.Previous();
    void Next_Click(object sender, RoutedEventArgs e) => _media.Next();

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

    /// <summary>Related sections (like the performance overlay) go below the power limits.</summary>
    public void AddSection(UIElement section) => Root.Children.Add(section);

    /// <summary>The row above volume and brightness (controller mode, Wi-Fi, Bluetooth).</summary>
    public void SetTopRow(UIElement row) => TopRow.Content = row;

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
