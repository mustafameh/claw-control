# Changelog

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
