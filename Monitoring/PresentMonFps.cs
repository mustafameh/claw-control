using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ClawControl.Core;

namespace ClawControl.Monitoring;

/// <summary>
/// FPS from Intel PresentMon, which reads Windows' frame-presentation events (ETW) at system level,
/// so it works for every graphics API and needs nothing inside the game. PresentMon runs only while
/// this object exists, inside a job object that closes it if Claw Control ever exits unexpectedly.
/// Display, input and GPU tracking are switched off to keep its overhead to the minimum FPS needs.
/// </summary>
public sealed class PresentMonFps : IDisposable
{
    const string SessionName = "ClawControl";
    // Frames from the last second make up the FPS; an app that sent nothing for this long shows 0.
    const double WindowSeconds = 1.0;
    const long IdleMs = 2000;

    readonly object _gate = new();
    readonly Dictionary<int, FrameWindow> _frames = new();
    readonly Process _process;
    readonly IntPtr _job;

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "Tools", "PresentMon.exe");

    public PresentMonFps()
    {
        var info = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[]
        {
            "--output_stdout", "--no_console_stats", "--v1_metrics",
            "--no_track_display", "--no_track_input", "--no_track_gpu",
            "--exclude", "dwm.exe", "--exclude", "ClawControl.exe",
            "--session_name", SessionName, "--stop_existing_session",
        })
            info.ArgumentList.Add(arg);

        _process = Process.Start(info) ?? throw new InvalidOperationException("PresentMon did not start");
        _job = KillWithClawControl(_process);
        try { _process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info($"PresentMon: {e.Data}"); };
        _process.BeginErrorReadLine();
        new Thread(Read) { IsBackground = true, Name = "PresentMon reader", Priority = ThreadPriority.BelowNormal }.Start();
        Log.Info("FPS counter started (PresentMon)");
    }

    /// <summary>Frames per second of a process over the last second, 0 if it is not drawing, null if never seen.</summary>
    public double? FpsFor(int processId)
    {
        lock (_gate)
        {
            if (!_frames.TryGetValue(processId, out var window)) return null;
            if (Environment.TickCount64 - window.LastArrivalMs > IdleMs) return 0;
            return window.Fps;
        }
    }

    void Read()
    {
        try
        {
            var output = _process.StandardOutput;
            var header = output.ReadLine()?.Split(',');
            if (header == null) return;
            int pidColumn = Array.IndexOf(header, "ProcessID");
            int timeColumn = Array.IndexOf(header, "TimeInSeconds");
            int frameColumn = Array.IndexOf(header, "msBetweenPresents");
            if (pidColumn < 0 || timeColumn < 0 || frameColumn < 0)
            {
                Log.Error($"Unexpected PresentMon output: {string.Join(",", header)}");
                return;
            }
            int needed = Math.Max(pidColumn, Math.Max(timeColumn, frameColumn));

            string? line;
            while ((line = output.ReadLine()) != null)
            {
                var parts = line.Split(',');
                if (parts.Length <= needed
                    || !int.TryParse(parts[pidColumn], out int pid)
                    || !double.TryParse(parts[timeColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out double time)
                    || !double.TryParse(parts[frameColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
                    continue;
                lock (_gate)
                {
                    if (!_frames.TryGetValue(pid, out var window)) _frames[pid] = window = new FrameWindow();
                    window.Add(time, ms);
                }
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            Log.Error("Reading PresentMon output", ex);
        }
        catch
        {
            // Stopping closes the output under the reader; that's expected.
        }
    }

    volatile bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        try { if (!_process.HasExited) _process.Kill(); } catch { }
        _process.Dispose();
        CloseHandle(_job);
        // Make sure PresentMon's event session is gone too, not just the process.
        try
        {
            var cleanup = new ProcessStartInfo(ExePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--terminate_existing_session", "--session_name", SessionName }) cleanup.ArgumentList.Add(arg);
            using var p = Process.Start(cleanup);
            p?.WaitForExit(3000);
        }
        catch { }
        Log.Info("FPS counter stopped");
    }

    sealed class FrameWindow
    {
        readonly Queue<(double Time, double Ms)> _recent = new();
        double _totalMs;
        public long LastArrivalMs { get; private set; }

        public void Add(double time, double ms)
        {
            _recent.Enqueue((time, ms));
            _totalMs += ms;
            while (_recent.Count > 1 && time - _recent.Peek().Time > WindowSeconds)
                _totalMs -= _recent.Dequeue().Ms;
            LastArrivalMs = Environment.TickCount64;
        }

        public double? Fps => _totalMs > 0 ? _recent.Count * 1000 / _totalMs : null;
    }

    // ---- Job object: Windows closes PresentMon when Claw Control's handle goes away ----

    static IntPtr KillWithClawControl(Process process)
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        var limits = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        int size = Marshal.SizeOf<ExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            SetInformationJobObject(job, 9, buffer, (uint)size); // JobObjectExtendedLimitInformation
            AssignProcessToJobObject(job, process.Handle);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
