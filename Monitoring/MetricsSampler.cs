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
    readonly Thread _thread;
    volatile HashSet<string> _items;

    /// <summary>Raised on the sampler thread with one reading per chosen metric, in catalog order.</summary>
    public event Action<IReadOnlyList<MetricReading>>? Updated;

    public MetricsSampler(IEnumerable<string> items, Func<string> tdpText)
    {
        _items = new HashSet<string>(items);
        _tdpText = tdpText;
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
        long fpsRetryAt = 0;
        try
        {
            while (!_stop.IsSet)
            {
                var items = _items;
                bool Needs(MetricSource source) => Metrics.All.Any(m => m.Source == source && items.Contains(m.Id));

                // Start only what the chosen items need; stop whatever is no longer needed.
                if (Needs(MetricSource.Fps))
                {
                    if (fps == null && Environment.TickCount64 >= fpsRetryAt)
                    {
                        fps = StartFps();
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
                int foreground = ForegroundProcessId();
                if (foreground != 0 && foreground != ownPid) targetPid = foreground;

                var readings = new List<MetricReading>();
                foreach (var metric in Metrics.All)
                {
                    if (!items.Contains(metric.Id)) continue;
                    string value;
                    try { value = Read(metric.Id, firmware, counters, fps, targetPid) ?? "–"; }
                    catch (Exception ex) { value = "–"; Log.Error($"Reading {metric.Id}", ex); }
                    readings.Add(new MetricReading(metric.Short, value));
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

    static PresentMonFps? StartFps()
    {
        try { return new PresentMonFps(); }
        catch (Exception ex) { Log.Error("Starting the FPS counter", ex); return null; }
    }

    string? Read(string id, FirmwareSensors? firmware, PerfCounters? counters, PresentMonFps? fps, int targetPid) => id switch
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

    static int ForegroundProcessId()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return (int)pid;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
