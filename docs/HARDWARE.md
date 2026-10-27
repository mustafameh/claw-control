# How Claw Control drives the hardware

Everything here was worked out on an MSI Claw A8 (model code `1T8K`) with MSI Center M
installed. Other Claw models use different firmware and are not supported.

## MSI firmware: power limits (WMI `root\WMI`)

- `MSI_ACPI.Get_Data` / `Set_Data`, argument `Data` = `Package_32` with `Bytes[32]`:
  `[0]` = index, `[1]` = value. Reply `[0]` = status (1 = ok), `[1]` = value. Admin only.
- `0x50` SPL, `0x51` SPPT, `0x52` FPPT (watts). Caps 35 / 50 / 60; lowest values used 7 / 9 / 9.
- Write order (as MSI Center M does it): if SPL changes write SPL = 7 first, if SPPT changes
  write SPPT = 9, then FPPT, SPPT, SPL.
- `0xD2` shift (fan / EC profile) = `0xC0 | type`: Performance and Manual `0xC4`, Balanced `0xC1`,
  Super Battery `0xC2`. Only write if the top two bits are set.
- `0xC5` AMD STAPM flag (bit 7 supported, bit 0 on): unused.
- Presets: Performance 28 / 45 / 55 W, Balanced 20 / 33 / 43, Super Battery 15 / 28 / 33 (with a
  30 FPS cap on battery).
- MSI keeps manual values in `HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center M\Component\User Scenario`
  (`ManualSPL` / `ManualSPPT` / `ManualFPPT` DWORDs) and re-applies them on resume and plug /
  unplug. Claw Control mirrors its values there so MSI's re-apply writes the same, and re-applies
  4 s after resume or a power-source change.

## Buttons (WMI `root\WMI` `MSI_Event`, `MSIEvt & 0xFF`)

- 41 = MSI Center M button, 88 = Quick Settings button, 42 / 49 = long presses,
  19 = charger plugged / unplugged.
- Taking over 41 (`Hardware/MsiButtonTakeover`), no MSI files changed:
  - `MSI_Center_M_Server` only acts on 41 if no process named `MSI_Center_M_Launcher` exists, and
    restarts a closed launcher every second forever, so the launcher is **suspended**
    (`NtSuspendProcess`). On exit it's closed and MSI starts a fresh one about a second later.
  - `MSI Center M.exe` stays resident and hidden after its first use and has its own listener for
    41, so it's closed whenever it has no visible window (60 s grace for a fresh launch; opened on
    purpose, it's left alone).
  - The launcher's single-instance mutex: `Global\MSI_Center_M_Launcher.exe`.
- 88 is left to MSI (it opens MSI's Game Bar widget, if installed).

## Controller chip (HID)

- VID `0DB0`, PID `1901` (Xbox mode; `1902` also listed). The settings channel is the interface
  whose descriptor has **output report 15** (`MI_01`, "vendor-defined device"), 64-byte reports.
  Opens without admin and alongside MSI's `MSI_Center_M_Server_ControlMode` (each handle gets
  every reply). HidSharp works.
- Packet: `0F 00 00 3C <cmd> <args...>` padded to 64. Replies: byte 4 = reply command.
  Commands: 4 ReadProfile `[0, offH, offL, len ≤ 55]` → reply 5 `[idx, offH, offL, len, data]`;
  0x21 WriteProfile to RAM `[1, offH, offL, len, data]`; 34 SyncToROM (persist); 36 SwitchMode
  `[mode, mkeys]`; 38 ReadGamepadMode → reply 39 `[mode, mkeys]`; 45 DisableDevice
  `[1 = off / 0 = on]`; 224 RGBControl `[play, frame]`; 9 SyncRGB. Wait up to 1 s for a reply
  after a queued command, resend up to 5 times.
- Modes: 0 off, 1 XInput (Xbox), 2 DInput (PlayStation; MSI calls it "PS4 mode"), 3 MSI,
  4 Desktop. M-keys function: 0 macro, 1 combination, 2 disabled.
- Profile = 1478 bytes: 0 custom (32) · 32 !liftToWake · 33 reportRate · 34 / 35 motor L / R
  (0-100 %; MSI's defaults 7 / 15) · 36 audio (5) · 41 sensor (10) · 51 shift (2) · 53 quickLT ·
  54 quickRT · 55 dpadDiagonalLock · 56 swapABXY · 57 standby · 58 16 keys × 8 (Up Down Left
  Right L1 R1 L3 R3 Cross Circle Square Triangle Home Select Start Camera) · 186 M1 (8 + macro
  161) · 355 M2 · 524 L2 (15) · 539 R2 · 554 LeftStick (16) · 570 RightStick · 586 light (883) ·
  1469 extend1 (9).
  - Key (8): `[enabled = 1, flags, turboSpeed, map0..map4]`; flags bit 0 turbo (MSI never sets
    it, may not work), bit 1 dead zone, bit 2 remapped (target ≠ self). Trigger (15):
    `[en, flags, turbo, dead%, edge%, map0..4, linear(5)]`. Key codes: Up 1 Down 2 Left 3 Right 4
    LB 5 RB 6 L3 7 R3 8 A 9 B 10 X 11 Y 12 Home 13 View 14 Menu 15 Camera 16 M1 17 M2 18 LT 19
    RT 20, left stick up / down / left / right 21-24, right stick 25-28, none 255.
  - Stick (16): `[en, dzEn, dz%, mapEn, flipX, flipY, mapValue, targetEn, target, edgeEn,
    edge%, linear(5)]`; swap sticks = mapEn + mapValue (left 2, right 1).
  - Sensor (gyro, 10): `[flags, alwaysOn, holdKey, 4, antiDz, dz*254/100, sens*254/100, 255,
    255, mapSelection 0 left / 1 right stick]`; flags bit 0 on, bit 1 press to enable, bit 2 as
    mouse, 3 / 4 flip X / Y, 5 / 6 disable X / Y (keep as read), bit 7 toggle (1 + 7 = press to
    disable).
  - Light: `[activeAnimation]` + 4 animations × 220 `[frames, effect = 9, 20 - speed,
    brightness%, 8 keyframes × 9 RGB]` + audio + reserve. The 9 RGB = left stick ring 4
    (down-left, down-right, up-right, up-left), right ring 4, ABXY 1. Steady = 1 frame,
    breathe = colour + black, colour cycle = 6 frames. Write animation 0, then
    RGBControl(224, play).
- Rumble (34 / 35) is 0-100 % per motor (the Linux hid-msi driver allows 0-100 too).
- **MSI rewrites some of it:** on every mode-change reply (39), including any mode *read*, MSI
  re-applies stick dead zones from `C:\ProgramData\MSI\MSI_Center_M\Control Mode\profile.rec`
  (JSON, loaded once at boot; `GS` for XInput, `DS` for Desktop) and, only if the Mystic Light
  style is "InfoMode", the mode colour. It writes only the byte range that differs from its own
  last write, so other settings survive. So: never ask the chip for its mode routinely (each
  reply makes MSI re-save to the chip); write stick settings to `profile.rec` (`GS`) as well, and
  re-send them shortly after a mode-1 / 2 reply until the next reboot.
- `Hardware/ClawPad` keeps what the chip has and what's wanted, writes only the differing runs
  (0x21), then SyncToROM 1.5 s after the last change (flash wear). The mode is applied only when
  the panel closes: the panel needs Xbox mode while open, PlayStation mode re-enumerates the
  device, and Off would kill input. Changes to the bytes the panel mapping covers (buttons,
  gyro on / off; triggers outside the Triggers screen) also reach the chip when the panel
  closes, and nothing is saved while it's on.

## Panel mapping (controller hidden from games while the panel is open)

- Games read the gamepad directly (XInput has no notion of focus), so the panel taking the
  foreground doesn't stop them, nor Steam Big Picture. While the panel is open the chip's
  working memory gets a temporary mapping (`ControllerService.PanelMapping`, `ClawPad.SetOverlay`):
  the panel's buttons send keyboard keys (`Input/PanelKeys`), which `DesktopInputBlocker` takes
  before any app sees them (raw-input readers included); X sends numpad 1 (140, the app
  switcher's End); the stick clicks, View, Menu, LT and
  RT map to 255 (nothing; let through while the Triggers screen's live meters show); the gyro
  is off. XInput then sees no buttons and no triggers; the controller stays connected. Never
  saved (no SyncToROM while it's on); 4 messages, ~15 ms on and ~15 ms off. It comes off only
  once every press is let go: B closes the panel while still held, and with the real mapping
  back the game would see that B.
- Tried on the device (2026-10-27): arrows 128-131, numpad 139-142 / 144 / 146-148 and F23 (156)
  arrive as keys; **F22 (157) sends nothing**. Numpad keys arrive as digits or as Home / Page Up /
  ..., depending on NumLock: tell them apart by scan code (not extended). View / Menu (key blocks
  13 / 14, enabled = 0 by default) sent nothing when remapped. The Xbox button (Home) sends Win + G.
- **The sticks can't be silenced in working memory**: byte 0 = 0, a 100 % dead zone, MapEn +
  MapValue 255 and TargetEn + TargetValue 0 all left them reaching XInput. So the panel doesn't
  use the sticks.
- MSI's `SaveProfile` (after every mode change) writes and then saves the chip's whole working
  memory, so a temporary mapping can end up saved: any reply we didn't ask for while it's on
  means save the real settings again once it's off. A mode change also re-sends the mapping
  (in case the chip dropped it).
- `%LOCALAPPDATA%\ClawControl\controller-restore.bin` holds the real bytes under the mapping while
  it's on ([offset, length, bytes] runs); a start that finds it puts them back and saves.

## Controller modes and MSI's Command Center pipe

- Desktop mode: A = left (double) click, B = right click, LT / RT = Ctrl + Numpad − / +,
  LB / RB send nothing, the stick moves the cursor, XInput reports nothing.
- `\\.\pipe\Command_Center_XInput` (plain UTF-8 text request and reply, open ACL; what MSI's Game
  Bar widget uses): `xinputmode_set` → gamepad mode, `gamebarosd_disappear` → back to before.
  `Hardware/MsiControllerMode` sends these when the panel opens and closes. Also
  `embeddedcontroller_get` ("On"), `controlmode_get`, `model_get` ("MS-1T8K"). A busy pipe
  answers "access denied": space requests out.
- MSI's AI Engine (`MSI_Center_M_Server_UserScenario`) sends `GoToDesktopMode` 0.2-0.5 s after any
  non-game window comes to the front, so gamepad mode is requested again at 0.5 / 1 / 2 s and
  whenever Desktop-mode input shows up while the panel is open.
- MSI's component pipes `MCXNPS_*` are encrypted and signature-checked: not used.

## AMD graphics (ADLX)

- `Hardware/Adlx.cs`, `Hardware/AmdGraphics.cs`: `amdadlx64.dll` from the driver, called through
  the C vtables in AMD's SDK headers (GPUOpen ADLX, `SDK/Include`; indexes = positions in each
  `...Vtbl` struct).
- **Use `ADLXInitializeWithIncompatibleDriver`**: plain `ADLXInitialize` treats MSI's OEM driver
  as unknown and lists no GPUs.
- **Switch a feature on before setting its value**: SetFPS / SetSharpness fail (ADLX_FAIL) while
  FRTC / image sharpening are off.
- **The FPS limit is Radeon Chill with min = max** (range 30-300), FRTC off: FRTC (Frame Rate
  Target Control) had no effect in a DirectX 12 game. FRTC is only the fallback without Chill.
- Upscaling (RSR) returns ADLX_FAIL for every call until it has been switched on **once** in AMD
  Software (sets the driver's `Gmx_RSREnabled` from 0 to 3); after that ADLX reads and sets it.
  ADLX decides support when a session starts, so the app needs a restart to show it.
- Variable graphics memory: `IADLXSystem3` (QueryInterface on the system); presets plus Custom
  0.5-16 GB; applies after a restart.
- Frame generation, upscaling and the FPS cap are read by the driver when a game starts its
  graphics: a change reaches a running game only after the game restarts.
- Ranges: FRTC 15-1000, image sharpening 10-100 in steps of 10, RSR sharpness 0-100. The screen's
  FreeSync range is 48-120 Hz; Windows offers 60 / 120 Hz modes.

## Windows

- Resolution and refresh rate: `EnumDisplaySettings` / `ChangeDisplaySettingsEx` with
  `CDS_UPDATEREGISTRY` (`Hardware/DisplayModes`).
- Scaling: `DisplayConfigGetDeviceInfo` / `DisplayConfigSetDeviceInfo` with the undocumented
  types -3 / -4 (what Settings uses), steps counted from the recommended one
  (`Hardware/DisplayScale`). Applies at once; custom scaling (which needs a sign-out) is left out.
- Default output device: only through the undocumented `IPolicyConfig` (`[PreserveSig]` methods,
  `LPWStr` strings; `Hardware/PolicyConfig`). Volume and per-app volume: NAudio.Wasapi.
- Bluetooth headphones: Windows has no API to connect them; `Hardware/BluetoothAudio` sends the
  `KSPROPSETID_BtAudio` one-shot reconnect / disconnect (`IOCTL_KS_PROPERTY`) to the device's
  `KSCATEGORY_AUDIO` filters. Battery: PnP property `{104EA319-...} 2` by container id.
- Wi-Fi / Bluetooth state: `Windows.Devices.Radios`, `NetworkInformation`, `WiFiAdapter`,
  `DeviceInformation`; watchers run only while the panel is open.
- Now playing: `GlobalSystemMediaTransportControlsSessionManager`.
- Sleep / hibernate: `SetSuspendState`; restart / shut down: `shutdown.exe`. Ending an app:
  `WM_CLOSE` to its windows, then the whole process tree after 4 s (at once if frozen,
  `IsHungAppWindow`); Explorer is never ended.
