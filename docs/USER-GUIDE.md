# Claw Control user guide

Claw Control is a quick settings panel for the MSI Claw A8 that you drive with the controller.
It floats over the right side of the screen, over whatever game is running, and opens
instantly because it's always ready in the background.

- [Opening and closing](#opening-and-closing)
- [Controls](#controls)
- [The header](#the-header)
- [Quick tab](#quick-tab)
- [Graphics tab](#graphics-tab)
- [Controller tab](#controller-tab)
- [Apps & power](#apps--power)
- [Performance overlay](#performance-overlay)
- [Good to know](#good-to-know)

## Opening and closing

- **Open:** press the **MSI Center M button**. The panel opens on the tab you used last (the Quick
  tab starts on the volume bar).
- **Close:** press the MSI Center M button again, press **B** (on the first level), or tap
  anywhere outside the panel.

While the panel is open, the buttons and triggers only drive the panel: the game behind it (and
Steam) doesn't see them, in Xbox mode or in MSI's Desktop (mouse) mode. The sticks still reach
the game, so the panel doesn't use them: move with the D-pad.

## Controls

| Action | Xbox buttons | PlayStation names | Keyboard |
|---|---|---|---|
| Move the selection | D-pad | D-pad | Arrow keys |
| Change a bar's value | D-pad left / right on the bar | D-pad left / right | Left / Right |
| Select, switch on / off | **A** | **✕ Cross** | Enter or Space |
| Back one screen / close | **B** | **○ Circle** | Esc |
| Previous / next tab | **LB / RB** (bumpers) | **L1 / R1** | Page Up / Page Down, or Q / E |
| Apps & power menu | **Y** | **△ Triangle** | |

- D-pad **up** from the top of a page reaches the tab row (left / right there switches tabs);
  up again reaches the app chip in the header.
- The selection is a glass drop that slides to whatever you select. The footer shows what **A**
  does on the selected control ("Select", "End", "Connect"…).
- **Touch** works everywhere: tap anything, tap a tab, slide a finger along a bar.
- Some actions ask for a second **A** to be sure (Restart, Shut down, ending apps). The first
  press arms them; they disarm after a few seconds.

## The header

From left to right: the **time**, the **app chip** (the app that was in front when you opened
the panel; opens [Apps & power](#apps--power)), **Bluetooth** and **Wi-Fi** (bright with a name =
connected, faint = on but not connected, crossed out = off) and the **battery**.

## Quick tab

Top to bottom:

- **Now playing** (only while music or video plays): artwork, title, artist, and previous /
  play-pause / next.
- **Controller mode circle**: flips the controller between Xbox (gamepad) and Desktop (mouse)
  mode. PlayStation mode and Off are on the [Controller tab](#controller-tab).
- **Wi-Fi tile**: **A** turns Wi-Fi on / off; **›** opens the list of networks. Saved and open
  networks connect from the list; a new network that needs a password opens Windows' own Wi-Fi
  list, which has a keyboard.
- **Bluetooth tile**: **A** turns Bluetooth on / off; **›** opens your paired devices.
  Headphones and speakers connect and disconnect right from the list (with their battery level
  when they report it). Other devices open Windows Settings; pair new devices there too.
- **Volume**: left / right changes it; **A** (or its arrow) opens **Sound**: choose the output
  device (speakers, headphones, TV…) and set the volume of each app that's playing sound.
- **Brightness**.
- **Power modes**, four circles. The caption under them shows the one under the selection and its
  limits before you pick it:

  | Mode | Sustained / slow boost / fast boost |
  |---|---|
  | Super Battery (green) | 15 / 28 / 33 W |
  | Balanced (blue) | 20 / 33 / 43 W |
  | Performance (orange) | 28 / 45 / 55 W |
  | Custom (purple) | your own limits |

- **Custom limits** (only in Custom): **Sustained** (7-35 W), **Slow boost** (9-50 W) and **Fast
  boost** (9-60 W). The values are applied to the firmware right away and written to MSI's
  settings too, so MSI Center M keeps them after sleep or plugging in the charger.
- **Overlay**: switches the [performance overlay](#performance-overlay) on or off. While it's on,
  pick where it sits on a mini screen and which numbers it shows.

## Graphics tab

AMD driver settings and the screen. Settings the driver doesn't support are hidden.

- **FPS limit**: 30-120 FPS in steps of 5, one step further is **Unlimited**. It uses Radeon
  Chill with the same minimum and maximum, which works in DirectX 12 games where AMD's older
  frame-rate limiter doesn't.
- Four circles: **Frame gen** (AMD Fluid Motion Frames), **Upscaling** (Radeon Super
  Resolution), **Sharpen** (Radeon Image Sharpening) and **FreeSync**.
- **Upscaling sharpness** (only while Upscaling is on) and **Sharpness** (only while Sharpen is
  on).
- **Resolution**: 16:10 resolutions that fill the screen, then 16:9 ones (with black bars).
  Changing the resolution keeps the refresh rate when the new one supports it.
- **Refresh rate**: what Windows offers for the current resolution.
- **Scaling**: Windows' own scaling steps for the current resolution (for example 100-200 %). They
  apply at once. Some older apps look blurry until you reopen them.
- **Graphics memory**: how much RAM is set aside for the GPU (0.5-16 GB). It takes effect after a
  restart: **Restart now** appears, and it needs a second **A**.

> [!NOTE]
> Frame generation, upscaling and the FPS limit are read by the driver when a game starts its
> graphics. A change reaches a game that's already running only after you restart the game. This
> is how AMD's driver works, not a Claw Control limitation.

## Controller tab

Settings stored in the controller itself, so they work everywhere and stay after a restart.
Changes to **Buttons** and the **Gyro** take effect when you close the panel (while it's open the
buttons are busy driving the panel); everything else changes at once.

- **Mode circles**: **Xbox**, **PlayStation** (DirectInput; for games and emulators that expect a
  DualShock-style pad), **Desktop** (mouse and keyboard: A = left click, B = right click, the stick
  moves the pointer) and **Off**. The new mode is applied **when you close the panel**: the panel
  itself needs Xbox mode while it's open.
- **Lights** bar: brightness of the stick rings and the ABXY light (0 = off). **A** (or its arrow)
  opens **Lights**: colours (Red, Orange, Yellow, Green, Cyan, Blue, Purple, Pink, White), effects
  (Steady, Breathe, Rainbow) and effect speed.
- **Left rumble** and **Right rumble**: 0-100 % per motor (left is the heavy one). Each buzzes
  briefly when you stop on a value, so you feel it.
- **Gyro**: **A** switches it on / off; **›** opens **Gyro**: moves the right stick or the mouse,
  speed, and "Only while holding LT".
- **Sticks**: dead zone and outer edge for each stick, swap sticks, and a live view of both
  sticks so you can see the effect.
- **Triggers**: where each trigger starts and where it's fully pressed, **Quick trigger** (a light
  press counts as full), "Same for both", and a live meter. While this screen is open the
  triggers act at once (and reach the game too).
- **Buttons**: remap A, B, X, Y, LB, RB, LT, RT, the stick clicks, the D-pad and the back buttons
  M1 / M2 to another button (or to nothing), with optional **Turbo** (repeats while held); plus
  **Nintendo layout** (swaps A / B and X / Y) and **D-pad diagonal lock**. Changed buttons show
  what they do now.

## Apps & power

Open it with **Y** (Triangle), the **app chip** in the header, or the **Y** hint in the footer.

- **End [app]**: closes the app that was in front when you opened the panel (first **A** arms,
  second **A** ends it). It's asked to close first; a frozen app is ended at once.
- **Running apps ›**: every open app with its memory use and a "Not responding" mark when frozen.
  **A** ends one. **Close background apps** ends all of them except the one in front.
- **Sleep**, **Hibernate** (only when hibernation is enabled in Windows), **Restart** and
  **Shut down** (these two need a second **A**).

## Performance overlay

Small numbers over your game, switched on from the Quick tab's **Overlay** tile. Choose a
position (four corners, or top / bottom centre in a row) and up to twelve numbers: **FPS**,
**Power** (W), **Temp** (CPU), **Fan**, **CPU** (%), **CPU clock**, **GPU** (%), **VRAM**,
**RAM**, **Battery**, **TDP** (the current limits) and **Time**.

FPS comes from Intel PresentMon, which reads Windows' frame events, so it works in every game and
graphics API without hooking into the game. The overlay and PresentMon only run while the overlay
is on.

## Good to know

- **Everything applies immediately** (except the controller mode, applied on close, and graphics
  memory, after a restart). There's no Save button.
- **Battery:** with the panel closed, Claw Control uses about 0.3 % of one CPU core and polls
  nothing (about 1 % with the overlay on).
- **MSI Center M still works.** Open it from the Start menu for anything Claw Control doesn't do
  (firmware updates, Game Library, Mystic Light styles).
- **The size stays the same** at any resolution or Windows scaling: the panel always covers the
  same share of the screen, pinned to the right edge.
- Problems? See [Troubleshooting](TROUBLESHOOTING.md).
