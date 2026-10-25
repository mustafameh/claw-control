using System.Diagnostics;
using System.Runtime.InteropServices;
using ClawControl.Core;

namespace ClawControl.Hardware;

/// <summary>
/// Stops MSI from also reacting to the MSI Center M button while Claw Control owns it.
/// Two MSI programs listen for the button besides MSI's server:
///
/// 1. MSI's launcher (MSI_Center_M_Launcher.exe). MSI's server only steps in when no process with
///    that name exists, and restarts a closed launcher every second without limit, so we pause
///    it instead of closing it.
/// 2. MSI Center M itself, which stays running hidden after its first use and shows itself on the
///    button. We close it whenever it is hidden; if you open it on purpose it is left alone until
///    you minimize or close it.
///
/// Turning this off closes the paused launcher; MSI's server starts a fresh one about a second
/// later and MSI's behaviour is back. No MSI files are changed.
/// </summary>
public sealed class MsiButtonTakeover : IDisposable
{
    const string LauncherName = "MSI_Center_M_Launcher";
    const string MainUiName = "MSI Center M";
    static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(60);

    readonly HashSet<int> _pausedLaunchers = new();
    readonly HashSet<int> _mainUiSeenVisible = new();
    readonly Timer _timer;

    public MsiButtonTakeover()
    {
        _timer = new Timer(_ => Check(), null, 0, 3_000);
    }

    void Check()
    {
        lock (_pausedLaunchers)
        {
            PauseLaunchers();
            CloseHiddenMainUi();
        }
    }

    void PauseLaunchers()
    {
        foreach (var process in Process.GetProcessesByName(LauncherName))
        {
            using (process)
            {
                if (_pausedLaunchers.Contains(process.Id)) continue;
                try
                {
                    int status = NtSuspendProcess(process.Handle);
                    if (status == 0)
                    {
                        _pausedLaunchers.Add(process.Id);
                        Log.Info($"Paused MSI launcher (pid {process.Id})");
                    }
                    else
                    {
                        Log.Error($"Could not pause MSI launcher (pid {process.Id}), status 0x{status:X8}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Pausing MSI launcher (pid {process.Id})", ex);
                }
            }
        }
    }

    void CloseHiddenMainUi()
    {
        foreach (var process in Process.GetProcessesByName(MainUiName))
        {
            using (process)
            {
                try
                {
                    if (HasVisibleWindow(process.Id))
                    {
                        _mainUiSeenVisible.Add(process.Id);
                        continue;
                    }
                    // Freshly opened: its window may not be up yet.
                    bool wasUsed = _mainUiSeenVisible.Contains(process.Id);
                    if (!wasUsed && DateTime.Now - process.StartTime < StartupGrace) continue;

                    process.Kill();
                    _mainUiSeenVisible.Remove(process.Id);
                    Log.Info($"Closed MSI Center M running hidden in the background (pid {process.Id})");
                }
                catch (Exception ex)
                {
                    Log.Error($"Checking MSI Center M (pid {process.Id})", ex);
                }
            }
        }
    }

    static bool HasVisibleWindow(int processId)
    {
        bool visible = false;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == processId && IsWindowVisible(hwnd) && !IsIconic(hwnd))
            {
                visible = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return visible;
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_pausedLaunchers)
        {
            foreach (int id in _pausedLaunchers)
            {
                try
                {
                    using var process = Process.GetProcessById(id);
                    process.Kill();
                    Log.Info($"Closed paused MSI launcher (pid {id}); MSI will start a fresh one");
                }
                catch (ArgumentException)
                {
                    // Already gone.
                }
                catch (Exception ex)
                {
                    Log.Error($"Closing paused MSI launcher (pid {id})", ex);
                }
            }
            _pausedLaunchers.Clear();
        }
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr processHandle);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
}
