namespace ClawControl.Input;

/// <summary>
/// The keys the controller chip sends instead of gamepad buttons while the panel is open
/// (ControllerService's panel mapping; docs/HARDWARE.md "Panel mapping"). Games read the gamepad
/// directly, whatever window is in front, so the panel's presses are kept from them by having the
/// chip send keys, which DesktopInputBlocker takes before any app sees them. Only codes tried on
/// the device are used: some (F22) send nothing at all.
/// </summary>
public static class PanelKeys
{
    // MSI's key codes, as the chip's mapping takes them.
    public const int Up = 128, Down = 129, Left = 130, Right = 131;  // arrow keys
    public const int Accept = 142;    // numpad 3
    public const int Back = 156;      // F23
    public const int Menu = 144;      // numpad 5
    public const int PrevPage = 146;  // numpad 7
    public const int NextPage = 148;  // numpad 9
    public const int Nothing = 255;

    const uint VK_F23 = 0x86;

    /// <summary>
    /// The panel command for a key the chip sends, or null. Numpad keys are told apart by scan
    /// code (they arrive as digits or as Home / Page Up / ..., depending on NumLock); arrows are
    /// left to the ordinary key translation.
    /// </summary>
    public static NavCommand? Translate(uint vk, uint scan, bool extended)
    {
        if (vk == VK_F23) return NavCommand.Back;
        if (extended) return null;
        return scan switch
        {
            0x51 => NavCommand.Accept,    // numpad 3
            0x4C => NavCommand.Menu,      // numpad 5
            0x47 => NavCommand.PrevPage,  // numpad 7
            0x49 => NavCommand.NextPage,  // numpad 9
            _ => null,
        };
    }

    /// <summary>A key the panel mapping sends (arrows included), as opposed to Desktop-mode input.</summary>
    public static bool IsPanelKey(uint vk, uint scan, bool extended) =>
        Translate(vk, scan, extended) != null || (extended && vk is >= 0x25 and <= 0x28);
}
