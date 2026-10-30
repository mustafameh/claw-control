using System.Diagnostics;
using ClawControl.Core;
using ClawControl.Hardware;

namespace ClawControl.Features.Graphics;

/// <summary>
/// What the Graphics tab shows and changes: AMD driver settings (AmdGraphics) and the main
/// screen's resolution, refresh rate (DisplayModes) and Windows scaling (DisplayScale). Keeps
/// the last values so the tab shows them at once, and reads again in the background when the
/// tab is shown.
/// </summary>
public sealed class GraphicsService : IDisposable
{
    readonly AmdGraphics _amd = new();
    readonly HardwareWorker _display = new();
    readonly Settings _settings;
    int _memoryAtStart = -1;

    public AmdState Amd { get; private set; } = AmdState.Unknown;
    public DisplayMode Mode { get; private set; }
    public IReadOnlyList<DisplayMode> Modes { get; private set; } = [];
    public ScaleInfo Scale { get; private set; } = ScaleInfo.Unknown;

    /// <summary>Anything on the tab may have changed (any thread).</summary>
    public event Action? Changed;

    public GraphicsService(Settings settings)
    {
        _settings = settings;
        _amd.Changed += state =>
        {
            Note("set", state);
            Amd = state;
            if (state.Available) _live = new(state.UpscalingOn, state.FrameGenOn);
            Changed?.Invoke();
        };
    }

    string _lastNote = "";

    /// <summary>Logs the AMD settings whenever they differ from last time (to see who changed what).</summary>
    void Note(string how, AmdState s)
    {
        string note = $"FPS limit {(s.FpsLimitOn ? s.FpsLimit.ToString() : "off")}, frame gen {s.FrameGenOn}, " +
            $"upscaling {s.UpscalingOn}, sharpening {(s.SharpeningOn ? $"{s.Sharpness}%" : "off")}, FreeSync {s.FreeSyncOn}";
        if (note == _lastNote) return;
        _lastNote = note;
        Log.Info($"AMD settings ({how}): {note}");
    }

    public async Task RefreshAsync(bool tabShown = false)
    {
        var display = _display.Run(() => (DisplayModes.Current(), DisplayModes.All(), DisplayScale.Read()));
        Amd = await _amd.ReadAsync();
        if (Amd.Available) Note("read", Amd);
        if (_memoryAtStart < 0) _memoryAtStart = Amd.MemoryChoice;
        (Mode, Modes, Scale) = await display;
        Changed?.Invoke();
        // Upscaling reads as unsupported until AMD Software's settings service runs (Hardware/
        // AmdGraphics): start it, which also lets frame generation changes through.
        // Only when the tab is shown (never at startup).
        if (tabShown && Amd.Available && !Amd.UpscalingSupported && !AmdGraphics.HasService && await _amd.StartServiceAsync())
            Changed?.Invoke();
    }

    // ---- AMD ----

    Monitoring.AmdLive? _live;
    long _liveAt = long.MinValue;
    int _liveReading;

    /// <summary>
    /// For the overlay (any thread): whether RSR and frame generation are on in the driver. The
    /// last answer comes back at once; it's asked again in the background when older than a few
    /// seconds, so a change made in AMD Software shows too. Nothing is asked unless the overlay asks.
    /// </summary>
    public Monitoring.AmdLive? Live()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _liveAt) > 5000 && Interlocked.Exchange(ref _liveReading, 1) == 0)
            _ = _amd.ReadSwitchesAsync().ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully && task.Result is var (upscaling, frameGen)) _live = new(upscaling, frameGen);
                Interlocked.Exchange(ref _liveAt, Environment.TickCount64);
                Interlocked.Exchange(ref _liveReading, 0);
            }, TaskScheduler.Default);
        return _live;
    }

    /// <summary>0 = no limit.</summary>
    public void SetFpsLimit(int fps) => _amd.SetFpsLimit(fps);
    public void SetFrameGen(bool on) => _amd.SetFrameGen(on);
    public void SetUpscaling(bool on) => _amd.SetUpscaling(on);
    public void SetFreeSync(bool on) => _amd.SetFreeSync(on);

    /// <summary>0 turns sharpening off; any other strength is remembered for the Sharpen circle.</summary>
    public void SetSharpness(int percent)
    {
        if (percent > 0 && percent != _settings.Sharpness)
        {
            _settings.Sharpness = percent;
            _settings.SaveSoon();
        }
        _amd.SetSharpness(percent);
    }

    public void SetUpscalingSharpness(int percent) => _amd.SetUpscalingSharpness(percent);

    public void ToggleSharpening() => SetSharpness(Amd.SharpeningOn ? 0 : Math.Max(10, _settings.Sharpness));

    // ---- Graphics memory (applies after a restart) ----

    /// <summary>The custom sizes, as indexes into AmdState.MemoryOptions.</summary>
    public IEnumerable<int> MemoryChoices =>
        Enumerable.Range(0, Amd.MemoryOptions.Count).Where(i => Amd.MemoryOptions[i].Custom);

    public void SetMemory(int choice) => _amd.SetMemory(choice);

    public bool RestartNeeded => _memoryAtStart >= 0 && Amd.MemoryChoice >= 0 && Amd.MemoryChoice != _memoryAtStart;

    public static void Restart()
    {
        Log.Info("Restarting Windows for the new graphics memory size");
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
    }

    // ---- Screen ----

    /// <summary>Resolutions that fill the 16:10 screen, then 16:9 ones (black bars), largest first.</summary>
    public IReadOnlyList<IReadOnlyList<(int Width, int Height)>> Resolutions
    {
        get
        {
            var sizes = Modes.Select(m => (m.Width, m.Height)).Distinct().OrderByDescending(s => s.Width).ToList();
            return new[]
            {
                sizes.Where(s => s.Width * 10 == s.Height * 16).ToList(),
                sizes.Where(s => s.Width * 9 == s.Height * 16).ToList(),
            }.Where(group => group.Count > 0).ToList();
        }
    }

    public IReadOnlyList<int> RefreshRates =>
        Modes.Where(m => m.Width == Mode.Width && m.Height == Mode.Height).Select(m => m.Hz).Distinct().OrderDescending().ToList();

    /// <summary>Keeps the refresh rate if the new resolution has it, else takes its fastest.</summary>
    public void SetResolution(int width, int height)
    {
        var rates = Modes.Where(m => m.Width == width && m.Height == height).Select(m => m.Hz).ToList();
        if (rates.Count == 0) return;
        SetMode(new DisplayMode(width, height, rates.Contains(Mode.Hz) ? Mode.Hz : rates.Max()));
    }

    public void SetRefreshRate(int hz) => SetMode(Mode with { Hz = hz });

    void SetMode(DisplayMode mode) => _display.Run(() =>
    {
        if (!DisplayModes.Apply(mode)) Log.Error($"Windows refused {mode.Width}x{mode.Height} at {mode.Hz} Hz");
        Mode = DisplayModes.Current();
        Scale = DisplayScale.Read();  // a new resolution has its own scaling steps
        Changed?.Invoke();
        return 0;
    });

    /// <summary>One of Windows' own scaling steps (applies at once).</summary>
    public void SetScale(int percent) => _display.Run(() =>
    {
        if (!DisplayScale.Apply(percent)) Log.Error($"Windows refused scaling {percent}%");
        Scale = DisplayScale.Read();
        Changed?.Invoke();
        return 0;
    });

    public void Dispose()
    {
        _amd.Dispose();
        _display.Dispose();
    }
}
