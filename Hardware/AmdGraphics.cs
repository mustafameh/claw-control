using ClawControl.Core;

namespace ClawControl.Hardware;

/// <summary>A graphics memory size the driver offers: its presets (Minimum / Medium / High) or a custom size.</summary>
public sealed record MemoryOption(bool Custom, double Gb);

/// <summary>Everything the Graphics tab shows from AMD's driver, read in one go.</summary>
public sealed record AmdState(
    bool Available,
    bool FpsLimitSupported, bool FpsLimitOn, int FpsLimit,
    bool FrameGenSupported, bool FrameGenOn,
    bool UpscalingSupported, bool UpscalingOn, int UpscalingSharpness,
    bool SharpeningSupported, bool SharpeningOn, int Sharpness,
    bool FreeSyncSupported, bool FreeSyncOn,
    IReadOnlyList<MemoryOption> MemoryOptions, int MemoryChoice)
{
    public static readonly AmdState Unknown = new(false, false, false, 0, false, false, false, false, 0, false, false, 0, false, false, [], -1);
}

/// <summary>
/// AMD driver settings for the Graphics tab: FPS limit (Frame Rate Target Control), AMD Fluid
/// Motion Frames, Radeon Super Resolution, Radeon Image Sharpening, FreeSync on the built-in
/// screen and Variable Graphics Memory. All ADLX calls run on one worker thread; ADLX starts the
/// first time something is asked and stays connected until Dispose. Nothing runs on its own.
/// </summary>
public sealed class AmdGraphics : IDisposable
{
    readonly HardwareWorker _worker = new();
    readonly LatestOnly<int> _fps, _sharpness, _upscalingSharpness;
    IntPtr _system, _gpu, _settings, _settings1, _chill, _frtc, _ris, _rsr, _afmf, _display, _freeSync, _system3, _vgm, _optionList;
    readonly List<IntPtr> _options = new();
    bool _started, _failed;

    /// <summary>The settings after a change went out (worker thread).</summary>
    public event Action<AmdState>? Changed;

    public AmdGraphics()
    {
        // Bars: holding the D-pad sends only the newest value. The driver refuses a value while
        // the feature is off, so switch it on first.
        _fps = new LatestOnly<int>(_worker, "Setting the FPS limit", fps => Apply(() =>
        {
            if (Adlx.Supported(_chill, Adlx.ChillSupported))
            {
                // Radeon Chill with min = max is a plain cap, and unlike Frame Rate Target Control
                // it also works in DirectX 12 / Vulkan games. FRTC stays off so the two don't fight.
                if (Adlx.Supported(_frtc, Adlx.FrtcEnabled)) Adlx.SetBool(_frtc, Adlx.FrtcSetEnabled, false);
                Adlx.SetBool(_chill, Adlx.ChillSetEnabled, fps > 0);
                if (fps == 0) return;
                var range = Adlx.Range(_chill, Adlx.ChillRange);
                int cap = Math.Clamp(fps, range.Min, range.Max);
                // Keep min ≤ max at every step: raise the max first, lower the min first.
                if (cap >= Adlx.Int(_chill, Adlx.ChillMaxFps))
                {
                    Adlx.SetInt(_chill, Adlx.ChillSetMax, cap);
                    Adlx.SetInt(_chill, Adlx.ChillSetMin, cap);
                }
                else
                {
                    Adlx.SetInt(_chill, Adlx.ChillSetMin, cap);
                    Adlx.SetInt(_chill, Adlx.ChillSetMax, cap);
                }
                return;
            }
            Adlx.SetBool(_frtc, Adlx.FrtcSetEnabled, fps > 0);
            if (fps == 0) return;
            var frtcRange = Adlx.Range(_frtc, Adlx.FrtcRange);
            Adlx.SetInt(_frtc, Adlx.FrtcSetFps, Math.Clamp(fps, frtcRange.Min, frtcRange.Max));
        }));
        _sharpness = new LatestOnly<int>(_worker, "Setting sharpening", percent => Apply(() =>
        {
            Adlx.SetBool(_ris, Adlx.RisSetEnabled, percent > 0);
            if (percent == 0) return;
            Adlx.SetInt(_ris, Adlx.RisSetSharpness, Scale(percent, Adlx.Range(_ris, Adlx.RisRange)));
        }));
        // Upscaling's own sharpening step (only set while upscaling is on).
        _upscalingSharpness = new LatestOnly<int>(_worker, "Setting upscaling sharpness", percent => Apply(() =>
            Adlx.SetInt(_rsr, Adlx.RsrSetSharpness, Scale(percent, Adlx.Range(_rsr, Adlx.RsrRange)))));
    }

    public Task<AmdState> ReadAsync() => _worker.Run(Read);

    /// <summary>Just whether upscaling (RSR) and frame generation are on (two driver calls, for the overlay).</summary>
    public Task<(bool Upscaling, bool FrameGen)?> ReadSwitchesAsync() => _worker.Run<(bool, bool)?>(() =>
    {
        if (!Start()) return null;
        try
        {
            return (Adlx.Supported(_rsr, Adlx.RsrSupported) && Adlx.Bool(_rsr, Adlx.RsrEnabled), Adlx.Supported(_afmf, Adlx.ToggleEnabled));
        }
        catch (Adlx.Exception) { return null; }
    });

    /// <summary>0 turns the limit off.</summary>
    public void SetFpsLimit(int fps) => _fps.Post(fps);

    /// <summary>0 turns image sharpening off; otherwise its strength in percent.</summary>
    public void SetSharpness(int percent) => _sharpness.Post(percent);

    /// <summary>How much upscaling sharpens what it stretches, in percent.</summary>
    public void SetUpscalingSharpness(int percent) => _upscalingSharpness.Post(percent);

    public void SetFrameGen(bool on) => Queue("frame generation", () => Adlx.SetBool(_afmf, Adlx.ToggleSetEnabled, on));

    public void SetUpscaling(bool on) => Queue("upscaling", () => Adlx.SetBool(_rsr, Adlx.RsrSetEnabled, on));

    public void SetFreeSync(bool on) => Queue("FreeSync", () => Adlx.SetBool(_freeSync, Adlx.ToggleSetEnabled, on));

    /// <summary>An index into <see cref="AmdState.MemoryOptions"/>; takes effect after a restart.</summary>
    public void SetMemory(int choice) => Queue("graphics memory", () => Adlx.Call(_vgm, Adlx.VgmSetOption, _options[choice]));

    void Queue(string what, Action change) => _worker.Run(() =>
    {
        try { Apply(change); }
        catch (Exception ex) { Log.Error($"Setting {what}", ex); }
        return 0;
    });

    /// <summary>On the worker: makes the change, then reports the settings as they are now.</summary>
    void Apply(Action change)
    {
        if (!Start()) return;
        try { change(); }
        finally { Changed?.Invoke(Read()); }
    }

    /// <summary>Sharpness is a percentage in the driver too (image sharpening: 10-100 in steps of 10).</summary>
    static int Scale(int percent, Adlx.IntRange range)
    {
        int value = Math.Clamp(percent, range.Min, range.Max);
        if (range.Step > 1) value = range.Min + (value - range.Min + range.Step / 2) / range.Step * range.Step;
        return Math.Clamp(value, range.Min, range.Max);
    }

    AmdState Read()
    {
        if (!Start()) return AmdState.Unknown;
        try
        {
            bool frtc = Adlx.Supported(_frtc, Adlx.FrtcSupported);
            bool chill = Adlx.Supported(_chill, Adlx.ChillSupported);
            bool rsr = Adlx.Supported(_rsr, Adlx.RsrSupported);
            bool ris = Adlx.Supported(_ris, Adlx.RisSupported);
            bool risOn = ris && Adlx.Bool(_ris, Adlx.RisEnabled);
            bool vgm = Adlx.Supported(_vgm, Adlx.VgmSupported);
            int choice = -1;
            if (vgm)
            {
                IntPtr current = Adlx.Get(_vgm, Adlx.VgmGetOption);
                choice = Options().FindIndex(o => SameOption(o, Describe(current)));
                Adlx.Free(current);
            }
            return new AmdState(true,
                chill || frtc,
                chill ? Adlx.Bool(_chill, Adlx.ChillEnabled) : frtc && Adlx.Bool(_frtc, Adlx.FrtcEnabled),
                chill ? Adlx.Int(_chill, Adlx.ChillMaxFps) : frtc ? Adlx.Int(_frtc, Adlx.FrtcFps) : 0,
                Adlx.Supported(_afmf, Adlx.ToggleSupported), Adlx.Supported(_afmf, Adlx.ToggleEnabled),
                rsr, rsr && Adlx.Bool(_rsr, Adlx.RsrEnabled), rsr ? Adlx.Int(_rsr, Adlx.RsrSharpness) : 0,
                ris, risOn, risOn ? Adlx.Int(_ris, Adlx.RisSharpness) : 0,
                Adlx.Supported(_freeSync, Adlx.ToggleSupported), Adlx.Supported(_freeSync, Adlx.ToggleEnabled),
                vgm ? Options() : [], choice);
        }
        catch (Exception ex)
        {
            Log.Error("Reading AMD graphics settings", ex);
            return AmdState.Unknown;
        }
    }

    List<MemoryOption> Options()
    {
        if (_options.Count == 0)
        {
            _optionList = Adlx.Get(_vgm, Adlx.VgmGetOptions);
            for (uint i = 0; i < Adlx.Size(_optionList); i++) _options.Add(Adlx.At(_optionList, i));
        }
        return _options.Select(Describe).ToList();
    }

    // Option mode 0 = one of the driver's presets, 1 = custom size.
    static MemoryOption Describe(IntPtr option) =>
        new(Adlx.Int(option, Adlx.OptionMode) == 1, Adlx.Double(option, Adlx.OptionCarvedGb));

    static bool SameOption(MemoryOption a, MemoryOption b) => a.Custom == b.Custom && Math.Abs(a.Gb - b.Gb) < 0.01;

    /// <summary>Connects once; gets each feature the driver has (missing ones stay 0).</summary>
    bool Start()
    {
        if (_started) return true;
        if (_failed) return false;
        try
        {
            _system = Adlx.Start();
            IntPtr gpus = Adlx.Get(_system, Adlx.SystemGetGpus);
            if (Adlx.Size(gpus) > 0) _gpu = Adlx.At(gpus, 0);
            Adlx.Free(gpus);

            _settings = Adlx.Get(_system, Adlx.SystemGet3DSettings);
            _frtc = Try(() => Adlx.Get(_settings, Adlx.GetFrameRateTargetControl, _gpu));
            _chill = Try(() => Adlx.Get(_settings, Adlx.GetChill, _gpu));
            _ris = Try(() => Adlx.Get(_settings, Adlx.GetImageSharpening, _gpu));
            _rsr = Try(() => Adlx.Get(_settings, Adlx.GetRadeonSuperResolution));
            _settings1 = Adlx.Query(_settings, "IADLX3DSettingsServices1");
            if (_settings1 != 0) _afmf = Try(() => Adlx.Get(_settings1, Adlx.GetFluidMotionFrames));

            _display = BuiltInDisplay();
            if (_display != 0)
            {
                IntPtr displays = Adlx.Get(_system, Adlx.SystemGetDisplayServices);
                _freeSync = Try(() => Adlx.Get(displays, Adlx.GetFreeSync, _display));
                Adlx.Free(displays);
            }

            _system3 = Adlx.Query(_system, "IADLXSystem3", Adlx.SystemQueryInterface);
            if (_system3 != 0) _vgm = Try(() => Adlx.Get(_system3, Adlx.GetVariableGraphicsMemory));
            _started = true;
            Log.Info($"AMD settings ready (FPS limit {_chill != 0 || _frtc != 0}, frame gen {_afmf != 0}, upscaling {_rsr != 0}, " +
                $"sharpening {_ris != 0}, FreeSync {_freeSync != 0}, graphics memory {_vgm != 0})");
        }
        catch (Exception ex)
        {
            _failed = true;
            Log.Error("Connecting to AMD's driver settings (ADLX)", ex);
        }
        return _started;
    }

    /// <summary>The Claw's own screen (eDP), or the first display.</summary>
    IntPtr BuiltInDisplay()
    {
        IntPtr services = Adlx.Get(_system, Adlx.SystemGetDisplayServices);
        IntPtr list = Adlx.Get(services, Adlx.GetDisplays);
        IntPtr chosen = 0;
        for (uint i = 0; i < Adlx.Size(list); i++)
        {
            IntPtr display = Adlx.At(list, i);
            if (chosen == 0 || Adlx.Int(display, Adlx.DisplayConnectorType) == Adlx.ConnectorEdp)
            {
                Adlx.Free(chosen);
                chosen = display;
                if (Adlx.Int(display, Adlx.DisplayConnectorType) == Adlx.ConnectorEdp) break;
            }
            else Adlx.Free(display);
        }
        Adlx.Free(list);
        Adlx.Free(services);
        return chosen;
    }

    static IntPtr Try(Func<IntPtr> get)
    {
        try { return get(); }
        catch (Adlx.Exception) { return 0; }
    }

    public void Dispose()
    {
        _worker.Run(() =>
        {
            if (!_started) return true;
            foreach (var p in _options) Adlx.Free(p);
            foreach (var p in new[] { _optionList, _vgm, _system3, _freeSync, _display, _afmf, _settings1, _rsr, _ris, _chill, _frtc, _settings, _gpu })
                Adlx.Free(p);
            Adlx.Terminate();
            return true;
        }).Wait(TimeSpan.FromSeconds(2));
        _worker.Dispose();
    }
}
