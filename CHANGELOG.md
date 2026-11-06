# Changelog

## 0.1.12 (2026-11-06)

- **Now playing, redone:** the cover's colours glow behind the tile and faint bars in the
  cover's colour move with the sound. Compact it shows the cover, title and time, with previous
  / play-pause (a ring shows how far the track is) / next. Select it and it opens: big cover,
  artist and app, a seek line with the time gone and left (**left / right** jump 10 s, or drag
  it), big previous / play / next. Works with Spotify, browsers (YouTube…) and any app in
  Windows' media controls; seeking where the app allows it. The bars listen to the sound only
  while the panel shows a playing track; nothing is recorded.
- **Fixed:** after Next in a browser in the background (Brave), the tile could keep showing the
  browser's icon instead of the new cover until you switched to the browser.
- **Fixed:** a faint square box under the panel's bottom corners over bright pictures (its
  shadow was cut off in a straight line).
- **Button hints** look like the buttons: A / B / X / Y in circles, LB / RB in capsules.
- **Fixed:** the panel didn't show over games in exclusive full screen (Crash Bandicoot N. Sane
  Trilogy, for one): since 0.1.10 it left the game the focus, and such a game hides every window
  over it. Over those games the panel takes the focus again (the game steps out of full screen
  while it's open) and closing it puts you back in the game. Borderless full-screen games keep
  the focus as before.
- **App switcher:** Close background apps is now a dark glass pill with a red ✕ and bigger text,
  readable over any picture (over a white page the old text all but disappeared).
- **Library tab:** pinned apps and your games as covers (Steam's art; Epic covers downloaded once
  from Epic's image server when you add the game) with the name in a poster font, last played first;
  "Playing" on a running game (A goes back to it) and "Starting…" until a game opens. Games are
  only added when you ask: **Find games on this Claw** reads your Steam, Epic and Xbox folders (no
  sign-in) and lists them unticked for you to pick; **Installed apps** adds any app from the
  Start menu as a game (games with their own installer); **Add a program** adds any
  `.exe`. Turn the tab off with Edit on the Quick tab.
- **Tabs:** Graphics · Library · Quick · Controller (Quick in the middle); the panel is a little
  wider so every tab name fits. The first opening after Claw Control starts (after a reboot) is
  always on the Quick tab; after that it opens where you left it.
- **Closer to Apple's Liquid Glass:** the glass deepens over bright pictures so text stays
  readable; lists fade softly at an edge with more to scroll instead of being cut off; corners
  nest inside the panel's (a slightly rounder panel, tighter inner corners); Windows'
  "Animation effects" off turns the motion off (Reduce Motion).
- **Overlay screen:** three rows of numbers per page.
- **Quick tab:** the mode circle, Wi-Fi, Bluetooth, Screenshot, Record and Overlay are separate
  tiles that move on their own in Edit (left / right past a neighbour, up / down to another
  row). The overlay is a half-width tile like Wi-Fi: A turns it on or off, › opens its settings.

## 0.1.11 (2026-11-03)

- **Fixed:** the controller switched back from mouse to Xbox mode (or the other way) a few
  seconds after you picked a mode. MSI's AI Engine puts back the mode it thinks you chose
  whenever the window in front changes, and Claw Control never told it; now it does, the way
  MSI's own Control Mode screen does. Claw Control also no longer asks MSI's Command Center to
  switch modes (its late answers piled up) and switches the controller itself.

## 0.1.10 (2026-10-30)

- **Fixed:** opening the panel over a full-screen game took its focus, so many games left full
  screen (back to the desktop's resolution, which also ended RSR). Over a full-screen game the
  panel now opens without taking the focus; the controller drives it as before.

## 0.1.9 (2026-10-30)

- **Fixed:** the overlay's Frame gen number only showed a dash: PresentMon names its frame-time
  column differently when it tracks generated frames, so Claw Control fell back to FPS only.

## 0.1.8 (2026-10-30)

- **Updates in the app:** once a day, when the panel opens, Claw Control asks GitHub whether a
  new version is out. The first time it finds one, the panel opens on **Update** (what's new and
  **Update now**); after that a green sign glows next to the power sign until it's installed.
  Update now downloads the setup, checks it against the release's checksums and installs it.

## 0.1.7 (2026-10-30)

- **Fixed:** Frame gen didn't switch on, and Upscaling was missing from the Graphics tab, until
  AMD Software had been opened. AMD's driver takes those two only while AMD Software's settings
  service runs; Claw Control now starts it when needed (AMD Software flashes open for a moment
  and closes) and connects to the driver again.

## 0.1.6 (2026-10-30)

- **Quick tab: arrange it.** Edit at the bottom of the tab: move tiles with A and up / down,
  hide them with X. While editing: **Panel size** (80-130 %, everything scales together) and
  **Side** (left or right; the app switcher moves to the other side of the panel).
- **Screenshot and Record tiles**: saved to Videos › Captures or a folder you pick (a D-pad
  folder browser), recordings with the game's sound on the GPU's video encoder. Screenshot's ›:
  size; Record's ›: frame rate, quality, sound. Works without Windows' Game Bar capture.
- **Overlay**: Size bar; **Frame gen** and **Upscaling** numbers that show what's really at work
  (AFMF's added frames, RSR or plain scaling), not just what's switched on.
- **Fixed:** in mouse mode chosen in Claw Control, LB / RB did nothing in the panel.
- **Fixed:** the glass stayed solid grey after Windows' Energy saver had been on.
- **Fixed:** long pages (and the panel above 100 %) stopped well short of the taskbar.
- **Fixed:** the app switcher flashed full size when the panel opened, stuttered in its first
  second (it was rebuilt up to three times) and showed the game on the arc before moving it to
  the tile. Listing apps is much lighter, the glass is updated far less, and the panel's code
  is compiled ahead of its first use.

## 0.1.5 (2026-10-27)

- **Fixed:** games that keep their logo only on their window (Fortnite and others) showed a blank
  icon in the app switcher; it now uses the icon the window shows in the taskbar. Icons that came
  out fully see-through show properly, and icons are worked out in the background.

## 0.1.4 (2026-10-27)

- **Fixed:** with a game in front the panel took almost a second to appear (finding out which app
  is in front for the app switcher waited on the busy game window). It opens at once again; the
  switcher's tile fills in a moment later.

## 0.1.3 (2026-10-27)

- **Fixed:** with no app in front (the desktop, Start or Search) the app switcher showed an empty
  glass tile; it now shows just the arc.

## 0.1.2 (2026-10-27)

- **App switcher:** floats left of the panel (small until you move into it with D-pad left, then
  it grows). The app in front on a glass tile, your other open apps on a glass arc (most recent
  first), a live preview of the selected one with its memory use or "Not responding", and Close
  background apps as the arc's last stop. **A** switches, **X** ends an app (only inside the
  switcher), swipe the arc to turn it, tap to switch. Store apps show their real name and icon.
  It replaces Running apps and End [app]; **Y** now opens just Power.
- **New look:** the panel floats with a soft shadow and a thicker-looking glass rim; sliders are
  split bars with a glass handle that glide to new values, roll their numbers and stretch at the
  ends; the selection drop also moves through the app switcher.
- The Quick tab opens on the controller mode circle.

## 0.1.1 (2026-10-27)

- **Fixed:** in Xbox mode the game and Steam Big Picture behind the panel also reacted to the
  buttons. While the panel is open the controller now sends its buttons to the panel only (a
  temporary mapping in the controller, put back when the panel closes; the controller stays
  connected). The sticks can't be held back this way and still reach the game, so the panel no
  longer moves with the left stick: use the D-pad.
- Changes to buttons and the gyro on the Controller tab take effect when the panel closes.

## 0.1.0 (first public release)

First release, for the MSI Claw A8 only (built and tested on one Claw A8).

- **Panel:** opens instantly on the MSI Center M button, fully driven by the controller (D-pad,
  A / B, LB / RB, Y) or touch. Floating glass card that keeps the same size at any resolution and
  Windows scaling. The controller never reaches the game behind the panel, in Xbox or Desktop mode.
- **Quick tab:** now playing, controller mode (Xbox ⇄ mouse), Wi-Fi and Bluetooth (connect saved
  networks and headphones from the list), volume with output devices and per-app volume,
  brightness, power modes (Super Battery, Balanced, Performance, Custom with your own limits),
  performance overlay settings.
- **Graphics tab:** FPS limit, frame generation, upscaling, sharpening, FreeSync, resolution,
  refresh rate, Windows scaling, graphics memory.
- **Controller tab:** Xbox / PlayStation / Desktop / Off modes, lights, rumble strength per
  motor, gyro, stick dead zones, trigger ranges and quick trigger, button remapping with turbo.
- **Apps & power:** end the app in front, running apps, close background apps, sleep, hibernate,
  restart, shut down.
- **Performance overlay:** FPS (Intel PresentMon), power, temperature, fan, CPU, GPU, RAM, VRAM,
  battery and more, in any corner.
- **Install:** setup program or zip; both refuse devices other than the Claw A8 and check for
  MSI Center M and the AMD driver. Starts at sign-in, restarts after a crash, uses about 0.3 % of
  one CPU core while the panel is closed.
