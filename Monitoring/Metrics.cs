namespace ClawControl.Monitoring;

/// <summary>Where a metric's value comes from. A source only runs while a chosen metric needs it.</summary>
public enum MetricSource { Fps, Firmware, Counters, System, Cached }

/// <param name="Id">Stable id stored in settings.</param>
/// <param name="Label">Name in the overlay settings.</param>
/// <param name="Short">Label drawn on the overlay itself.</param>
public sealed record Metric(string Id, string Label, string Short, MetricSource Source);

public readonly record struct MetricReading(string Label, string Value);

public static class Metrics
{
    public static readonly IReadOnlyList<Metric> All = new Metric[]
    {
        new("fps", "FPS", "FPS", MetricSource.Fps),
        new("power", "Power", "PWR", MetricSource.Firmware),
        new("cputemp", "Temp", "TEMP", MetricSource.Firmware),
        new("fan", "Fan", "FAN", MetricSource.Firmware),
        new("cpu", "CPU", "CPU", MetricSource.Counters),
        new("cpuclock", "CPU clock", "CLK", MetricSource.Counters),
        new("gpu", "GPU", "GPU", MetricSource.Counters),
        new("vram", "VRAM", "VRAM", MetricSource.Counters),
        new("ram", "RAM", "RAM", MetricSource.System),
        new("battery", "Battery", "BAT", MetricSource.System),
        new("tdp", "TDP", "TDP", MetricSource.Cached),
        new("time", "Time", "TIME", MetricSource.Cached),
    };

    public static readonly string[] Defaults = { "fps", "power", "cputemp", "battery" };
}
