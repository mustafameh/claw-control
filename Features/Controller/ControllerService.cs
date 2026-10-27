using System.Text.Json.Nodes;
using ClawControl.Core;
using ClawControl.Hardware;
using ClawControl.Input;

namespace ClawControl.Features.Controller;

public enum PadMode { Xbox, PlayStation, Desktop, Off }

public enum LightEffect { Steady, Breathe, Rainbow }

/// <summary>
/// The controller settings the Controller tab shows, each mapped to its bytes in the chip's
/// settings block (see docs/HARDWARE.md, "Controller chip"). Reads come from the cached block; every
/// change goes to the chip at once through <see cref="ClawPad.Patch"/>.
///
/// Also deals with MSI's quirks: the chip mode is only switched when the panel closes (while it's
/// open the controller must stay a gamepad to drive the panel), and stick dead zones are
/// re-applied after MSI rewrites them from its own saved copy on every mode change.
///
/// While the panel is open the chip has the panel mapping on top (see <see cref="PanelMapping"/>),
/// so changes to buttons, triggers and the gyro reach the chip when the panel closes.
/// </summary>
public sealed class ControllerService
{
    // Offsets in the settings block.
    const int MotorLeft = 34, MotorRight = 35;
    const int Sensor = 41;
    const int QuickLt = 53, QuickRt = 54, DiagonalLock = 55, SwapAbxy = 56;
    const int Keys = 58, M1 = 186, M2 = 355, Lt = 524, Rt = 539, LeftStick = 554, RightStick = 570;
    const int Light = 586, AnimationLength = 220, KeyframeLength = 27;
    const int MaxMotor = 100;  // percent (as the Linux hid-msi driver allows); MSI's default is just 7 / 15

    static readonly string MsiProfilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MSI", "MSI_Center_M", "Control Mode", "profile.rec");

    /// <summary>Remappable buttons, in grid order: (id, label, key code, offset of its 8/10-byte block).</summary>
    public static readonly (string Id, string Label, int Code, int Offset, bool Trigger)[] Buttons =
    {
        ("M1", "M1", 17, M1, false), ("M2", "M2", 18, M2, false),
        ("CRO", "A", 9, Keys + 8 * 8, false), ("CIR", "B", 10, Keys + 9 * 8, false),
        ("SQU", "X", 11, Keys + 10 * 8, false), ("TRI", "Y", 12, Keys + 11 * 8, false),
        ("L1", "LB", 5, Keys + 4 * 8, false), ("R1", "RB", 6, Keys + 5 * 8, false),
        ("L2", "LT", 19, Lt, true), ("R2", "RT", 20, Rt, true),
        ("L3", "LS", 7, Keys + 6 * 8, false), ("R3", "RS", 8, Keys + 7 * 8, false),
        ("Up", "Up", 1, Keys, false), ("Down", "Down", 2, Keys + 8, false),
        ("Left", "Left", 3, Keys + 16, false), ("Right", "Right", 4, Keys + 24, false),
    };

    /// <summary>What a button can be set to act as (code → label).</summary>
    public static readonly (int Code, string Label)[] Targets =
    {
        (9, "A"), (10, "B"), (11, "X"), (12, "Y"), (5, "LB"), (6, "RB"), (19, "LT"), (20, "RT"),
        (7, "LS"), (8, "RS"), (1, "Up"), (2, "Down"), (3, "Left"), (4, "Right"),
        (21, "LS ↑"), (22, "LS ↓"), (23, "LS ←"), (24, "LS →"), (25, "RS ↑"), (26, "RS ↓"), (27, "RS ←"), (28, "RS →"),
        (255, "Nothing"),
    };

    public static string TargetLabel(int code) => Targets.FirstOrDefault(t => t.Code == code).Label ?? $"#{code}";

    readonly ClawPad _pad;
    readonly Settings _settings;
    readonly Timer _stickReassert;
    bool _panelOpen;
    PadMode? _pendingMode;

    /// <summary>Anything shown on the Controller tab may have changed (any thread).</summary>
    public event Action? Changed;

    public ControllerService(ClawPad pad, Settings settings)
    {
        _pad = pad;
        _settings = settings;
        _pad.Changed += () => { NoteChipMode(); Changed?.Invoke(); };
        // MSI rewrites stick dead zones from its own saved copy on every mode change; put ours back.
        _stickReassert = new Timer(_ => ReassertSticks());
        _pad.ModeReported += mode =>
        {
            if (mode is 1 or 2) _stickReassert.Change(600, Timeout.Infinite);
            NoteChipMode();
            Changed?.Invoke();
        };
    }

    public bool Ready => _pad.Profile != null;

    /// <summary>Reads the chip once in the background (startup) and when the tab is shown.</summary>
    public Task<bool> RefreshAsync() => _pad.LoadAsync();

    byte At(int offset) => _pad.Profile is { } p ? p[offset] : (byte)0;

    // ---- Mode (applied when the panel closes) ----

    public PadMode Mode => _pendingMode ?? CurrentMode;

    /// <summary>A different mode was chosen; it applies when the panel closes.</summary>
    public bool ModePending => _pendingMode is { } mode && mode != CurrentMode;

    PadMode CurrentMode => _settings.ControllerOff ? PadMode.Off : _userMode switch
    {
        2 => PadMode.PlayStation,
        4 => PadMode.Desktop,
        _ => PadMode.Xbox,
    };

    /// <summary>
    /// The mode you're in outside the panel. While the panel is open the chip is temporarily in
    /// Xbox mode (so the panel can be driven), and MSI passes through mode 3 when switching back;
    /// neither counts.
    /// </summary>
    int _userMode
    {
        get => _settings.ControllerMode;
        set
        {
            if (_settings.ControllerMode == value) return;
            _settings.ControllerMode = value;
            _settings.SaveSoon();
        }
    }

    void NoteChipMode()
    {
        if (!_panelOpen && _pad.Mode is 1 or 2 or 4) _userMode = _pad.Mode.Value;
    }

    /// <summary>Remembers the choice; the chip switches when the panel closes (see PanelClosed).</summary>
    public void SetMode(PadMode mode)
    {
        _pendingMode = mode;
        if (!_panelOpen) _ = ApplyPendingMode();
        Changed?.Invoke();
    }

    /// <summary>
    /// The panel opened: if the controller is off, turn it on so the panel can be used, and keep
    /// its presses from the game behind (the panel mapping).
    /// </summary>
    public void PanelOpened()
    {
        _panelOpen = true;
        _triggersLive = false;
        if (_settings.ControllerOff) _ = _pad.SetDisabledAsync(false);
        if (_settings.HideControllerFromGames) _ = _pad.SetOverlay(PanelMappingOverlay);
    }

    /// <summary>
    /// The panel closed: once the controller is let go (<paramref name="letGo"/>; the press that
    /// closed the panel would reach the game otherwise) the real mapping goes back, then the
    /// chosen mode / off applies.
    /// </summary>
    public void PanelClosed(Task letGo)
    {
        _panelOpen = false;
        _triggersLive = false;
        _ = EndPanelMappingAfter(letGo);
        _ = ApplyPendingMode(letGo);
    }

    async Task EndPanelMappingAfter(Task letGo)
    {
        await letGo;
        if (!_panelOpen) await _pad.SetOverlay(null);  // unless it opened again meanwhile
    }

    /// <summary>Takes the panel mapping off and waits for the chip to have it (quitting).</summary>
    public void EndPanelMapping(TimeSpan timeout) => _pad.SetOverlay(null).Wait(timeout);

    /// <summary>
    /// The Triggers screen's live meters are showing: let the triggers through while they are
    /// (they reach the game too, but you're pressing them on purpose).
    /// </summary>
    public void SetTriggersLive(bool live)
    {
        if (_triggersLive == live) return;
        _triggersLive = live;
        if (_panelOpen && _settings.HideControllerFromGames)
            _ = _pad.SetOverlay(live ? PanelMappingLiveTriggersOverlay : PanelMappingOverlay);
    }

    bool _triggersLive;

    // ---- Panel mapping ----

    static readonly Func<byte[], byte[]> PanelMappingOverlay = wanted => PanelMapping(wanted, triggers: true);
    static readonly Func<byte[], byte[]> PanelMappingLiveTriggersOverlay = wanted => PanelMapping(wanted, triggers: false);

    /// <summary>
    /// What the chip gets while the panel is open (docs/HARDWARE.md "Panel mapping"). Games read
    /// the gamepad directly, whatever window is in front, so the panel's buttons send keys instead
    /// (Input/PanelKeys), which the input blocker takes before any app sees them; every other
    /// button and both triggers send nothing, and the gyro is off. Only the chip's working memory
    /// gets this, never its saved settings. The sticks can't be silenced this way and still reach
    /// the game, so the panel doesn't use them. <paramref name="triggers"/> false: the triggers
    /// stay as they are (Triggers screen).
    /// </summary>
    static byte[] PanelMapping(byte[] wanted, bool triggers)
    {
        var b = wanted.ToArray();
        void Map(int block, bool trigger, int code)
        {
            b[block] = 1;
            b[block + 1] = (byte)((b[block + 1] & ~0x01) | 0x04);  // no turbo; remapped
            int map = block + (trigger ? 5 : 3);
            b[map] = (byte)code;
            b[map + 1] = b[map + 2] = b[map + 3] = b[map + 4] = 0;
        }
        // Key blocks in the chip's order: Up Down Left Right LB RB LS RS A B X Y Home View Menu
        // Camera. Home (the Xbox button, which sends Win + G) and Camera stay as they are.
        int[] codes =
        {
            PanelKeys.Up, PanelKeys.Down, PanelKeys.Left, PanelKeys.Right,
            PanelKeys.PrevPage, PanelKeys.NextPage, PanelKeys.Nothing, PanelKeys.Nothing,
            PanelKeys.Accept, PanelKeys.Back, PanelKeys.Nothing, PanelKeys.Menu,
            -1, PanelKeys.Nothing, PanelKeys.Nothing, -1,
        };
        for (int i = 0; i < codes.Length; i++)
            if (codes[i] >= 0) Map(Keys + i * 8, false, codes[i]);
        if (triggers)
        {
            Map(Lt, true, PanelKeys.Nothing);
            Map(Rt, true, PanelKeys.Nothing);
        }
        b[Sensor] = (byte)(b[Sensor] & ~0x01);  // gyro off
        return b;
    }

    /// <summary>
    /// Notes the mode once MSI has restored it after the panel closed. MSI reads the mode itself
    /// after switching and we hear that reply, so there's no need to ask (asking makes MSI
    /// re-save its settings to the chip).
    /// </summary>
    async Task SettleMode()
    {
        await Task.Delay(1500);
        NoteChipMode();
        Changed?.Invoke();
    }

    async Task ApplyPendingMode(Task? letGo = null)
    {
        if (letGo != null) await letGo;
        // Give MSI's own "panel closed" mode restore time to land first.
        await Task.Delay(700);
        if (_panelOpen) return;
        var mode = _pendingMode;
        _pendingMode = null;
        if (mode == null)
        {
            if (_settings.ControllerOff) await _pad.SetDisabledAsync(true);
            else await SettleMode();
            return;
        }
        _settings.ControllerOff = mode == PadMode.Off;
        _settings.SaveSoon();
        if (mode == PadMode.Off)
        {
            await _pad.SetDisabledAsync(true);
        }
        else
        {
            int raw = mode switch { PadMode.PlayStation => 2, PadMode.Desktop => 4, _ => 1 };
            _userMode = raw;
            if (_pad.Mode != raw) await _pad.SwitchModeAsync(raw);
        }
        Changed?.Invoke();
    }

    // ---- Vibration ----

    /// <summary>Strength of the left (heavy) or right (light) motor, 0-100 %.</summary>
    public int Rumble(bool left) => Math.Min(MaxMotor, (int)At(left ? MotorLeft : MotorRight));

    public void SetRumble(bool left, int percent)
    {
        _pad.Patch(left ? MotorLeft : MotorRight, (byte)Math.Clamp(percent, 0, MaxMotor));
        UpdateMsiProfile(p => p["MP"] = new JsonObject { ["LeftMotorValue"] = Rumble(true), ["RightMotorValue"] = Rumble(false) });
    }

    // ---- Sticks ----

    public int Deadzone(bool left) => At((left ? LeftStick : RightStick) + 2);
    public int OuterEdge(bool left) => At((left ? LeftStick : RightStick) + 10);
    public bool InvertY => At(LeftStick + 5) == 1;
    public bool SwapSticks => At(LeftStick + 3) == 1 && At(LeftStick + 6) == 2;

    public void SetDeadzone(bool left, int percent) => PatchStick(left, 2, 1, percent);
    public void SetOuterEdge(bool left, int percent) => PatchStick(left, 10, 9, percent);

    void PatchStick(bool left, int valueOffset, int enableOffset, int percent)
    {
        int stick = left ? LeftStick : RightStick;
        _pad.Patch(stick + enableOffset, 1);
        _pad.Patch(stick + valueOffset, (byte)Math.Clamp(percent, 0, 100));
        SaveSticksToMsi();
    }

    public void SetInvertY(bool on)
    {
        _pad.Patch(LeftStick + 5, (byte)(on ? 1 : 0));
        _pad.Patch(RightStick + 5, (byte)(on ? 1 : 0));
    }

    public void SetSwapSticks(bool on)
    {
        _pad.Patch(LeftStick + 3, (byte)(on ? 1 : 0));
        _pad.Patch(LeftStick + 6, (byte)(on ? 2 : 0));
        _pad.Patch(RightStick + 3, (byte)(on ? 1 : 0));
        _pad.Patch(RightStick + 6, (byte)(on ? 1 : 0));
        SaveSticksToMsi();
    }

    /// <summary>Puts our stick settings back after MSI overwrote them on a mode change.</summary>
    void ReassertSticks()
    {
        // Only needed when we changed sticks since MSI loaded its copy (at boot); otherwise MSI
        // writes back exactly what's there, and rewriting would just wear the chip's memory.
        if (_sticksChanged) _pad.Rewrite(LeftStick, 32);
    }

    bool _sticksChanged;

    /// <summary>MSI's own saved copy, so after a reboot MSI applies our sticks instead of fighting them.</summary>
    void SaveSticksToMsi()
    {
        _sticksChanged = true;
        if (_pad.Profile is not { } p) return;
        var sticks = new JsonObject
        {
            ["LSDZ"] = p[LeftStick + 2], ["LSEDZ"] = p[LeftStick + 10], ["LSS"] = SwapSticks,
            ["RSDZ"] = p[RightStick + 2], ["RSEDZ"] = p[RightStick + 10], ["RSS"] = SwapSticks,
        };
        UpdateMsiProfile(json => json["GS"] = sticks);
    }

    // ---- Triggers ----

    public int TriggerStart(bool left) => At((left ? Lt : Rt) + 3);
    public int TriggerFull(bool left) => At((left ? Lt : Rt) + 4);
    public bool QuickTrigger(bool left) => At(left ? QuickLt : QuickRt) == 1;

    public void SetTriggerStart(bool left, int percent) => PatchTrigger(left, 3, percent);
    public void SetTriggerFull(bool left, int percent) => PatchTrigger(left, 4, percent);

    void PatchTrigger(bool left, int valueOffset, int percent)
    {
        int trigger = left ? Lt : Rt;
        _pad.Patch(trigger, 1);
        _pad.Patch(trigger + 1, (byte)(At(trigger + 1) | 0x02));  // dead zone on
        _pad.Patch(trigger + valueOffset, (byte)Math.Clamp(percent, 0, 100));
        if (_pad.Profile is { } p)
            UpdateMsiProfile(json => json["TP"] = new JsonObject
            {
                ["LTDZ"] = p[Lt + 3], ["LTEDZ"] = p[Lt + 4], ["RTDZ"] = p[Rt + 3], ["RTEDZ"] = p[Rt + 4], ["MT"] = false,
            });
    }

    public void SetQuickTrigger(bool left, bool on) => _pad.Patch(left ? QuickLt : QuickRt, (byte)(on ? 1 : 0));

    /// <summary>The Triggers screen edits LT and RT together (remembered).</summary>
    public bool TriggersTogether
    {
        get => _settings.TriggersTogether;
        set
        {
            _settings.TriggersTogether = value;
            _settings.SaveSoon();
            Changed?.Invoke();
        }
    }

    // ---- Buttons ----

    /// <summary>Key code the button acts as (its own code when not remapped).</summary>
    public int MappedTo(string id)
    {
        var b = Buttons.First(x => x.Id == id);
        int map = At(b.Offset + (b.Trigger ? 5 : 3));
        return map == 0 ? b.Code : map;
    }

    public bool Turbo(string id) => (At(Buttons.First(x => x.Id == id).Offset + 1) & 0x01) != 0;

    public void SetMapping(string id, int code)
    {
        var b = Buttons.First(x => x.Id == id);
        byte flags = At(b.Offset + 1);
        flags = (byte)(code != b.Code ? flags | 0x04 : flags & ~0x04);
        _pad.Patch(b.Offset, 1, flags);
        // Only the first of the five mapping slots is used; clear the rest.
        _pad.Patch(b.Offset + (b.Trigger ? 5 : 3), (byte)code, 0, 0, 0, 0);
    }

    public void SetTurbo(string id, bool on)
    {
        var b = Buttons.First(x => x.Id == id);
        byte flags = At(b.Offset + 1);
        _pad.Patch(b.Offset + 1, (byte)(on ? flags | 0x01 : flags & ~0x01));
        if (on && At(b.Offset + 2) == 0) _pad.Patch(b.Offset + 2, 12);  // MSI's default speed
    }

    public bool NintendoLayout => At(SwapAbxy) == 1;
    public bool DiagonalLockOn => At(DiagonalLock) == 1;
    public void SetNintendoLayout(bool on) => _pad.Patch(SwapAbxy, (byte)(on ? 1 : 0));
    public void SetDiagonalLock(bool on) => _pad.Patch(DiagonalLock, (byte)(on ? 1 : 0));

    public int ChangedButtonCount => Buttons.Count(b => MappedTo(b.Id) != b.Code && !(b.Id is "M1" or "M2"));

    // ---- Gyro ----

    public bool GyroOn => (At(Sensor) & 0x01) != 0;
    public bool GyroAsMouse => (At(Sensor) & 0x04) != 0;
    public bool GyroOnlyWhileLt => At(Sensor + 1) == 0 && At(Sensor + 2) == 19;
    public int GyroSpeed => (int)Math.Round(At(Sensor + 6) * 100.0 / 254.0);

    public void SetGyro(bool on, bool? asMouse = null, bool? onlyWhileLt = null, int? speed = null)
    {
        bool mouse = asMouse ?? GyroAsMouse;
        bool holdLt = onlyWhileLt ?? GyroOnlyWhileLt;
        int sens = speed ?? (GyroSpeed == 0 ? 50 : GyroSpeed);
        // Keep the bits MSI keeps (flip / disable axes, 3-6); set on, mouse, press-to-enable.
        int flags = At(Sensor) & 0b0111_1000;
        if (on) flags |= 0x01;
        if (mouse) flags |= 0x04;
        if (holdLt) flags |= 0x02;
        _pad.Patch(Sensor, (byte)flags, (byte)(holdLt ? 0 : 1), (byte)(holdLt ? 19 : At(Sensor + 2)), 4, At(Sensor + 4),
            At(Sensor + 5), (byte)Math.Round(Math.Clamp(sens, 0, 100) * 254 / 100.0), 255, 255, (byte)(mouse ? 0 : 1));
    }

    // ---- Lights ----

    int Animation0 => Light + 1;
    public int LightBrightness => At(Animation0 + 3);
    public (byte R, byte G, byte B) LightColor => (At(Animation0 + 4), At(Animation0 + 5), At(Animation0 + 6));
    public LightEffect Effect => At(Animation0) switch { >= 3 => LightEffect.Rainbow, 2 => LightEffect.Breathe, _ => LightEffect.Steady };
    public int LightSpeed => 20 - Math.Min(20, (int)At(Animation0 + 2));

    public void SetLightBrightness(int percent)
    {
        _pad.Patch(Light, 0);
        _pad.Patch(Animation0 + 3, (byte)Math.Clamp(percent, 0, 100));
        _pad.PlayLights();
    }

    public void SetLights(LightEffect effect, (byte R, byte G, byte B) color, int? speed = null)
    {
        static byte[] Frame((byte R, byte G, byte B) c) =>
            Enumerable.Repeat(new[] { c.R, c.G, c.B }, 9).SelectMany(x => x).ToArray();
        (byte, byte, byte)[] rainbow = { (255, 0, 0), (255, 255, 0), (0, 255, 0), (0, 255, 255), (0, 0, 255), (255, 0, 255) };
        var frames = new byte[8 * KeyframeLength];
        int count;
        switch (effect)
        {
            case LightEffect.Breathe:
                count = 2;
                Frame(color).CopyTo(frames, 0);  // second frame stays dark
                break;
            case LightEffect.Rainbow:
                count = rainbow.Length;
                for (int i = 0; i < rainbow.Length; i++) Frame(rainbow[i]).CopyTo(frames, i * KeyframeLength);
                break;
            default:
                count = 1;
                Frame(color).CopyTo(frames, 0);
                break;
        }
        int s = speed ?? (effect == LightEffect.Breathe ? 15 : 17);
        _pad.Patch(Light, 0);
        // Picking a colour or effect while the lights are off (brightness 0) turns them on.
        byte brightness = At(Animation0 + 3) is 0 ? (byte)50 : At(Animation0 + 3);
        _pad.Patch(Animation0, (byte)count, 9, (byte)(20 - Math.Clamp(s, 0, 20)), brightness);
        _pad.Patch(Animation0 + 4, frames);
        _pad.PlayLights();
    }

    // ---- MSI's saved copy ----

    /// <summary>Edits MSI's profile.rec (it reads it at boot); never fatal.</summary>
    static void UpdateMsiProfile(Action<JsonObject> change)
    {
        try
        {
            if (!File.Exists(MsiProfilePath)) return;
            if (JsonNode.Parse(File.ReadAllText(MsiProfilePath)) is not JsonObject json) return;
            change(json);
            File.WriteAllText(MsiProfilePath, json.ToJsonString());
        }
        catch (Exception ex)
        {
            Log.Error("Updating MSI's saved controller settings", ex);
        }
    }
}
