using System.Runtime.InteropServices;
using ClawControl.Core;

namespace ClawControl.Monitoring;

/// <summary>
/// Reads the chosen metrics once a second on its own low-priority thread. Each source starts
/// only when a chosen metric needs it and stops as soon as none does; when the sampler is
/// disposed its thread stops every source itself, so the caller never waits.
/// </summary>
public sealed class MetricsSampler : IDisposable
{
    const int IntervalMs = 1000;

    readonly ManualResetEventSlim _stop = new();
    readonly Func<string> _tdpText;
    readonly Func<AmdLive?> _amd;
    readonly Thread _thread;
    volatile HashSet<string> _items;

    /// <summary>Raised on the sampler thread with one reading per chosen metric, in catalog order.</summary>
    public event Action<IReadOnlyList<MetricReading>>? Updated;

    /// <param name="amd">AMD's switches as the driver has them (cached; the provider refreshes it now and then).</param>
    public MetricsSampler(IEnumerable<string> items, Func<string> tdpText, Func<AmdLive?> amd)
    {
        _items = new HashSet<string>(items);
        _tdpText = tdpText;
        _amd = amd;
        _thread = new Thread(Run) { IsBackground = true, Name = "Metrics", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void SetItems(IEnumerable<string> items) => _items = new HashSet<string>(items);

    public void Dispose() => _stop.Set();

    /// <summary>For app shutdown only: give the thread a moment to stop its sources.</summary>
    public void WaitForStop(TimeSpan timeout)
    {
        _stop.Set();
        _thread.Join(timeout);
    }

    void Run()
    {
        FirmwareSensors? firmware = null;
        PerfCounters? counters = null;
        PresentMonFps? fps = null;
        int ownPid = Environment.ProcessId, targetPid = 0;
        IntPtr targetWindow = IntPtr.Zero;
        long fpsRetryAt = 0;
        bool frameTypesRefused = false;
        try
        {
            while (!_stop.IsSet)
            {
                var items = _items;
                bool Needs(MetricSource source) => Metrics.All.Any(m => m.Source == source && items.Contains(m.Id));

                // Start only what the chosen items need; stop whatever is no longer needed.
                if (Needs(MetricSource.Fps))
                {
                    // Frame generation needs frames followed to the screen (a bit more work for PresentMon).
                    bool frameTypes = items.Contains("framegen") && !frameTypesRefused;
                    if (fps is { Failed: true, FrameTypes: true })
                    {
                        Log.Info("PresentMon can't tell generated frames apart here; FPS only");
                        frameTypesRefused = true;
                        frameTypes = false;
                    }
                    if (fps != null && (fps.Failed || fps.FrameTypes != frameTypes)) { fps.Dispose(); fps = null; }
                    if (fps == null && Environment.TickCount64 >= fpsRetryAt)
                    {
                        fps = StartFps(frameTypes);
                        if (fps == null) fpsRetryAt = Environment.TickCount64 + 30_000;
                    }
                }
                else if (fps != null) { fps.Dispose(); fps = null; }

                firmware = Needs(MetricSource.Firmware) ? firmware ?? new FirmwareSensors() : null;

                if (Needs(MetricSource.Counters))
                {
                    counters ??= new PerfCounters();
                    counters.Configure(items.Contains("cpu"), items.Contains("cpuclock"), items.Contains("gpu"), items.Contains("vram"));
                    counters.Collect();
                }
                else if (counters != null) { counters.Dispose(); counters = null; }

                // FPS belongs to the app in front; ignore our own panel coming to the front.
                var window = GetForegroundWindow();
                GetWindowThreadProcessId(window, out uint foreground);
                if (foreground != 0 && foreground != ownPid) (targetPid, targetWindow) = ((int)foreground, window);

                var readings = new List<MetricReading>();
                foreach (var metric in Metrics.All)
                {
                    if (!items.Contains(metric.Id)) continue;
                    string? value;
                    bool lit = false;
                    try { (value, lit) = Read(metric.Id, firmware, counters, fps, targetPid, targetWindow); }
                    catch (Exception ex) { value = null; Log.Error($"Reading {metric.Id}", ex); }
                    readings.Add(new MetricReading(metric.Short, value ?? "–", lit));
                }
                Updated?.Invoke(readings);
                _stop.Wait(IntervalMs);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Performance overlay readings stopped", ex);
        }
        finally
        {
            fps?.Dispose();
            counters?.Dispose();
        }
    }

    static PresentMonFps? StartFps(bool frameTypes)
    {
        try { return new PresentMonFps(frameTypes); }
        catch (Exception ex) { Log.Error("Starting the FPS counter", ex); return null; }
    }

    (string? Value, bool Lit) Read(string id, FirmwareSensors? firmware, PerfCounters? counters, PresentMonFps? fps, int targetPid, IntPtr targetWindow) => id switch
    {
        "framegen" => FrameGen(fps, targetPid),
        "upscale" => Upscaling(targetWindow),
        _ => (ReadText(id, firmware, counters, fps, targetPid), false),
    };

    /// <summary>"+58" (frames per second AMD's frame generation adds to the game's, green), "Idle"
    /// (on, but adding none: not full screen, fast motion, an unsupported game), "Off".</summary>
    (string?, bool) FrameGen(PresentMonFps? fps, int targetPid)
    {
        if (_amd() is { FrameGenOn: false }) return ("Off", false);
        if (fps?.GeneratedFor(targetPid) is not double generated) return (null, false);
        return generated >= 1 ? ($"+{generated:0}", true) : ("Idle", false);
    }

    /// <summary>"RSR 1280×800" (the driver upscales a full-screen game, green), "Scaled 1280×800"
    /// (a lower resolution simply stretched), "Native".</summary>
    (string?, bool) Upscaling(IntPtr targetWindow)
    {
        var (kind, mode) = Scaling.Read(targetWindow, _amd()?.UpscalingOn == true);
        string size = $"{mode.Width}×{mode.Height}";
        return kind switch
        {
            ScalingKind.Rsr => ($"RSR {size}", true),
            ScalingKind.Scaled => ($"Scaled {size}", false),
            _ => ("Native", false),
        };
    }

    string? ReadText(string id, FirmwareSensors? firmware, PerfCounters? counters, PresentMonFps? fps, int targetPid) => id switch
    {
        "fps" => fps?.FpsFor(targetPid) is double f ? $"{f:0}" : null,
        "power" => firmware?.BatteryWatts() is double w ? FormatPower(w) : null,
        "cputemp" => firmware?.CpuTemperature() is int t ? $"{t} °C" : null,
        "fan" => firmware?.FanRpm() is int rpm ? $"{rpm} rpm" : null,
        "cpu" => counters?.CpuPercent is double c ? $"{c:0}%" : null,
        "cpuclock" => counters?.CpuMhz is double mhz ? $"{mhz / 1000:0.00} GHz" : null,
        "gpu" => counters?.GpuPercent is double g ? $"{g:0}%" : null,
        "vram" => counters?.VramBytes is double v ? $"{v / (1024.0 * 1024 * 1024):0.0} GB" : null,
        "ram" => SystemInfo.Ram() is { } ram ? $"{ram.UsedGb:0.0} / {ram.TotalGb:0} GB" : null,
        "battery" => SystemInfo.Battery() is { } b
            ? $"{b.Percent}%" + (b.Charging ? " charging" : b.MinutesLeft is int m ? $" {m / 60}:{m % 60:00}" : "")
            : null,
        "tdp" => _tdpText(),
        "time" => DateTime.Now.ToString("HH:mm"),
        _ => null,
    };

    /// <summary>Negative battery watts are what the Claw is using; positive means it is charging.</summary>
    static string FormatPower(double watts) => watts switch
    {
        < -0.3 => $"{-watts:0.0} W",
        > 0.3 => $"+{watts:0.0} W charging",
        _ => "on charger",
    };

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
