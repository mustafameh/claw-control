# Troubleshooting and FAQ

- [The panel doesn't open](#the-panel-doesnt-open)
- [MSI Center M opens instead of Claw Control](#msi-center-m-opens-instead-of-claw-control)
- ["Claw Control only works on the MSI Claw A8"](#claw-control-only-works-on-the-msi-claw-a8)
- [The controller doesn't move the selection](#the-controller-doesnt-move-the-selection)
- [A graphics setting doesn't change my game](#a-graphics-setting-doesnt-change-my-game)
- [Upscaling, Frame gen or another circle is missing](#upscaling-frame-gen-or-another-circle-is-missing)
- [Hibernate is missing](#hibernate-is-missing)
- [The overlay shows "–" for FPS](#the-overlay-shows---for-fps)
- [Power limits went back to MSI's values](#power-limits-went-back-to-msis-values)
- [Give the MSI button back to MSI Center M](#give-the-msi-button-back-to-msi-center-m)
- [Quit or restart Claw Control](#quit-or-restart-claw-control)
- [Reset all settings](#reset-all-settings)
- [Reporting a bug](#reporting-a-bug)
- [FAQ](#faq)

## The panel doesn't open

1. Check that Claw Control is running: Task Manager › Details › **ClawControl.exe**. If it isn't,
   start it from the Start menu (**Claw Control**).
2. Still nothing? Open Task Scheduler › Task Scheduler Library › **Claw Control** and check that
   the task exists and its last run result. Reinstalling recreates it.
3. Look at the end of the log: `%LOCALAPPDATA%\ClawControl\log.txt` (paste that path into File
   Explorer's address bar).

## MSI Center M opens instead of Claw Control

Claw Control takes over the button only while it runs. If MSI Center M opens:

- Claw Control isn't running (see above), or
- `"TakeOverMsiButton"` is `false` in `%LOCALAPPDATA%\ClawControl\settings.json`, or
- an MSI Center M update changed how its launcher works. Please
  [report it](#reporting-a-bug) with the log.

## "Claw Control only works on the MSI Claw A8"

Claw Control checks the device's board code (MS-1T8K) before doing anything, because it writes
the Claw A8's firmware and controller settings. It refuses every other device, including other
Claw models, which the author doesn't own and hasn't tested. If you see this on a Claw A8,
[report it](#reporting-a-bug) with the board shown in the message.

## The controller doesn't move the selection

- In MSI's **Desktop mode**, Claw Control asks MSI Center M to switch the controller to gamepad
  mode while the panel is open, and switches it back when the panel closes. If MSI Center M isn't
  installed or running, that can't happen: switch to Xbox mode (Quick tab mode circle, or MSI
  Center M) and try again.
- The D-pad, left stick, A, B, LB / RB and Y work in the panel; touch always works too.

## A graphics setting doesn't change my game

Frame generation, upscaling and the FPS limit are read by AMD's driver when a game starts its
graphics. Restart the game after changing them. (AMD's own overlay can apply some of them live
through a private channel; the public driver interface Claw Control uses can't.)

Resolution changes apply immediately to the desktop; full-screen games usually keep their own
resolution setting.

## Upscaling, Frame gen or another circle is missing

Circles are hidden when the driver reports the feature as unsupported.

- **Upscaling** (Radeon Super Resolution) stays unsupported until it has been switched on once in
  **AMD Software: Adrenalin Edition**. Turn it on there, then restart Claw Control.
- Others: update the graphics driver from MSI's support page for the Claw A8.
- If the Graphics tab says "AMD's graphics settings aren't available", the AMD driver library
  wasn't found: reinstall the graphics driver.

## Hibernate is missing

Hibernation is turned off in Windows. To turn it on, run as administrator:

```powershell
powercfg /hibernate on
```

Then reopen the Apps & power menu after restarting Claw Control.

## The overlay shows "–" for FPS

FPS appears once a game (or any app drawing with the GPU) is in front and presenting frames.
PresentMon starts with the overlay, so the first number can take a second or two. If it never
appears, check the log for "PresentMon" lines.

## Power limits went back to MSI's values

MSI Center M re-applies its own values after sleep and when the charger is plugged in or out.
Claw Control writes its limits into MSI's settings too and re-applies them 4 seconds after those
events, so they should come back by themselves. If they don't, pick the mode again and
[report it](#reporting-a-bug).

## Give the MSI button back to MSI Center M

The simplest way is to [quit](#quit-or-restart-claw-control) or uninstall Claw Control: MSI gets
its button back straight away.

To keep Claw Control running without the button (mainly for testing):

1. Quit Claw Control (see below).
2. Open `%LOCALAPPDATA%\ClawControl\settings.json` in Notepad and set
   `"TakeOverMsiButton": false`.
3. Start Claw Control again from the Start menu.

The button then opens MSI Center M, and the panel only opens with
`"C:\Program Files\ClawControl\ClawControl.exe" --show` (as administrator).

## Quit or restart Claw Control

- **Quit until the next sign-in:** end **ClawControl.exe** in Task Manager › Details, or run
  `"C:\Program Files\ClawControl\ClawControl.exe" --quit` as administrator. Quitting hands the
  MSI Center M button back to MSI.
- **Start again:** Start menu › **Claw Control**.

## Reset all settings

Quit Claw Control, delete `%LOCALAPPDATA%\ClawControl\settings.json`, and start it again. Settings
stored in the device (power limits, controller settings, AMD and display settings) stay as they
are; change them in the panel.

## Reporting a bug

Open an [issue](https://github.com/mustafameh/claw-control/issues/new/choose) and include:

- what you did, what you expected and what happened;
- the Claw Control version (Settings › Apps › Installed apps) and your MSI Center M version;
- the log: `%LOCALAPPDATA%\ClawControl\log.txt`.

For problems with buttons or navigation, turn on the detailed log first: quit Claw Control, set
`"DetailedLog": true` in `settings.json`, start it again, reproduce the problem, then attach the
log. Turn it off afterwards; it logs every press.

The log contains no personal data beyond app names you ended and device names (Wi-Fi network,
Bluetooth devices) shown in the panel; look through it before attaching.

## FAQ

**Does it work on the Claw A1M, Claw 7 AI+ or Claw 8 AI+?**
No. It was made and tested on an MSI Claw A8 only; the author doesn't own the other models.
They use different firmware and controller details, so Claw Control refuses to install or run on
them rather than risk writing the wrong values. Support would need someone with that device to
help work out its details: see [CONTRIBUTING.md](../CONTRIBUTING.md).

**Does it replace MSI Center M?**
It replaces MSI Center M's quick settings and its button. MSI Center M stays installed and is
still needed (controller modes, firmware updates, Game Library, Mystic Light styles).

**Why does it need administrator rights?**
Power limits go through the firmware, which only administrators can write; taking over the MSI
button and blocking the controller from reaching the game behind the panel need it too. It gets
these rights once, at install, through a scheduled task, so there's no prompt when it starts.

**Does it collect data or connect to the internet?**
No. It has no network code. Settings and the log stay on the device.

**How much battery does it use?**
With the panel closed, about 0.3 % of one CPU core: it polls nothing and waits for the MSI
button. About 1 % with the overlay on.

**Is it safe?**
It writes the same values, through the same interfaces, that MSI Center M writes, within MSI's
own limits (for example power limits of at most 35 / 50 / 60 W). Still, it's an independent
project with no warranty: see the [license](../LICENSE).
