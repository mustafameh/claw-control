using System.Runtime.InteropServices;

namespace ClawControl.UI;

static class NativeWindow
{
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const byte VK_MENU = 0x12;
    const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>Keeps the panel out of Alt+Tab and the taskbar.</summary>
    public static void HideFromAltTab(IntPtr hwnd) =>
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

    /// <summary>
    /// Takes the foreground from the game so it stops reacting to the controller while the
    /// panel is open. Windows normally refuses this to background apps.
    /// </summary>
    /// <returns>Whether the panel ended up in the foreground.</returns>
    public static bool BringToFront(IntPtr hwnd)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == hwnd) return true;

        uint foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        uint ownThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != ownThread
                        && AttachThreadInput(ownThread, foregroundThread, true);
        try
        {
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(ownThread, foregroundThread, false);
        }

        if (GetForegroundWindow() != hwnd)
        {
            // Fallback: a synthetic Alt tap counts as our own input, which lifts the lock.
            // Marked so the panel's own input blocker lets it through.
            var marker = (UIntPtr)Input.DesktopInputBlocker.OwnInputMarker;
            keybd_event(VK_MENU, 0, 0, marker);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, marker);
            SetForegroundWindow(hwnd);
        }
        return GetForegroundWindow() == hwnd;
    }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
