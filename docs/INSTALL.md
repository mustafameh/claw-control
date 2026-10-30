# Installing Claw Control

> [!IMPORTANT]
> Claw Control is made **only for the MSI Claw A8** (board MS-1T8K). It has been built and
> tested on one Claw A8. Other Claw models (Claw A1M, Claw 7 AI+, Claw 8 AI+) were not available
> to the author, so it has not been tested on them. Both the setup and the app check the device
> and refuse anything that isn't a Claw A8, before changing anything.

## Before you install

| You need | Why | How to check / get it |
|---|---|---|
| **MSI Claw A8** | Claw Control writes the Claw A8's firmware and controller settings. | Settings › System › About shows "Claw A8". |
| **Windows 11** (or Windows 10 version 2004 or newer), 64-bit | The Claw ships with Windows 11. | Settings › System › About. |
| **MSI Center M** | Claw Control runs alongside it: it switches the controller to gamepad mode through MSI Center M while the panel is open, and takes over its button. | It comes preinstalled. If it's gone, install it from MSI's support page for the Claw A8 or the Microsoft Store. |
| **AMD graphics driver** | The Graphics tab's AMD settings (FPS limit, frame generation, upscaling, sharpening, FreeSync, graphics memory) go through it. | Preinstalled. Updates: MSI's support page for the Claw A8 or AMD. |
| About 200 MB of disk space | The app includes its own .NET runtime, so nothing else needs installing. | |

Optional:

- **Upscaling (Radeon Super Resolution)** only appears in Claw Control after it has been switched
  on **once** in AMD Software: Adrenalin Edition (a driver limitation). Open AMD Software, turn on
  Radeon Super Resolution, then restart Claw Control.
- **Hibernate** only appears in the Apps & power menu when hibernation is enabled in Windows.

The setup checks the first four for you: it refuses to install on another device, and warns
(with the choice to stop) if MSI Center M or the AMD driver is missing.

## Option 1: Setup program (recommended)

1. Download `ClawControl-Setup-<version>.exe` from the
   [Releases page](https://github.com/mustafameh/claw-control/releases).
2. Run it. Windows may show **"Windows protected your PC"**: the setup isn't code-signed (signing
   certificates cost money every year). Click **More info › Run anyway**. You can check the
   download against `SHA256SUMS.txt` on the release page first (see below).
3. Allow the admin prompt. Read the information page (what Claw Control changes), accept the MIT
   license, and click **Install**.
4. Claw Control starts right away. Press the **MSI Center M button** to open the panel.

It installs to `C:\Program Files\ClawControl`, adds **Claw Control** to the Start menu (starts it
again if you ever quit it) and to **Settings › Apps › Installed apps**.

## Option 2: Zip package

For people who prefer no installer:

1. Download `ClawControl-<version>-win-x64.zip` from the
   [Releases page](https://github.com/mustafameh/claw-control/releases) and unzip it.
2. Double-click **Install.cmd** and allow the admin prompt. A window shows the same checks as the
   setup, then installs to `C:\Program Files\ClawControl` and starts Claw Control.

The zip install doesn't appear in Installed apps; uninstall it with **Uninstall Claw Control.cmd**
in `C:\Program Files\ClawControl`.

## Checking the download (optional)

Each release lists SHA256 checksums in `SHA256SUMS.txt`. In PowerShell:

```powershell
Get-FileHash .\ClawControl-Setup-0.1.1.exe -Algorithm SHA256
```

The hash must match the line for that file.

## What gets installed and changed

- `C:\Program Files\ClawControl`: the app, its .NET runtime and Intel PresentMon (for FPS).
- A scheduled task **Claw Control** (Task Scheduler › Task Scheduler Library). It starts Claw
  Control at sign-in with administrator rights, so the panel works without an admin prompt every
  time, and restarts it 10 seconds after a crash. Nothing else runs in the background.
- `%LOCALAPPDATA%\ClawControl`: `settings.json` and `log.txt` (rotated at 1 MB).
- **The MSI Center M button.** While Claw Control runs, MSI's launcher is paused (not removed or
  changed), so the button opens Claw Control instead of MSI Center M. Quitting or uninstalling
  hands it back. MSI Center M itself still opens from the Start menu.
- **What you change in the panel** is written where MSI Center M would write it: power limits
  (firmware, and MSI's own registry key so MSI keeps them), controller settings (the controller's
  memory), AMD graphics settings, display and sound settings.

Claw Control connects to one place only: GitHub, once a day when you open the panel, to see
whether a new version is out (it sends nothing about you or the device). When one is, a green
sign shows next to the power sign in the panel's header; **Update now** downloads the setup
from the GitHub release, checks it against the release's `SHA256SUMS.txt` and installs it.
Nothing installs unless you press it.

## Updating

Install the new version over the old one (setup or zip). Your settings are kept.

## Uninstalling

- **Setup install:** Settings › Apps › Installed apps › Claw Control › Uninstall. It asks whether
  to delete your settings and log too.
- **Zip install:** double-click `C:\Program Files\ClawControl\Uninstall Claw Control.cmd`.

Either way Claw Control quits first, the MSI Center M button goes back to MSI, and the scheduled
task and program folder are removed.

What stays as you left it: the power mode and limits (until you pick another mode in MSI Center
M), the controller's settings (mode, lights, rumble, gyro, sticks, triggers, remapped buttons:
they live in the controller), AMD graphics settings, resolution, refresh rate, scaling and sound
devices. Change them back in MSI Center M, AMD Software or Windows Settings if you want.

## Quitting without uninstalling

Claw Control has no quit button (it's meant to be always ready). To stop it until the next
sign-in, end **ClawControl.exe** in Task Manager › Details, or run as administrator:

```powershell
& "C:\Program Files\ClawControl\ClawControl.exe" --quit
```

Start it again from the Start menu (**Claw Control**). To leave the MSI Center M button to MSI
permanently but keep Claw Control, see [Troubleshooting](TROUBLESHOOTING.md#give-the-msi-button-back-to-msi-center-m).

## Install problems

| Message | What to do |
|---|---|
| "Claw Control is made only for the MSI Claw A8" | It's a different device; it can't be installed there (see the note at the top). |
| "MSI Center M isn't installed" | Install MSI Center M first (MSI's support page for the Claw A8 or the Microsoft Store), then run the setup again. |
| "The AMD graphics driver wasn't found" | Install the graphics driver from MSI's support page for the Claw A8. You can install anyway; only the Graphics tab's AMD settings won't work. |
| "Windows protected your PC" | More info › Run anyway (the setup isn't code-signed). |
| Antivirus warning | Claw Control runs as administrator and watches the keyboard and mouse while the panel is open (so the controller can't reach the game behind it). Some antivirus tools flag that pattern. The full source code is in this repository. |

More in [Troubleshooting](TROUBLESHOOTING.md).
