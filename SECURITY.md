# Security policy

Claw Control runs with administrator rights (it writes firmware settings and installs low-level
keyboard and mouse hooks while its panel is open), so security problems matter.

## Reporting a vulnerability

Please **don't** open a public issue. Use GitHub's private reporting instead:
[Report a vulnerability](https://github.com/mustafameh/claw-control/security/advisories/new).
Describe the problem, how to reproduce it and what an attacker could do. You'll get an answer as
soon as possible; fixes are released as a new version and credited (unless you'd rather not be).

## Supported versions

Only the latest release gets fixes.

## What Claw Control does with its rights

- It has no network code and accepts no network input.
- It installs to `C:\Program Files\ClawControl`, which only administrators can change, and starts
  from a scheduled task that only an administrator can change.
- It listens for three named events in the user's session (`Local\ClawControl.Show`,
  `Local\ClawControl.Quit`, `Local\ClawControl.Capture`): open the panel, quit, and render the
  panel to PNG files in the user's own data folder. Nothing else is accepted from other processes.
- Its keyboard and mouse hooks only run while the panel is open, to keep input meant for the
  panel from reaching the app behind it (system keys such as volume pass through). Nothing is
  recorded; key codes are only written to the log when the detailed log is turned on.
