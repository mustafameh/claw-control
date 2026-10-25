using System.Runtime.InteropServices;

namespace ClawControl.Input;

/// <summary>Live stick / trigger positions for the Controller tab previews, and a test rumble.
/// Only called while those screens are open.</summary>
public static class PadState
{
    public readonly record struct Snapshot(double LeftX, double LeftY, double RightX, double RightY, double LeftTrigger, double RightTrigger);

    /// <summary>Sticks -1..1 (up is +Y), triggers 0..1; null if no gamepad is connected.</summary>
    public static Snapshot? Read()
    {
        for (uint slot = 0; slot < 4; slot++)
        {
            if (XInputGetState(slot, out var s) != 0) continue;
            var g = s.Gamepad;
            return new Snapshot(g.ThumbLX / 32768.0, g.ThumbLY / 32768.0, g.ThumbRX / 32768.0, g.ThumbRY / 32768.0,
                g.LeftTrigger / 255.0, g.RightTrigger / 255.0);
        }
        return null;
    }

    /// <summary>A short rumble at full request strength, so the chip's vibration setting can be felt.
    /// <paramref name="left"/> picks one motor (left = heavy, right = light); null buzzes both.</summary>
    public static async Task Buzz(bool? left = null, int milliseconds = 140)
    {
        for (uint slot = 0; slot < 4; slot++)
        {
            if (XInputGetState(slot, out _) != 0) continue;
            var on = new XInputVibration
            {
                Left = left == false ? (ushort)0 : ushort.MaxValue,
                Right = left == true ? (ushort)0 : ushort.MaxValue,
            };
            XInputSetState(slot, ref on);
            await Task.Delay(milliseconds);
            var off = new XInputVibration();
            XInputSetState(slot, ref off);
            return;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputVibration
    {
        public ushort Left, Right;
    }

    [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint userIndex, out XInputState state);
    [DllImport("xinput1_4.dll")] static extern uint XInputSetState(uint userIndex, ref XInputVibration vibration);
}
