# Contributing to Claw Control

Thanks for helping! Claw Control is a small project made by one person with one MSI Claw A8, so
bug reports from real use are the most valuable contribution.

## Bug reports and ideas

- **Bugs:** open an [issue](https://github.com/mustafameh/claw-control/issues/new/choose) with the
  bug template. Attach `%LOCALAPPDATA%\ClawControl\log.txt` (see
  [Troubleshooting › Reporting a bug](docs/TROUBLESHOOTING.md#reporting-a-bug)).
- **Ideas:** use the feature request template. Say what you'd use it for; Claw Control stays
  small and fast on purpose, so not every MSI Center M feature belongs in it.

## Other Claw models

Claw Control only supports the **MSI Claw A8** (board MS-1T8K), because that's the only device
it has been built and tested on: the author doesn't own the Claw A1M, Claw 7 AI+ or Claw 8 AI+.
They use different firmware and controller details, and wrong values written to firmware or the
controller could misbehave, so the app refuses to run on them.

If you own one of them and want to help, open an issue first. Supporting a model means checking,
on that device, everything in [docs/HARDWARE.md](docs/HARDWARE.md): the firmware's power-limit
indexes and limits, the button event codes, the controller chip's IDs and settings layout, and
the MSI Center M behaviour Claw Control relies on, then adding the board to `Hardware/Device.cs`
with the right values. Reading values is harmless; writing must wait until each one is confirmed.

## Code

- Read [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) first: build, install, architecture, the design
  rules and the lessons learned. Hardware details are in [docs/HARDWARE.md](docs/HARDWARE.md).
- Keep it fast and light: nothing may run or poll while the panel is closed unless the user
  turned it on (the overlay), and the panel must never wait on hardware (use `HardwareWorker`).
- Follow the design rules: labels and values only, the existing building blocks in
  `UI/Theme.xaml`, everything reachable with the D-pad.
- Match the surrounding code: comment density, naming, idioms. Comments say why, not what.
- Test on a real Claw A8, with the panel opened by the MSI button, in both Xbox and Desktop
  controller modes. Check the log for errors.
- One feature or fix per pull request, with a short description of what you tested.

## License

By contributing you agree that your contribution is licensed under the [MIT license](LICENSE).
