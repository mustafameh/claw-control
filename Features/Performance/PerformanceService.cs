using System.Windows.Threading;
using ClawControl.Core;
using ClawControl.Hardware;
using Microsoft.Win32;

namespace ClawControl.Features.Performance;

public enum PerfMode { SuperBattery, Balanced, Performance, Custom }

public readonly record struct PowerLimits(int Spl, int Sppt, int Fppt)
{
    public override string ToString() => $"{Spl} / {Sppt} / {Fppt} W";
}

/// <summary>
/// TDP modes and limits for the Claw A8 (MSI model 1T8K). Presets, limits and the write
/// sequence match what MSI Center M uses (docs/HARDWARE.md), so the firmware sees exactly what
/// it would see from MSI.
///
/// Lives on the UI thread; every firmware call goes through the HardwareWorker so the UI
/// updates instantly and the hardware catches up in the background.
/// </summary>
public sealed class PerformanceService
{
    public const int MaxSpl = 35, MaxSppt = 50, MaxFppt = 60;
    // MSI itself briefly writes 7 W and 9 W while changing limits, so the firmware accepts them.
    public const int MinSpl = 7, MinSppt = 9, MinFppt = 9;

    public static readonly IReadOnlyDictionary<PerfMode, PowerLimits> Presets = new Dictionary<PerfMode, PowerLimits>
    {
        [PerfMode.SuperBattery] = new(15, 28, 33),
        [PerfMode.Balanced] = new(20, 33, 43),
        [PerfMode.Performance] = new(28, 45, 55),
    };

    const string MsiScenarioKey = @"SOFTWARE\WOW6432Node\MSI\MSI Center M\Component\User Scenario";

    readonly HardwareWorker _hardware;
    readonly MsiAcpi _acpi;
    readonly Settings _settings;
    readonly DispatcherTimer _applyDelay;
    int _requestVersion;
    // Nothing is written to the firmware until startup has read what's there.
    bool _ready;

    public PerfMode Mode { get; private set; } = PerfMode.Custom;
    public PowerLimits Target { get; private set; }
    public PowerLimits? Actual { get; private set; }
    public string Status { get; private set; } = "";

    /// <summary>Raised on the UI thread whenever anything above changes.</summary>
    public event Action? Changed;

    public PerformanceService(HardwareWorker hardware, MsiAcpi acpi, Settings settings)
    {
        _hardware = hardware;
        _acpi = acpi;
        _settings = settings;
        Enum.TryParse(settings.Mode, out PerfMode mode);
        Mode = mode;
        Target = LimitsFor(mode);

        // Slider drags produce many changes; only the value you stop on is sent to the firmware.
        _applyDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _applyDelay.Tick += (_, _) => { _applyDelay.Stop(); Apply(); };

        // MSI re-applies its own settings after sleep and on plug/unplug; put ours back afterwards.
        var ui = Dispatcher.CurrentDispatcher;
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode is PowerModes.Resume or PowerModes.StatusChange)
                ui.BeginInvoke(async () =>
                {
                    await Task.Delay(4000);
                    Log.Info($"Re-applying after power event ({e.Mode})");
                    Apply();
                });
        };
    }

    public PowerLimits CustomLimits => new(_settings.CustomSpl, _settings.CustomSppt, _settings.CustomFppt);

    PowerLimits LimitsFor(PerfMode mode) => mode == PerfMode.Custom ? CustomLimits : Presets[mode];

    /// <summary>
    /// On first run, adopt whatever is active instead of changing anything.
    /// Afterwards, make sure the firmware matches the saved choice (MSI may have changed it at boot).
    /// </summary>
    public async Task InitializeAsync(bool firstRun)
    {
        try
        {
            var current = await _hardware.Run(ReadLimits);
            Actual = current;
            _ready = current != null;
            if (firstRun && current is PowerLimits now)
            {
                var msiManual = await _hardware.Run(ReadMsiManualValues);
                var match = Presets.Where(p => p.Value == now).Select(p => (PerfMode?)p.Key).FirstOrDefault();
                var custom = match == null ? now : msiManual ?? CustomLimits;
                (_settings.CustomSpl, _settings.CustomSppt, _settings.CustomFppt) = (custom.Spl, custom.Sppt, custom.Fppt);
                Mode = match ?? PerfMode.Custom;
                Target = now;
                _settings.Mode = Mode.ToString();
                _settings.SaveSoon();
                Status = "Using your current limits";
                Log.Info($"First run: adopted {Mode} {now}");
                Changed?.Invoke();
            }
            else if (current != Target)
            {
                Log.Info($"Firmware has {current}, restoring {Mode} {Target}");
                Apply();
            }
            else
            {
                Status = "Applied";
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading power limits at startup", ex);
            Status = "Couldn't read the firmware. See log.";
            Changed?.Invoke();
        }
    }

    public void SelectMode(PerfMode mode)
    {
        Mode = mode;
        Target = LimitsFor(mode);
        _settings.Mode = mode.ToString();
        _settings.SaveSoon();
        Changed?.Invoke();
        _applyDelay.Stop();
        Apply();
    }

    /// <summary>A slider moved: switch to Custom with these limits and apply once the slider settles.</summary>
    public void SetCustom(PowerLimits limits)
    {
        (_settings.CustomSpl, _settings.CustomSppt, _settings.CustomFppt) = (limits.Spl, limits.Sppt, limits.Fppt);
        _settings.Mode = nameof(PerfMode.Custom);
        _settings.SaveSoon();
        Mode = PerfMode.Custom;
        Target = limits;
        Status = "Applying…";
        Changed?.Invoke();
        _applyDelay.Stop();
        _applyDelay.Start();
    }

    /// <summary>
    /// Keeps the three limits in order (sustained ≤ slow boost ≤ fast boost) by pushing the
    /// others along with whichever slider moved, and keeps each inside MSI's range.
    /// </summary>
    public static PowerLimits Normalize(int spl, int sppt, int fppt, int moved)
    {
        spl = Math.Clamp(spl, MinSpl, MaxSpl);
        sppt = Math.Clamp(sppt, MinSppt, MaxSppt);
        fppt = Math.Clamp(fppt, MinFppt, MaxFppt);
        switch (moved)
        {
            case 0: sppt = Math.Max(sppt, spl); fppt = Math.Max(fppt, sppt); break;
            case 1: spl = Math.Min(spl, sppt); fppt = Math.Max(fppt, sppt); break;
            default: sppt = Math.Min(sppt, fppt); spl = Math.Min(spl, sppt); break;
        }
        return new PowerLimits(spl, sppt, fppt);
    }

    /// <summary>Panel opened: re-read the firmware in the background and fix it if something else changed it.</summary>
    public async void Refresh()
    {
        if (!_ready) return;
        int version = _requestVersion;
        try
        {
            var current = await _hardware.Run(ReadLimits);
            if (version != _requestVersion) return;
            Actual = current;
            if (current != null && current != Target)
            {
                Log.Info($"Limits were changed outside Claw Control ({current}); restoring {Target}");
                Apply();
            }
            else
            {
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Refreshing power limits", ex);
        }
    }

    async void Apply()
    {
        if (!_ready)
        {
            Log.Info("Ignoring apply before startup finished reading the firmware");
            return;
        }
        int version = ++_requestVersion;
        var (mode, target) = (Mode, Target);
        Status = "Applying…";
        Changed?.Invoke();
        try
        {
            // If newer requests are already queued, skip straight to the latest one.
            var result = await _hardware.Run(() =>
                version == Volatile.Read(ref _requestVersion) ? WriteToFirmware(mode, target) : null);
            if (version != _requestVersion || result is not PowerLimits actual) return;
            Actual = actual;
            Status = actual == target ? "Applied" : $"Firmware reports {actual}";
            if (actual != target) Log.Error($"Asked for {target}, firmware reports {actual}");
        }
        catch (Exception ex)
        {
            Log.Error("Applying power limits", ex);
            Status = "Couldn't apply. See log.";
        }
        Changed?.Invoke();
    }

    // ---- Hardware thread only below ----

    PowerLimits? ReadLimits()
    {
        var spl = _acpi.Get(MsiAcpi.Spl);
        var sppt = _acpi.Get(MsiAcpi.Sppt);
        var fppt = _acpi.Get(MsiAcpi.Fppt);
        return spl is byte a && sppt is byte b && fppt is byte c ? new PowerLimits(a, b, c) : null;
    }

    PowerLimits? WriteToFirmware(PerfMode mode, PowerLimits target)
    {
        var current = ReadLimits() ?? throw new InvalidOperationException("Firmware did not report current limits");

        // MSI's order: drop sustained and slow boost to a floor first so the three limits never
        // cross while changing, then write fast boost, slow boost and sustained.
        if (target.Spl != current.Spl) _acpi.Set(MsiAcpi.Spl, MinSpl);
        if (target.Sppt != current.Sppt) _acpi.Set(MsiAcpi.Sppt, MinSppt);
        if (target.Fppt != current.Fppt) _acpi.Set(MsiAcpi.Fppt, (byte)Math.Min(target.Fppt, MaxFppt));
        if (target.Sppt != current.Sppt) _acpi.Set(MsiAcpi.Sppt, (byte)Math.Min(target.Sppt, MaxSppt));
        if (target.Spl != current.Spl) _acpi.Set(MsiAcpi.Spl, (byte)Math.Min(target.Spl, MaxSpl));

        // Firmware power/fan profile MSI pairs with each mode. Only touch it if the firmware
        // reports the profile feature as present and active (top two bits set).
        byte profile = mode switch { PerfMode.SuperBattery => 0xC2, PerfMode.Balanced => 0xC1, _ => 0xC4 };
        if (_acpi.Get(MsiAcpi.ShiftMode) is byte shift && (shift & 0xC0) == 0xC0 && shift != profile)
            _acpi.Set(MsiAcpi.ShiftMode, profile);

        WindowsPowerMode.Set(mode switch
        {
            PerfMode.SuperBattery => WindowsPowerMode.BestEfficiency,
            PerfMode.Balanced => WindowsPowerMode.Balanced,
            _ => WindowsPowerMode.BestPerformance,
        });

        WriteMsiManualValues(target);
        var actual = ReadLimits();
        Log.Info($"Applied {mode} {target}, firmware reports {actual}");
        return actual;
    }

    /// <summary>
    /// Keep MSI's saved Manual values equal to ours, so when MSI re-applies its settings
    /// (after sleep, on plug/unplug) it writes our numbers instead of fighting them.
    /// </summary>
    void WriteMsiManualValues(PowerLimits limits)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MsiScenarioKey, writable: true);
            if (key == null) return;
            key.SetValue("ManualSPL", limits.Spl, RegistryValueKind.DWord);
            key.SetValue("ManualSPPT", limits.Sppt, RegistryValueKind.DWord);
            key.SetValue("ManualFPPT", limits.Fppt, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Log.Error("Updating MSI's saved manual values", ex);
        }
    }

    PowerLimits? ReadMsiManualValues()
    {
        using var key = Registry.LocalMachine.OpenSubKey(MsiScenarioKey);
        return key?.GetValue("ManualSPL") is int a && key.GetValue("ManualSPPT") is int b && key.GetValue("ManualFPPT") is int c
            ? Normalize(a, b, c, moved: 0)
            : null;
    }
}
