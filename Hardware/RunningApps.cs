using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClawControl.Core;

namespace ClawControl.Hardware;

/// <summary>An app with a window: what the app switcher shows (like Alt + Tab).</summary>
public sealed record RunningApp(int ProcessId, string Name, string? Path, long MemoryBytes, bool NotResponding)
{
    public ImageSource? Icon { get; init; }
    /// <summary>A sharp ~96 px icon (RunningApps.LargeIcon), worked out with the rest in the background.</summary>
    public ImageSource? BigIcon { get; init; }
    /// <summary>Its top window (the one in front of its others): what switching to it brings up.</summary>
    public IntPtr Window { get; init; }
    /// <summary>All its windows (what ending it closes).</summary>
    public IReadOnlyList<IntPtr> Windows { get; init; } = [];
    /// <summary>Its id in the shell (AppUserModelID), when the window has one: Store apps' real name and icon.</summary>
    public string? AppId { get; init; }
    /// <summary>A Store app whose own process couldn't be found (suspended): ProcessId is Windows'
    /// frame host, which serves every Store app and is never ended.</summary>
    public bool Hosted { get; init; }
    /// <summary>Tells apps apart (Store apps can share the frame host's process).</summary>
    public string Key => AppId ?? ProcessId.ToString();
}

/// <summary>
/// Apps with windows (Alt + Tab's rules: visible, not a tool window, not cloaked, no owner),
/// grouped by app (Store apps by their shell id: they live in Windows' frame host), closing them, and the power actions. Only read when asked (the menu is
/// open); nothing watches in the background.
/// </summary>
public static class RunningApps
{
    static readonly Dictionary<string, ImageSource?> Icons = new();
    // Windows' own shell windows aren't apps to close.
    static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow" };

    /// <summary>The window in front right now (call before the panel takes focus); Describe says whose.</summary>
    public static IntPtr FrontWindow() => GetForegroundWindow();

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    /// <summary>The app in front (before the panel took focus); null for the desktop / taskbar.</summary>
    public static RunningApp? Describe(IntPtr window) =>
        window != IntPtr.Zero && IsApp(window) && Identify(window) is { } who ? App(who, [window]) : null;

    /// <summary>Whose window it is. Store apps' windows belong to Windows' frame host
    /// (ApplicationFrameWindow): their own process owns the frame's inner CoreWindow, and the
    /// window's shell id says which app it is even while that's suspended.</summary>
    static (int Pid, string? AppId, bool Hosted)? Identify(IntPtr window)
    {
        if (Process(window) is not { } pid || pid == Environment.ProcessId) return null;
        string? appId = WindowAppId(window);
        if (ClassOf(window) != "ApplicationFrameWindow") return (pid, appId, false);
        int? own = null;
        EnumChildWindows(window, (child, _) =>
        {
            if (ClassOf(child) == "Windows.UI.Core.CoreWindow" && Process(child) is { } app && app != pid) own = app;
            return own == null;
        }, IntPtr.Zero);
        return own is { } real ? (real, appId, false) : (pid, appId, true);
    }

    /// <summary>Every app with a window, most recently used first (Alt + Tab's order); Claw Control
    /// itself left out.</summary>
    public static List<RunningApp> List()
    {
        var byApp = new Dictionary<string, ((int Pid, string? AppId, bool Hosted) Who, List<IntPtr> Windows)>();
        EnumWindows((window, _) =>
        {
            if (IsApp(window) && Identify(window) is { } who)
            {
                string key = who.AppId ?? who.Pid.ToString();
                if (!byApp.TryGetValue(key, out var app)) byApp[key] = app = (who, new());
                app.Windows.Add(window);
            }
            return true;
        }, IntPtr.Zero);
        // EnumWindows goes front to back, and the dictionary keeps that order.
        return byApp.Values.Select(a => App(a.Who, a.Windows)).OfType<RunningApp>().ToList();
    }

    /// <summary>Brings the app's window to the front (restoring it if minimised).</summary>
    public static void SwitchTo(RunningApp app)
    {
        if (app.Window == IntPtr.Zero || !IsWindow(app.Window)) return;
        if (IsIconic(app.Window)) ShowWindow(app.Window, SwRestore);
        SetForegroundWindow(app.Window);
    }

    /// <summary>A sharp icon of about <paramref name="size"/> pixels (the shell's big icons), or the small one.</summary>
    public static ImageSource? LargeIcon(RunningApp app, int size)
    {
        // A Store app's own tile icon (its shell id), else the program file's icon, else the one
        // its window shows in the taskbar (games often set their logo only there).
        bool fileHasIcon = app.Path != null && ExtractIconEx(app.Path, -1, null, null, 0) > 0;
        string?[] sources = [app.AppId != null ? AppsFolder + app.AppId : null, fileHasIcon ? app.Path : null];
        foreach (var source in sources.OfType<string>())
        {
            string key = $"{source}|{size}";
            lock (Icons)
                if (Icons.TryGetValue(key, out var cached)) { if (cached != null) return cached; continue; }
            ImageSource? icon = null;
            try
            {
                var factoryId = typeof(IShellItemImageFactory).GUID;
                if (SHCreateItemFromParsingName(source, IntPtr.Zero, ref factoryId, out IShellItemImageFactory factory) == 0)
                {
                    // Icon only (no thumbnail of the file), scaled up by the shell if it has no big one.
                    if (factory.GetImage(new NativeSize { Width = size, Height = size }, 0x4, out IntPtr bitmap) == 0)
                    {
                        try { icon = FromAlphaBitmap(bitmap); }
                        finally { DeleteObject(bitmap); }
                    }
                    Marshal.ReleaseComObject(factory);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Big icon for {app.Name}", ex);
            }
            lock (Icons) Icons[key] = icon;
            if (icon != null) return icon;
        }
        return WindowIcon(app.Window) ?? app.Icon;
    }

    /// <summary>The icon a window shows in the taskbar (asked with a short timeout: a busy game
    /// window may be slow to answer).</summary>
    static ImageSource? WindowIcon(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        IntPtr icon = IntPtr.Zero;
        foreach (int kind in new[] { 1, 2 })  // ICON_BIG, ICON_SMALL2
            if (icon == IntPtr.Zero && SendMessageTimeout(window, WmGetIcon, kind, IntPtr.Zero, SmtoAbortIfHung, 150, out var answer) != IntPtr.Zero)
                icon = answer;
        if (icon == IntPtr.Zero) icon = GetClassLongPtr(window, GclpHIcon);
        if (icon == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    const string AppsFolder = @"shell:AppsFolder\";
    static readonly Dictionary<string, string?> ShellNames = new();

    /// <summary>The name the Start menu shows for a shell id ("Photos", "Settings"), or null.</summary>
    static string? ShellName(string appId)
    {
        lock (ShellNames)
            if (ShellNames.TryGetValue(appId, out var known)) return known;
        string? name = null;
        try
        {
            var itemId = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName(AppsFolder + appId, IntPtr.Zero, ref itemId, out IShellItem item) == 0)
            {
                if (item.GetDisplayName(0, out IntPtr text) == 0)
                {
                    name = Marshal.PtrToStringUni(text);
                    Marshal.FreeCoTaskMem(text);
                }
                Marshal.ReleaseComObject(item);
            }
        }
        catch { }  // not an app the Start menu knows: the program's own name stays
        lock (ShellNames) ShellNames[appId] = name;
        return name;
    }

    /// <summary>The shell id (AppUserModelID) set on a window, which Store apps (and some others) have.</summary>
    static string? WindowAppId(IntPtr window)
    {
        try
        {
            var storeId = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(window, ref storeId, out var store) != 0) return null;
            try
            {
                var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
                if (store.GetValue(ref key, out var value) != 0) return null;
                try { return value.Type == VtString ? Marshal.PtrToStringUni(value.Pointer) : null; }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A 32-bit shell bitmap with its alpha kept (CreateBitmapSourceFromHBitmap drops it).</summary>
    static BitmapSource? FromAlphaBitmap(IntPtr bitmap)
    {
        var info = new BitmapInfo();
        if (GetObject(bitmap, Marshal.SizeOf<BitmapInfo>(), ref info) == 0 || info.BitsPixel != 32 || info.Bits == IntPtr.Zero) return null;
        int stride = info.WidthBytes, height = Math.Abs(info.Height);
        var pixels = new byte[stride * height];
        Marshal.Copy(info.Bits, pixels, 0, pixels.Length);
        // Old-style icons come back with no alpha at all (every pixel 0): they're opaque, not invisible.
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length && !anyAlpha; i += 4) anyAlpha = pixels[i] != 0;
        if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var source = BitmapSource.Create(info.Width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        // Bottom-up bitmaps (positive height) come out upside down.
        BitmapSource result = info.Height > 0 ? new TransformedBitmap(source, new ScaleTransform(1, -1)) : source;
        result.Freeze();
        return result;
    }

    static RunningApp? App((int Pid, string? AppId, bool Hosted) who, List<IntPtr> windows)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(who.Pid);
            string? path = null;
            if (!who.Hosted) try { path = process.MainModule?.FileName; } catch { }
            string name = (who.AppId != null ? ShellName(who.AppId) : null)
                ?? (path != null ? FileVersionInfo.GetVersionInfo(path).FileDescription : null) ?? "";
            if (string.IsNullOrWhiteSpace(name)) name = who.Hosted ? GetWindowTitle(windows[0]) : process.ProcessName;
            // Browsers and Electron apps run as many processes: count them all. (A suspended Store
            // app's own process isn't known: no number.)
            long memory = who.Hosted ? 0 : System.Diagnostics.Process.GetProcessesByName(process.ProcessName).Sum(p =>
            {
                using (p) { try { return p.WorkingSet64; } catch { return 0; } }
            });
            var app = new RunningApp(who.Pid, name.Trim(), path, memory, windows.Any(IsHungAppWindow))
            {
                Icon = Icon(path), Window = windows[0], Windows = windows, AppId = who.AppId, Hosted = who.Hosted,
            };
            return app with { BigIcon = LargeIcon(app, 96) };
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
            foreach (var window in app.Windows) PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
            // Windows' frame host serves every Store app: closing the window is all it takes.
            if (app.Hosted) return;
            using var process = System.Diagnostics.Process.GetProcessById(app.ProcessId);
            if (process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return;
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
        return !ShellClasses.Contains(ClassOf(window));
    }

    static string ClassOf(IntPtr window)
    {
        var className = new StringBuilder(64);
        GetClassName(window, className, className.Capacity);
        return className.ToString();
    }

    static string GetWindowTitle(IntPtr window)
    {
        var title = new StringBuilder(GetWindowTextLength(window) + 1);
        GetWindowText(window, title, title.Capacity);
        return title.ToString();
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

    const int GwOwner = 4, GwlExStyle = -20, WsExToolWindow = 0x80, DwmwaCloaked = 14, WmClose = 0x10, SwRestore = 9;

    [StructLayout(LayoutKind.Sequential)] struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid FormatId; public int PropertyId; }

    /// <summary>PROPVARIANT, only as far as a string value needs (24 bytes on x64).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    const ushort VtString = 31;  // VT_LPWSTR

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid id, out IntPtr result);
        [PreserveSig] int GetParent(out IntPtr parent);
        [PreserveSig] int GetDisplayName(int form, out IntPtr name);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int max);

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfo
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr bitmap, int size, ref BitmapInfo info);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr bitmap);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    const int WmGetIcon = 0x7F, GclpHIcon = -14, SmtoAbortIfHung = 0x2;
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr window, int message, int wParam, IntPtr lParam, int flags, int timeoutMs, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr(IntPtr window, int index);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);

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
