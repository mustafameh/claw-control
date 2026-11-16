using System.Diagnostics;
using System.Runtime.InteropServices;
using ClawControl.Core;

namespace ClawControl.UI;

/// <summary>
/// Windows' own touch keyboard, brought up for a text box (the Cover screen's Steam search): shown
/// through TabTip's invocation interface (started first if it isn't running), hidden again when
/// typing ends. Whether it's on screen comes from the input pane's position.
/// </summary>
static class TouchKeyboard
{
    static readonly Guid TipInvocation = new("4CE576FA-83DC-4F88-951C-9D0782B4E376");  // UIHostNoLaunch
    static readonly Guid InputPane = new("D5120AA3-46BA-44C5-822D-CA8092C1FC72");     // FrameworkInputPane
    const string TabTip = @"C:\Program Files\Common Files\microsoft shared\ink\TabTip.exe";

    public static async void Show()
    {
        try
        {
            if (IsShown()) return;
            if (!Toggle())
            {
                // Not running yet: start it, then ask again.
                if (File.Exists(TabTip)) Process.Start(new ProcessStartInfo(TabTip) { UseShellExecute = true })?.Dispose();
                for (int i = 0; i < 10 && !Toggle(); i++) await Task.Delay(150);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Showing the touch keyboard", ex);
        }
    }

    public static void Hide()
    {
        try
        {
            if (IsShown()) Toggle();
        }
        catch (Exception ex)
        {
            Log.Detail($"Hiding the touch keyboard: {ex.Message}");
        }
    }

    static bool Toggle()
    {
        var type = Type.GetTypeFromCLSID(TipInvocation);
        if (type == null) return false;
        object? tip = null;
        try
        {
            tip = Activator.CreateInstance(type);
            if (tip is not ITipInvocation invocation) return false;
            invocation.Toggle(GetDesktopWindow());
            return true;
        }
        catch (COMException)
        {
            return false;  // TabTip isn't running
        }
        finally
        {
            if (tip != null) Marshal.ReleaseComObject(tip);
        }
    }

    static bool IsShown()
    {
        object? pane = null;
        try
        {
            var type = Type.GetTypeFromCLSID(InputPane);
            if (type == null) return false;
            pane = Activator.CreateInstance(type);
            return pane is IFrameworkInputPane framework && framework.Location(out var area) == 0 && area.Right - area.Left > 0 && area.Bottom - area.Top > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pane != null) Marshal.ReleaseComObject(pane);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("37C994E7-432B-4834-A2F7-DCE1F13B834B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITipInvocation
    {
        void Toggle(IntPtr window);
    }

    [ComImport, Guid("5752238B-24F0-495A-82F1-2FD593056796"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFrameworkInputPane
    {
        [PreserveSig] int Advise(IntPtr window, IntPtr handler, out int cookie);
        [PreserveSig] int AdviseWithHWND(IntPtr window, IntPtr handler, out int cookie);
        [PreserveSig] int Unadvise(int cookie);
        [PreserveSig] int Location(out Rect area);
    }

    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
}
