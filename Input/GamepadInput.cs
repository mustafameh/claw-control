using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClawControl.Input;

public enum NavCommand { Up, Down, Left, Right, Accept, Back, PrevPage, NextPage, Menu }

/// <summary>
/// Reads the built-in controller through XInput while the panel is open and turns presses into
/// navigation commands. Holding a direction repeats it (useful on sliders). Polling stops
/// completely while the panel is hidden, so it costs no battery during games.
///
/// With the panel mapping on (ControllerService) XInput sees no buttons and DesktopInputBlocker
/// gets them as keys instead; this covers the moment before it lands and setups without it. The
/// D-pad only: the sticks still reach the game behind the panel, so the panel doesn't use them.
/// </summary>
public sealed class GamepadInput : IDisposable
{
    const ushort DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008;
    const ushort LeftBumper = 0x0100, RightBumper = 0x0200, ButtonA = 0x1000, ButtonB = 0x2000, ButtonY = 0x8000;
    const int RepeatDelayMs = 350, RepeatIntervalMs = 80;

    static readonly (ushort Mask, NavCommand Command)[] Map =
    {
        (DPadUp, NavCommand.Up), (DPadDown, NavCommand.Down),
        (DPadLeft, NavCommand.Left), (DPadRight, NavCommand.Right),
        (ButtonA, NavCommand.Accept), (ButtonB, NavCommand.Back),
        (LeftBumper, NavCommand.PrevPage), (RightBumper, NavCommand.NextPage),
        (ButtonY, NavCommand.Menu),  // the Apps & power menu
    };

    readonly ManualResetEventSlim _active = new(false);
    volatile bool _disposed;

    /// <summary>Raised on the polling thread.</summary>
    public event Action<NavCommand>? Command;

    public GamepadInput()
    {
        new Thread(Loop) { IsBackground = true, Name = "Gamepad" }.Start();
    }

    public bool Enabled
    {
        set { if (value) _active.Set(); else _active.Reset(); }
    }

    void Loop()
    {
        var clock = Stopwatch.StartNew();
        int slot = -1, lastLoggedSlot = int.MinValue;
        long nextScan = 0;
        ushort? previous = null;
        NavCommand? held = null;
        ushort heldMask = 0;
        long heldSince = 0, lastRepeat = 0;

        while (!_disposed)
        {
            if (!_active.IsSet)
            {
                previous = null;
                held = null;
                // The controller mode can change while hidden (MSI switches it), so look again on open.
                slot = -1;
                nextScan = 0;
                lastLoggedSlot = int.MinValue;
                _active.Wait();
                clock.Restart();
                continue;
            }

            long now = clock.ElapsedMilliseconds;
            if (slot < 0)
            {
                if (now < nextScan) { Thread.Sleep(50); continue; }
                slot = FindController();
                nextScan = now + 2000;
                if (slot != lastLoggedSlot)
                {
                    Core.Log.Detail(slot < 0
                        ? "No XInput controller visible (MSI Desktop mode turns the controller into keyboard/mouse)"
                        : $"XInput controller on slot {slot}");
                    lastLoggedSlot = slot;
                }
                if (slot < 0) continue;
            }

            if (XInputGetState((uint)slot, out var state) != 0)
            {
                slot = -1;
                continue;
            }

            ushort buttons = state.Gamepad.Buttons;
            // Ignore whatever was already held when the panel opened.
            ushort pressed = previous is ushort p ? (ushort)(buttons & ~p) : (ushort)0;
            previous = buttons;
            if (pressed != 0) Core.Log.Detail($"Gamepad press 0x{pressed:X4}");

            foreach (var (mask, command) in Map)
            {
                if ((pressed & mask) == 0) continue;
                Command?.Invoke(command);
                if (command <= NavCommand.Right)
                {
                    held = command;
                    heldMask = mask;
                    heldSince = lastRepeat = now;
                }
            }

            if (held is NavCommand h)
            {
                if ((buttons & heldMask) == 0)
                    held = null;
                else if (now - heldSince >= RepeatDelayMs && now - lastRepeat >= RepeatIntervalMs)
                {
                    Command?.Invoke(h);
                    lastRepeat = now;
                }
            }

            Thread.Sleep(16);
        }
    }

    static int FindController()
    {
        for (uint i = 0; i < 4; i++)
            if (XInputGetState(i, out _) == 0) return (int)i;
        return -1;
    }

    public void Dispose()
    {
        _disposed = true;
        _active.Set();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetState(uint userIndex, out XInputState state);
}
