using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ClawControl.Core;

namespace ClawControl.Monitoring;

/// <summary>
/// FPS from Intel PresentMon, which reads Windows' frame-presentation events (ETW) at system level,
/// so it works for every graphics API and needs nothing inside the game. PresentMon runs only while
/// this object exists, inside a job object that closes it if Claw Control ever exits unexpectedly.
/// Input and GPU tracking are switched off to keep its overhead to the minimum needed.
///
/// With <c>frameTypes</c> it also follows each frame to the screen and says who made it: the game,
/// or AMD's frame generation (AFMF), which the driver adds between the game's frames. That's how
/// the overlay knows frame generation is really at work, not just switched on. Without it,
/// display tracking stays off (the cheapest way to count FPS).
/// </summary>
public sealed class PresentMonFps : IDisposable
{
    const string SessionName = "ClawControl";
    // Frames from the last second make up the FPS; an app that sent nothing for this long shows 0.
    const double WindowMs = 1000;
    const long IdleMs = 2000;
    /// <summary>Generated frames are counted over this long (PresentMon hands out rows in bursts).</summary>
    const long GeneratedWindowMs = 2000;

    readonly object _gate = new();
    readonly Dictionary<int, FrameWindow> _frames = new();
    readonly HashSet<string> _typesSeen = new();
    readonly Process _process;
    readonly IntPtr _job;

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "Tools", "PresentMon.exe");

    /// <summary>Started with frame types (AFMF) tracked.</summary>
    public bool FrameTypes { get; }

    /// <summary>PresentMon's output didn't look as expected (e.g. it refused an option): start one without frame types.</summary>
    public bool Failed { get; private set; }

    /// <summary>Frame types are in the output (the FrameType column).</summary>
    public bool HasFrameTypes { get; private set; }

    public PresentMonFps(bool frameTypes)
    {
        FrameTypes = frameTypes;
        var info = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string[] mode = frameTypes
            ? ["--v2_metrics", "--track_frame_type"]
            : ["--v1_metrics", "--no_track_display"];
        foreach (var arg in mode.Concat(new[]
        {
            "--output_stdout", "--no_console_stats",
            "--no_track_input", "--no_track_gpu",
            "--exclude", "dwm.exe", "--exclude", "ClawControl.exe",
            "--session_name", SessionName, "--stop_existing_session",
        }))
            info.ArgumentList.Add(arg);

        _process = Process.Start(info) ?? throw new InvalidOperationException("PresentMon did not start");
        _job = KillWithClawControl(_process);
        try { _process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info($"PresentMon: {e.Data}"); };
        _process.BeginErrorReadLine();
        new Thread(Read) { IsBackground = true, Name = "PresentMon reader", Priority = ThreadPriority.BelowNormal }.Start();
        Log.Info($"FPS counter started (PresentMon{(frameTypes ? ", with frame generation" : "")})");
    }

    /// <summary>Frames per second the app made over the last second, 0 if it is not drawing, null if never seen.</summary>
    public double? FpsFor(int processId)
    {
        lock (_gate)
        {
            if (!_frames.TryGetValue(processId, out var window)) return null;
            if (Environment.TickCount64 - window.LastArrivalMs > IdleMs) return 0;
            return window.Fps;
        }
    }

    /// <summary>Frames per second AMD's frame generation added to the app's (0 if none), or
    /// null if frame types aren't known (not tracked, or the app was never seen).</summary>
    public double? GeneratedFor(int processId)
    {
        lock (_gate)
        {
            if (!HasFrameTypes || !_frames.TryGetValue(processId, out var window)) return null;
            return window.Generated(Environment.TickCount64);
        }
    }

    void Read()
    {
        try
        {
            var output = _process.StandardOutput;
            var header = output.ReadLine()?.Split(',');
            if (header == null)
            {
                Failed = !_disposed;
                return;
            }
            int Column(params string[] names) => names.Select(n => Array.FindIndex(header, h => h.Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(i => i >= 0, -1);
            int pidColumn = Column("ProcessID");
            int frameColumn = Column("msBetweenPresents");
            int typeColumn = Column("FrameType");
            if (pidColumn < 0 || frameColumn < 0)
            {
                Log.Error($"Unexpected PresentMon output: {string.Join(",", header)}");
                Failed = true;
                return;
            }
            HasFrameTypes = typeColumn >= 0;
            int needed = Math.Max(pidColumn, Math.Max(frameColumn, typeColumn));

            string? line;
            while ((line = output.ReadLine()) != null)
            {
                var parts = line.Split(',');
                if (parts.Length <= needed || !int.TryParse(parts[pidColumn], out int pid)) continue;
                string? type = typeColumn >= 0 ? parts[typeColumn] : null;
                bool generated = type != null && type.Contains("AFMF", StringComparison.OrdinalIgnoreCase);
                bool hasMs = double.TryParse(parts[frameColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms);
                lock (_gate)
                {
                    if (type != null && _typesSeen.Add(type)) Log.Info($"PresentMon frame type seen: {type}");
                    if (!_frames.TryGetValue(pid, out var window)) _frames[pid] = window = new FrameWindow();
                    long now = Environment.TickCount64;
                    if (generated) window.AddGenerated(now);
                    else if (hasMs) window.Add(ms, now);
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
        readonly Queue<double> _recent = new();
        readonly Queue<long> _generated = new();
        double _totalMs;
        public long LastArrivalMs { get; private set; }

        /// <summary>One of the app's own frames, <paramref name="ms"/> after its last one.</summary>
        public void Add(double ms, long now)
        {
            _recent.Enqueue(ms);
            _totalMs += ms;
            while (_recent.Count > 1 && _totalMs - _recent.Peek() >= WindowMs)
                _totalMs -= _recent.Dequeue();
            LastArrivalMs = now;
        }

        public void AddGenerated(long now)
        {
            _generated.Enqueue(now);
            Forget(now);
            LastArrivalMs = now;
        }

        public double? Fps => _totalMs > 0 ? _recent.Count * 1000 / _totalMs : null;

        public double Generated(long now)
        {
            Forget(now);
            return _generated.Count * 1000.0 / GeneratedWindowMs;
        }

        void Forget(long now)
        {
            while (_generated.Count > 0 && now - _generated.Peek() > GeneratedWindowMs) _generated.Dequeue();
        }
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
