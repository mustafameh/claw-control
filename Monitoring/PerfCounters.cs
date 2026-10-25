using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClawControl.Monitoring;

/// <summary>
/// Windows performance counters (the ones Task Manager uses) for CPU, GPU and VRAM. Only the
/// counters for chosen metrics are opened. Use from the metrics thread only.
/// </summary>
public sealed class PerfCounters : IDisposable
{
    const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000;
    const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    // GPU engine instances come and go with processes; rebuild the query now and then to be sure.
    const long RebuildMs = 30_000;

    readonly double _baseMhz = ReadBaseMhz();
    IntPtr _query, _cpuUtility, _cpuPerformance, _gpu3d, _vramDedicated;
    (bool Cpu, bool Clock, bool Gpu, bool Vram) _wanted;
    long _builtAt;

    public double? CpuPercent { get; private set; }
    public double? CpuMhz { get; private set; }
    public double? GpuPercent { get; private set; }
    public double? VramBytes { get; private set; }

    public void Configure(bool cpu, bool clock, bool gpu, bool vram)
    {
        if (_wanted == (cpu, clock, gpu, vram) && _query != IntPtr.Zero) return;
        _wanted = (cpu, clock, gpu, vram);
        Rebuild();
    }

    public void Collect()
    {
        if (_query == IntPtr.Zero) return;
        if (_wanted.Gpu && Environment.TickCount64 - _builtAt > RebuildMs) Rebuild();
        if (PdhCollectQueryData(_query) != 0) return;
        if (_cpuUtility != IntPtr.Zero && Single(_cpuUtility) is double cpu) CpuPercent = Math.Clamp(cpu, 0, 100);
        if (_cpuPerformance != IntPtr.Zero && Single(_cpuPerformance) is double perf && _baseMhz > 0) CpuMhz = perf / 100 * _baseMhz;
        if (_gpu3d != IntPtr.Zero && Sum(_gpu3d) is double gpu) GpuPercent = Math.Clamp(gpu, 0, 100);
        if (_vramDedicated != IntPtr.Zero && Sum(_vramDedicated) is double vram) VramBytes = vram;
    }

    void Rebuild()
    {
        Close();
        var (cpu, clock, gpu, vram) = _wanted;
        if (!(cpu || clock || gpu || vram)) return;
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
        if (cpu) _cpuUtility = Add(@"\Processor Information(_Total)\% Processor Utility");
        if (clock) _cpuPerformance = Add(@"\Processor Information(_Total)\% Processor Performance");
        if (gpu) _gpu3d = Add(@"\GPU Engine(*engtype_3D)\Utilization Percentage");
        if (vram) _vramDedicated = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        PdhCollectQueryData(_query);  // rate counters need a first sample to compare against
        _builtAt = Environment.TickCount64;
    }

    IntPtr Add(string path) => PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;

    static double? Single(IntPtr counter) =>
        PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var value) == 0 && value.CStatus <= 1
            ? value.Double
            : null;

    /// <summary>Sum over all instances of a wildcard counter.</summary>
    static double? Sum(IntPtr counter)
    {
        uint size = 0;
        if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero) != PDH_MORE_DATA)
            return null;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, buffer) != 0)
                return null;
            double total = 0;
            int itemSize = Marshal.SizeOf<FmtItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<FmtItem>(buffer + i * itemSize);
                if (item.Value.CStatus <= 1) total += item.Value.Double;
            }
            return total;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static double ReadBaseMhz()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return key?.GetValue("~MHz") is int mhz ? mhz : 0;
    }

    void Close()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = _cpuUtility = _cpuPerformance = _gpu3d = _vramDedicated = IntPtr.Zero;
    }

    public void Dispose() => Close();

    [StructLayout(LayoutKind.Explicit)]
    struct FmtValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double Double;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FmtItem
    {
        public IntPtr Name;
        public FmtValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FmtValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);
}
