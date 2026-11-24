using ClawControl.Input;

namespace ClawControl.Hardware;

/// <summary>Buttons the A8 panel temporarily remaps. Offsets and native codes follow the MSI key block order.</summary>
internal sealed record PanelButton(string Name, int Offset, byte Native, byte PanelCode, bool Trigger = false)
{
    const int Keys = 58;
    static PanelButton Key(string name, byte native, byte panel) => new(name, Keys + (native - 1) * 8, native, panel);
    internal static readonly PanelButton[] All =
    [
        Key("D-pad up", 1, PanelKeys.Up), Key("D-pad down", 2, PanelKeys.Down),
        Key("D-pad left", 3, PanelKeys.Left), Key("D-pad right", 4, PanelKeys.Right),
        Key("LB", 5, PanelKeys.PrevPage), Key("RB", 6, PanelKeys.NextPage),
        Key("Left stick click", 7, PanelKeys.Nothing), Key("Right stick click", 8, PanelKeys.Nothing),
        Key("A", 9, PanelKeys.Accept), Key("B", 10, PanelKeys.Back),
        Key("X", 11, PanelKeys.Action), Key("Y", 12, PanelKeys.Menu),
        Key("View (Back)", 14, PanelKeys.Nothing), Key("Menu (Start)", 15, PanelKeys.Nothing),
        new("LT", 524, 19, PanelKeys.Nothing, true), new("RT", 539, 20, PanelKeys.Nothing, true),
    ];

    int Map => Offset + (Trigger ? 5 : 3);
    internal void ApplyPanel(byte[] profile)
    {
        profile[Offset] = 1;
        profile[Offset + 1] = (byte)((profile[Offset + 1] & ~1) | 4);
        profile[Map] = PanelCode;
        Array.Clear(profile, Map + 1, 4);
    }

    internal bool MatchesPanel(byte[] profile) =>
        profile[Offset] == 1 && (profile[Offset + 1] & 5) == 4 && profile[Map] == PanelCode
        && profile.AsSpan(Map + 1, 4).IndexOfAnyExcept((byte)0) < 0;

    internal bool RestoreNative(byte[] profile)
    {
        // View and Menu use firmware pass-through (their native mapping blocks are disabled).
        bool passThrough = Native is 14 or 15;
        byte enabled = passThrough ? (byte)0 : (byte)1;
        byte code = passThrough ? (byte)0 : Native;
        if (profile[Offset] == enabled && (profile[Offset + 1] & 5) == 0 &&
            profile[Map] == code && profile.AsSpan(Map + 1, 4).IndexOfAnyExcept((byte)0) < 0) return false;
        profile[Offset] = enabled;
        profile[Offset + 1] &= 0xFA;
        profile[Map] = code;
        Array.Clear(profile, Map + 1, 4);
        return true;
    }
}
