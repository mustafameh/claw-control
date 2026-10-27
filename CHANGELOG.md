# Changelog

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
