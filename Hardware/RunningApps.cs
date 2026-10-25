using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClawControl.Core;

namespace ClawControl.Hardware;

/// <summary>An app with a window: what the Apps &amp; power menu lists (like Alt + Tab).</summary>
public sealed record RunningApp(int ProcessId, string Name, string? Path, long MemoryBytes, bool NotResponding)
{
    public ImageSource? Icon { get; init; }
}

/// <summary>
/// Apps with windows (Alt + Tab's rules: visible, not a tool window, not cloaked, no owner),
/// grouped by process, closing them, and the power actions. Only read when asked (the menu is
/// open); nothing watches in the background.
/// </summary>
public static class RunningApps
{
    static readonly Dictionary<string, ImageSource?> Icons = new();
    // Windows' own shell windows aren't apps to close.
    static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow" };

    /// <summary>The app in front right now (call before the panel takes focus).</summary>
    public static RunningApp? Front() => Describe(GetForegroundWindow());

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    /// <summary>The app in front (before the panel took focus); null for the desktop / taskbar.</summary>
    public static RunningApp? Describe(IntPtr window) =>
        window != IntPtr.Zero && IsApp(window) && Process(window) is { } pid && pid != Environment.ProcessId ? App(pid, [window]) : null;

    /// <summary>Every app with a window, biggest memory first; Claw Control itself left out.</summary>
    public static List<RunningApp> List()
    {
        var byProcess = new Dictionary<int, List<IntPtr>>();
        EnumWindows((window, _) =>
        {
            if (IsApp(window) && Process(window) is { } pid && pid != Environment.ProcessId)
            {
                if (!byProcess.TryGetValue(pid, out var windows)) byProcess[pid] = windows = new();
                windows.Add(window);
            }
            return true;
        }, IntPtr.Zero);
        return byProcess.Select(p => App(p.Key, p.Value)).OfType<RunningApp>()
            .OrderByDescending(a => a.MemoryBytes).ToList();
    }

    static RunningApp? App(int pid, List<IntPtr> windows)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            string? path = null;
            try { path = process.MainModule?.FileName; } catch { }
            string name = path != null ? FileVersionInfo.GetVersionInfo(path).FileDescription ?? "" : "";
            if (string.IsNullOrWhiteSpace(name)) name = process.ProcessName;
            // Browsers and Electron apps run as many processes: count them all.
            long memory = System.Diagnostics.Process.GetProcessesByName(process.ProcessName).Sum(p =>
            {
                using (p) { try { return p.WorkingSet64; } catch { return 0; } }
            });
            return new RunningApp(pid, name.Trim(), path, memory, windows.Any(IsHungAppWindow)) { Icon = Icon(path) };
        }
        catch
        {
            return null;  // gone already
        }
    }

    /// <summary>Asks the app to close its windows (it can save); force-closes it if it's frozen
    /// or still running after the wait. Windows' own Explorer is never force-closed.</summary>
    public static async Task Close(RunningApp app, TimeSpan wait)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(app.ProcessId);
            bool explorer = process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
            EnumWindows((window, _) =>
            {
                if (IsApp(window) && Process(window) == app.ProcessId) PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
            if (explorer) return;
            if (!app.NotResponding && await Task.Run(() => process.WaitForExit((int)wait.TotalMilliseconds))) return;
            Log.Info($"Force-closing {app.Name}");
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { }  // already gone
        catch (Exception ex)
        {
            Log.Error($"Closing {app.Name}", ex);
        }
    }

    // ---- Power ----

    public static void Sleep() => SetSuspendState(false, false, false);
    public static void Hibernate() => SetSuspendState(true, false, false);
    public static void Restart() => Shutdown("/r /t 0");
    public static void ShutDown() => Shutdown("/s /t 0");

    static void Shutdown(string args)
    {
        Log.Info($"shutdown {args}");
        System.Diagnostics.Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false });
    }

    // ---- Windows ----

    static bool IsApp(IntPtr window)
    {
        if (!IsWindowVisible(window) || GetWindow(window, GwOwner) != IntPtr.Zero) return false;
        if ((GetWindowLong(window, GwlExStyle) & WsExToolWindow) != 0) return false;
        if (DwmGetWindowAttribute(window, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        if (GetWindowTextLength(window) == 0) return false;
        var className = new StringBuilder(64);
        GetClassName(window, className, className.Capacity);
        return !ShellClasses.Contains(className.ToString());
    }

    static int? Process(IntPtr window) => GetWindowThreadProcessId(window, out uint pid) != 0 ? (int)pid : null;

    static ImageSource? Icon(string? path)
    {
        if (path == null) return null;
        lock (Icons)
        {
            if (Icons.TryGetValue(path, out var cached)) return cached;
            ImageSource? icon = null;
            var info = new ShFileInfo();
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), 0x100) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                try
                {
                    var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    icon = bitmap;
                }
                finally { DestroyIcon(info.hIcon); }
            }
            return Icons[path] = icon;
        }
    }

    const int GwOwner = 4, GwlExStyle = -20, WsExToolWindow = 0x80, DwmwaCloaked = 14, WmClose = 0x10;

    delegate bool EnumProc(IntPtr window, IntPtr param);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, int command);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvents);
}
