using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ClawControl.Core;

namespace ClawControl.Input;

public enum InputSource { Gamepad, Keyboard, Mouse }

/// <summary>
/// Makes the panel behave the same in MSI's Desktop mode as in handheld (XInput) mode.
///
/// In Desktop mode the controller stops reporting as a gamepad and instead acts as a mouse and
/// keyboard: A is a (double) left click, B a right click, LB / RB are Ctrl + Numpad − / +, and
/// the stick moves the cursor. While the panel is open, low-level hooks (which only exist while
/// it is open) swallow all of that so nothing behind the panel reacts, and turn it into panel
/// commands instead. Touch input is never blocked.
///
/// Windows silently removes a low-level hook that ever answers too slowly, so the hot paths are
/// compiled ahead of time, the hook thread runs at high priority, and while the panel is open the
/// hooks are checked every couple of seconds with an invisible probe and reinstalled if gone.
/// </summary>
public sealed class DesktopInputBlocker
{
    /// <summary>Synthetic input we send ourselves carries this marker and is let through.</summary>
    public const uint OwnInputMarker = 0xC1A3C0DE;
    const uint ProbeMarker = 0xC1A3C0DF;

    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14, WM_TIMER = 0x0113;
    const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208,
        WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C, WM_MOUSEHWHEEL = 0x020E;
    const uint LLKHF_UP = 0x80;
    const uint TouchSignatureMask = 0xFFFFFF00, TouchSignature = 0xFF515700;
    const ushort ProbeKey = 0x87;  // F24: no keyboard has it, and the probe is swallowed anyway
    const int MouseIdBase = 0x1000;
    // Keep blocking briefly after closing: the press that closed the panel may still be arriving.
    const long CloseGraceMs = 400, MaxHoldMs = 3000;
    // MSI sends A as a double click; the second click within this time is the same press.
    const long ClickDebounceMs = 350;
    // Stick-as-mouse: this much cursor travel is one step. Pauses longer than StickResetMs start over.
    const int StickStepPixels = 160;
    const long StickResetMs = 300;
    const long ProbeIntervalMs = 2000, ProbeTimeoutMs = 300;
    // Held direction keys repeat at the gamepad's pace instead of Windows' ~30 per second.
    const long KeyRepeatMs = 120;

    readonly object _gate = new();
    readonly HashSet<int> _held = new();          // presses we swallowed, so we swallow their release too
    readonly HookProc _keyboardProc, _mouseProc;  // kept alive for as long as the hooks exist
    bool _open;
    long _closedAtMs, _lastClickMs, _lastMoveMs, _lastKeyRepeatMs;
    int _moveX, _moveY;
    int _keys, _clicks, _moves, _steps;           // per-opening counts, for the log
    Thread? _thread;

    // Hook thread only.
    IntPtr _keyboardHook, _mouseHook;
    long _probeSentMs, _lastProbeMs;
    long _keyboardProbeSeenMs, _mouseProbeSeenMs;

    /// <summary>Raised on the hook thread when swallowed input maps to a panel command.</summary>
    public event Action<NavCommand, InputSource>? Command;

    /// <summary>
    /// Raised on the hook thread (at most every half second) when the controller is sending
    /// Desktop-mode keyboard/mouse input while the panel is open.
    /// </summary>
    public event Action? DesktopInputSeen;
    long _lastDesktopInputMs;

    void NoteDesktopInput()
    {
        long now = Environment.TickCount64;
        if (now - _lastDesktopInputMs < 500) return;
        _lastDesktopInputMs = now;
        DesktopInputSeen?.Invoke();
    }

    static DesktopInputBlocker()
    {
        // Compile the hook paths now; a slow first call (JIT) can get a hook removed by Windows.
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
        foreach (var name in new[] { nameof(KeyboardHook), nameof(MouseHook), nameof(HandleMouse),
                                     nameof(StickStep), nameof(Swallow), nameof(Translate), nameof(LogLater) })
            RuntimeHelpers.PrepareMethod(typeof(DesktopInputBlocker).GetMethod(name, all)!.MethodHandle);
        IntPtr scratch = Marshal.AllocHGlobal(64);
        try
        {
            Marshal.PtrToStructure<KeyboardHookData>(scratch);
            Marshal.PtrToStructure<MouseHookData>(scratch);
        }
        finally
        {
            Marshal.FreeHGlobal(scratch);
        }
        GetPhysicalCursorPos(out _);
    }

    public DesktopInputBlocker()
    {
        _keyboardProc = KeyboardHook;
        _mouseProc = MouseHook;
    }

    public void Open()
    {
        lock (_gate)
        {
            _open = true;
            _moveX = _moveY = 0;
            _keys = _clicks = _moves = _steps = 0;
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "Input blocker", Priority = ThreadPriority.Highest };
            _thread.Start();
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _open = false;
            _closedAtMs = Environment.TickCount64;
            LogDetail($"Panel closed. Controller input while open: {_keys} keys, {_clicks} clicks, " +
                     $"{_moves} stick moves -> {_steps} steps");
        }
    }

    void Run()
    {
        InstallHooks();
        // Low-level hooks need a message loop on the thread that installed them.
        UIntPtr timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 100, IntPtr.Zero);
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.Message == WM_TIMER)
                {
                    if (ShouldStop()) break;
                    CheckHooksAlive();
                }
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            KillTimer(IntPtr.Zero, timer);
            UninstallHooks();
        }
    }

    void InstallHooks()
    {
        IntPtr module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            LogLater($"Could not block Desktop-mode input (error {Marshal.GetLastWin32Error()})");
        else
            LogDetail("Blocking controller mouse/keyboard input while the panel is open");
    }

    void UninstallHooks()
    {
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
    }

    /// <summary>
    /// Every couple of seconds while open, send ourselves a marked key and a 1 px mouse move.
    /// Each hook swallows its probe; if one didn't see it, Windows removed that hook.
    /// </summary>
    void CheckHooksAlive()
    {
        if (!_open) return;
        long now = Environment.TickCount64;
        if (_probeSentMs != 0 && now - _probeSentMs >= ProbeTimeoutMs)
        {
            bool keyboardAlive = Volatile.Read(ref _keyboardProbeSeenMs) >= _probeSentMs;
            bool mouseAlive = Volatile.Read(ref _mouseProbeSeenMs) >= _probeSentMs;
            if (!keyboardAlive || !mouseAlive)
            {
                LogLater($"Input hook was dropped by Windows (keyboard alive: {keyboardAlive}, mouse alive: {mouseAlive}); reinstalling");
                UninstallHooks();
                InstallHooks();
            }
            _probeSentMs = 0;
        }
        if (_probeSentMs == 0 && now - _lastProbeMs >= ProbeIntervalMs)
        {
            _probeSentMs = _lastProbeMs = now;
            var probe = new[]
            {
                KeyInput(ProbeKey, 0), KeyInput(ProbeKey, 2),
                // Windows drops zero-distance moves, so nudge 1 px; the hook swallows it, so the cursor stays put.
                new Input { Type = 0, U = new InputUnion { Mouse = new MouseInput { Dx = 1, Flags = 0x0001, Extra = (UIntPtr)ProbeMarker } } },
            };
            SendInput((uint)probe.Length, probe, Marshal.SizeOf<Input>());
        }
    }

    static Input KeyInput(ushort vk, uint flags) =>
        new() { Type = 1, U = new InputUnion { Keyboard = new KeyboardInput { Vk = vk, Flags = flags, Extra = (UIntPtr)ProbeMarker } } };

    bool ShouldStop()
    {
        lock (_gate)
        {
            if (_open) return false;
            long sinceClose = Environment.TickCount64 - _closedAtMs;
            if (sinceClose < CloseGraceMs) return false;
            if (_held.Count > 0 && sinceClose < MaxHoldMs) return false;
            _held.Clear();
            _thread = null;
            return true;
        }
    }

    bool Active => _open || Environment.TickCount64 - _closedAtMs < CloseGraceMs;

    /// <summary>Decides whether to swallow a press or release. Call with _gate held.</summary>
    bool Swallow(bool isPress, int id)
    {
        if (isPress)
        {
            if (!Active) return false;
            _held.Add(id);
            return true;
        }
        // Only swallow releases of presses we swallowed; anything else must reach its app.
        return _held.Remove(id);
    }

    /// <summary>
    /// Keys that always reach Windows, even while the panel is open: the Claw's volume buttons
    /// (and media keys) only change system-wide things, never the game behind the panel, and the
    /// panel's volume slider follows the change.
    /// </summary>
    static bool PassesThrough(uint vk) => vk is >= VK_VOLUME_MUTE and <= VK_MEDIA_PLAY_PAUSE;

    const uint VK_VOLUME_MUTE = 0xAD;       // 0xAD-0xAF: mute, volume down, volume up
    const uint VK_MEDIA_PLAY_PAUSE = 0xB3;  // 0xB0-0xB3: next, previous, stop, play/pause

    IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KeyboardHookData>(lParam);
            uint marker = (uint)info.ExtraInfo;
            if (marker == ProbeMarker)
            {
                Volatile.Write(ref _keyboardProbeSeenMs, Environment.TickCount64);
                return 1;
            }
            bool isPress = (info.Flags & LLKHF_UP) == 0;
            NavCommand? command = null;
            bool swallow;
            lock (_gate)
            {
                // A held key repeats its press about 30 times a second without releasing.
                bool repeat = isPress && _held.Contains((int)info.VkCode);
                swallow = marker != OwnInputMarker && !PassesThrough(info.VkCode) && Swallow(isPress, (int)info.VkCode);
                if (swallow && isPress && _open)
                {
                    command = Translate(info.VkCode);
                    if (!repeat)
                    {
                        _keys++;
                        _lastKeyRepeatMs = Environment.TickCount64;
                        if (Log.Detailed) LogDetail($"Key 0x{info.VkCode:X2} -> {(command?.ToString() ?? "blocked")}");
                    }
                    else if (command is NavCommand.Up or NavCommand.Down or NavCommand.Left or NavCommand.Right
                             && Environment.TickCount64 - _lastKeyRepeatMs >= KeyRepeatMs)
                    {
                        // Holding a direction keeps moving, at the same pace as the gamepad.
                        _lastKeyRepeatMs = Environment.TickCount64;
                    }
                    else
                    {
                        // Holding anything else (e.g. a page switch) counts once.
                        command = null;
                    }
                }
            }
            if (command is NavCommand c) Command?.Invoke(c, InputSource.Keyboard);
            if (swallow && _open) NoteDesktopInput();
            if (swallow) return 1;
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<MouseHookData>(lParam);
            uint marker = (uint)info.ExtraInfo;
            if (marker == ProbeMarker)
            {
                Volatile.Write(ref _mouseProbeSeenMs, Environment.TickCount64);
                return 1;
            }
            bool fromTouch = (marker & TouchSignatureMask) == TouchSignature;
            if (!fromTouch && marker != OwnInputMarker)
            {
                NavCommand? command;
                bool swallow;
                lock (_gate)
                    swallow = HandleMouse((int)wParam, info, out command);
                if (command is NavCommand c) Command?.Invoke(c, InputSource.Mouse);
                if (swallow && _open) NoteDesktopInput();
                if (swallow) return 1;
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>Call with _gate held.</summary>
    bool HandleMouse(int message, MouseHookData info, out NavCommand? command)
    {
        command = null;
        long now = Environment.TickCount64;
        switch (message)
        {
            case WM_LBUTTONDOWN:
                if (!Swallow(true, MouseIdBase + 1)) return false;
                if (_open)
                {
                    _clicks++;
                    if (now - _lastClickMs > ClickDebounceMs) command = NavCommand.Accept;
                    if (Log.Detailed) LogDetail($"Left click -> {(command?.ToString() ?? "second half of double click, ignored")}");
                }
                _lastClickMs = now;
                return true;
            case WM_RBUTTONDOWN:
                if (!Swallow(true, MouseIdBase + 2)) return false;
                if (_open)
                {
                    _clicks++;
                    command = NavCommand.Back;
                    LogDetail("Right click -> Back");
                }
                return true;
            case WM_MBUTTONDOWN: return Swallow(true, MouseIdBase + 3);
            case WM_XBUTTONDOWN: return Swallow(true, MouseIdBase + 4);
            case WM_LBUTTONUP: return Swallow(false, MouseIdBase + 1);
            case WM_RBUTTONUP: return Swallow(false, MouseIdBase + 2);
            case WM_MBUTTONUP: return Swallow(false, MouseIdBase + 3);
            case WM_XBUTTONUP: return Swallow(false, MouseIdBase + 4);
            case WM_MOUSEWHEEL:
                if (_open) command = (short)(info.MouseData >> 16) > 0 ? NavCommand.Up : NavCommand.Down;
                return Active;
            case WM_MOUSEHWHEEL:
                return Active;
            case WM_MOUSEMOVE:
                if (_open)
                {
                    _moves++;
                    command = StickStep(info, now);
                    if (command != null) _steps++;
                }
                return Active;
            default:
                return false;
        }
    }

    /// <summary>
    /// The cursor stays put while we swallow movement, so each move event's distance from the
    /// cursor is how far the stick pushed. Enough travel in one direction counts as one step.
    /// </summary>
    NavCommand? StickStep(MouseHookData info, long now)
    {
        // Hook coordinates are physical pixels; GetCursorPos can be scaled, so use the physical one.
        if (!GetPhysicalCursorPos(out var cursor)) return null;
        if (now - _lastMoveMs > StickResetMs) _moveX = _moveY = 0;
        _lastMoveMs = now;
        _moveX += info.X - cursor.X;
        _moveY += info.Y - cursor.Y;
        if (Math.Abs(_moveX) < StickStepPixels && Math.Abs(_moveY) < StickStepPixels) return null;
        var step = Math.Abs(_moveX) > Math.Abs(_moveY)
            ? (_moveX > 0 ? NavCommand.Right : NavCommand.Left)
            : (_moveY > 0 ? NavCommand.Down : NavCommand.Up);
        _moveX = _moveY = 0;
        return step;
    }

    /// <summary>
    /// Keys to panel commands. In Desktop mode the triggers send Ctrl + Numpad − / + (zoom) and
    /// LB / RB send nothing; triggers deliberately do nothing here, matching gamepad mode. Pages
    /// switch with LB / RB once Command Center has the controller in gamepad mode.
    /// </summary>
    static NavCommand? Translate(uint vk) => vk switch
    {
        0x26 => NavCommand.Up,
        0x28 => NavCommand.Down,
        0x25 => NavCommand.Left,
        0x27 => NavCommand.Right,
        0x0D or 0x20 => NavCommand.Accept,       // Enter, Space
        0x1B or 0x08 => NavCommand.Back,         // Esc, Backspace
        0x21 or 0x51 => NavCommand.PrevPage,     // Page Up, Q (keyboards)
        0x22 or 0x45 => NavCommand.NextPage,     // Page Down, E (keyboards)
        _ => null,
    };

    // Never write files inside a hook; it would delay everyone's input.
    static void LogLater(string message) => ThreadPool.QueueUserWorkItem(_ => Log.Info(message));

    /// <summary>Detailed log only (check Log.Detailed first where the text is built, to skip the formatting).</summary>
    static void LogDetail(string message)
    {
        if (Log.Detailed) ThreadPool.QueueUserWorkItem(_ => Log.Detail(message));
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KeyboardHookData
    {
        public uint VkCode, ScanCode, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseHookData
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Msg
    {
        public IntPtr Hwnd;
        public int Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KeyboardInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public UIntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput
    {
        public int Dx, Dy, Data;
        public uint Flags, Time;
        public UIntPtr Extra;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out Msg msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref Msg msg);
    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr id, uint elapseMs, IntPtr proc);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr hWnd, UIntPtr id);
    [DllImport("user32.dll")] static extern bool GetPhysicalCursorPos(out Point point);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
