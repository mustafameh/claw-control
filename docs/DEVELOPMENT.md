# Developing Claw Control

A fast, controller-first replacement for MSI Center M's quick settings on the **MSI Claw A8**
(MSI model code `1T8K`, AMD Ryzen Z2 Extreme). C# / WPF / .NET 10. It runs elevated and stays
resident from login, so the panel opens instantly. How the hardware is driven:
[HARDWARE.md](HARDWARE.md).

## Build, install, run

- Needs the .NET 10 SDK. Build: `dotnet build ClawControl.csproj -c Release` (self-contained,
  win-x64; users need no .NET installed).
- Target is `net10.0-windows10.0.19041.0` (WinRT media sessions for now playing; adds ~25 MB of
  `Microsoft.Windows.SDK.NET.dll`). Sound uses NAudio.Wasapi (Core Audio); FPS uses Intel
  PresentMon 2.6.0 (`Tools/PresentMon.exe`, runs only while the overlay shows FPS).
- Install a development build:
  `installer\install.ps1 -Source bin\Release\net10.0-windows10.0.19041.0\win-x64 -Yes` (asks for
  admin rights once). It asks a running copy to quit first (that hands the MSI button back
  cleanly), copies to `C:\Program Files\ClawControl`, (re)registers the "Claw Control" scheduled
  task and starts it. `-Yes` skips the questions; a device other than the Claw A8 is still
  refused.
- The task (`installer\task.ps1`, shared by every install route) runs at the Highest run level
  (admin at every login without a prompt), priority 3, with two triggers: logon, and 10 s after a
  crash (Windows' "Application Error" event 1000 naming `ClawControl.exe`). Task Scheduler's
  restart-on-failure only covers a start that fails.
- Release files: `build-release.ps1` (needs Inno Setup 6) builds `dist\ClawControl-Setup-<version>.exe`
  (`installer\ClawControl.iss`), `dist\ClawControl-<version>-win-x64.zip` (Install.cmd /
  Uninstall.cmd + `scripts\` + `app\`) and `dist\SHA256SUMS.txt`. The version comes from
  `ClawControl.csproj`. Both installers check the board (MS-1T8K) and refuse anything else, and
  warn when MSI Center M or the AMD driver is missing.
- Device check: `Hardware/Device` reads the BIOS data from the registry (instant). On anything but
  board MS-1T8K the app shows a message and exits before touching any hardware.
- Start Claw Control through the task, not as a child of another program: a process started from
  inside a packaged (MSIX) app joins that package, gets its AppData redirected and closes with it.
- Data: `%LOCALAPPDATA%\ClawControl\log.txt` (rotates at 1 MB) and `settings.json`.
  `CLAWCONTROL_DATA_DIR` overrides the folder (tests). The log keeps one-off events and errors;
  every press, key, command and page switch is only logged with `"DetailedLog": true` in
  settings.json (`Log.Detail`, read at startup), for bug reports. Each start logs its own cost
  ("Startup: … methods compiled … s CPU in the first 20 s").
- Command line: `--show` (open the panel), `--quit`, `--background` (what the task uses: exits at
  once if a copy runs or the user quit on purpose this boot), `--snapshots` (saves `snapshot.png`
  each time the panel opens).
- Named signals, settable without admin from the same user:
  - `Local\ClawControl.Quit`: quit (then `Start-ScheduledTask 'Claw Control'` restarts it).
  - `Local\ClawControl.Capture`: renders every page and every screen in `IPage.Screens` (whole,
    unscrolled) to `%LOCALAPPDATA%\ClawControl\captures\NN Page - Screen.png` while the panel
    stays closed. The elevated panel can't be screenshotted from a normal process, so this is the
    way to check the look (and to make README screenshots). Skipped while the panel is open;
    values loaded in the background may be missing on the first capture after a start.
- Input injected with SendInput from a non-admin process is silently blocked while the elevated
  panel is in front (UIPI): keep the panel closed during such tests.
- `Core/Warmup` compiles the panel's code (UI, Features, Input) once on a low-priority thread
  10 s after start, so the first use of something (the switcher growing) doesn't drop frames
  while .NET compiles it.
- ReadyToRun (precompiled) builds were measured and bring nothing here (same compile time and
  startup CPU, +31 MB). Measured cost: panel closed ≈ 0.3 % of one core (≈ 1 % with the overlay
  on, plus PresentMon 0.1 %), panel open ≈ 15 %, ~240 MB working set, ~3 s CPU at startup.

## Repository layout

| Path | What |
|---|---|
| `App.xaml.cs`, `Program.cs` | Startup: single instance, device check, building every service, signals |
| `Core/` | Logging, settings, background workers (`HardwareWorker`, `LatestOnly`) |
| `Hardware/` | Everything that talks to the device or Windows (firmware, controller chip, AMD, audio, display, radios…) |
| `Features/` | The tabs and screens (Quick = `Performance/`, `Graphics/`, `Controller/`, `Apps/`, `Network/`, `Sound/`, `Overlay/`) |
| `Input/` | Gamepad polling, the input blocker, key translation |
| `Monitoring/` | Performance overlay: sensors, PresentMon, the overlay window |
| `UI/` | The panel window, theme and building blocks, glass, selection drop, D-pad navigation |
| `Tools/PresentMon.exe` | Intel PresentMon 2.6.0 (MIT), shipped with the app |
| `installer/` | Setup script (Inno Setup), zip install / uninstall scripts, the scheduled task |
| `docs/` | Install guide, user guide, troubleshooting, this file, hardware notes, images |

## Architecture

- `App` builds everything at startup; the panel (`UI/OverlayWindow`) is pre-rendered and only
  shown and hidden.
- `Core/HardwareWorker`: one background thread per kind of slow work (firmware, brightness, audio,
  display each have their own), so the UI never waits and one never queues behind another.
  `Core/LatestOnly<T>`: slider → hardware sender that skips values replaced before they went out.
- Pages (`Features/*`, `IPage`): switched with LB / RB, tapping the tab pill, or D-pad up to the
  tab row then left / right. `IPage` has `OnShown` / `OnHidden` (start / stop anything that
  listens) and `DefaultFocus` (selected whenever the panel opens on that page).
  - `Performance/PerformancePage` is the **Quick** tab (id "performance"): now-playing strip,
    mode / Wi-Fi / Bluetooth row, volume (A → Sound screen), brightness, mode circles + caption,
    Custom power sliders, then the overlay tile (`Overlay/OverlaySection`).
  - Graphics (`Features/Graphics`) and Controller (`Features/Controller`) tabs.
  - Sub screens (`OverlayWindow.OpenSubPage`, e.g. `Sound/SoundPage`) show over the current page;
    the tab pill becomes "‹ Title". Screens can open screens (a stack): B goes back one level, to
    the row that opened it; LB / RB / Y / closing the panel leave all levels.
  - Power (`Features/Apps/AppsPowerScreen`): opened by the header chip or **Y**.
  - App switcher (`Features/Apps/AppSwitcher`, 2026-10-27): a Canvas in the panel window's left
    column (`Root`), its tile level with the card's first row (`FirstRow`). The panel's one
    `GlassBackdrop` covers the whole window and draws the card and the switcher's pieces (the
    arc is a row of overlapping blurred circles). A separate never-activating window was tried
    first: WPF touch didn't reach it. It reaches 40 units into the card's shadow room (`SwitcherOverlap`).
    The panel's glass drop (`SelectionLens`, rooted at `Root`) follows its selection (`SelectedView`).
    It's a mini version (0.6, scaled toward the card's edge) until it has the D-pad, then springs to
    full size (`_grow`; glass pieces carry an opacity, `SelectionLens.Nudge` keeps the drop on it);
    the live preview shows only at full size. D-pad left from the page's left edge hands it the
    D-pad (`OverlayWindow.Handle` passes commands on until it lets go). Live previews are DWM
    thumbnails (`UI/WindowPreview`) drawn into the glass window under a hole in the frame. The
    glass gets the card and the switcher as two layers (`GlassBackdrop.Layer`): the switcher's
    pieces in its own units with one transform, so growing changes one transform, and only
    pieces that changed are sent. While hidden the glass is left in the closed look
    (`GlassBackdrop.Update`): a shown window draws its old pieces for a frame first. The arc
    keeps its icon views when a fresh list comes in and is re-placed only when it turns. X =
    `NavCommand.Action` (numpad 1 in the panel mapping; only the switcher uses it, so a stray X on a page can't end an app). Store apps:
    their windows belong to the frame host (`ApplicationFrameWindow`); `RunningApps.Identify` finds
    the app's own process through the inner `CoreWindow` and its name / tile icon through the
    window's AppUserModelID (`shell:AppsFolder\<id>`); apps are keyed by `RunningApp.Key`. Apps come in Alt + Tab order
    (`RunningApps.List`, one process snapshot per list), icons from the shell at 96 px; names and
    icons are learned once per app and kept. The app in front is known at once on opening
    (`RunningApps.Recall`: window calls only, plus what was learned), `Describe` fills in the rest.
  - Quick tab tiles (`Performance/QuickTiles`, 2026-10-30): each part of the tab is a tile in a
    saved order (`QuickOrder`, `QuickHidden`). Edit (bottom of the tab) puts a clear block
    (`Handle`) over each tile and leaves the tile's own controls out of navigation
    (`PanelInput.Skip`); A picks a tile up, up / down move it (`PanelInput.Steer`), X hides it
    (`PanelInput.Action`, the footer shows `ActionHint`), B / Done leaves (`IPage.HandleBack`,
    `IPage.BackHint`).
  - Capture (`Features/Capture`, `Hardware/ScreenCapture`): Screenshot / Record tiles and the
    Capture screen. The panel closes first (`OverlayWindow.HideAndWaitAsync`); while recording,
    the panel and glass are excluded from capture (`SetWindowDisplayAffinity`).
- Input: the panel mapping (`ControllerService.PanelMapping`, docs/HARDWARE.md "Panel mapping")
  has the chip send keys instead of buttons while the panel is open, so games don't see the
  presses; `DesktopInputBlocker` (low-level hooks, only while open) takes those keys and MSI's
  Desktop-mode mouse / keyboard before any app and turns them into commands; `GamepadInput`
  (XInput, polls only while open) covers the moment before the mapping lands. D-pad only: the
  sticks still reach the game. `UI/SpatialNavigator` (D-pad focus by on-screen position).
  `OverlayWindow.Handle` drops the same command arriving from two sources within 150 ms. Left / right on a focused slider move
  it; `UI/PanelInput.Accept` gives a slider an A action, `PanelInput.Hint` sets the footer's A
  label, `PanelInput.Steer` lets a control decide a D-pad direction first (paged rows).
- To give the MSI button back: `"TakeOverMsiButton": false` in settings.json, then restart.
  `"HideControllerFromGames": false` turns the panel mapping off (games see the panel's presses).

## Adding a feature

1. Hardware access in `Hardware/`, on its own `HardwareWorker`; sliders go through `LatestOnly`.
2. UI on the Quick tab (quick controls near the top) or a sub screen via `OpenSubPage`, built from
   the building blocks below, following the design rules.
3. Read state in `OnShown` (show cached values first, refresh in the background), stop listeners
   in `OnHidden`. Nothing runs or polls while the panel is closed unless the user turned it on.
4. Check D-pad paths: every control reachable, left / right on sliders change values, A works.
5. Build, install with `installer\install.ps1`, check the log, and the look with the capture
   signal.

## Design rules

- Floating glass card, top-right with a 12-unit gap, as tall as its content (scrolls if taller),
  26-unit corners.
- Sizes are **design units** (`UI/ScreenFit`): 1 unit = screen height / 800 physical pixels
  (1.5 px on the Claw's 1920 × 1200 at 150 %, what it was designed at). The card gets a
  `LayoutTransform` that cancels Windows scaling, so the panel covers the same share of the
  screen at any resolution and scaling, pinned to the right edge. Same for the overlay numbers
  (`OverlayHud.Place`). Don't use `SystemParameters.WorkArea` / `PrimaryScreenWidth`: they convert
  with the scaling from app start, so after a resolution change (which often changes scaling
  too) the panel lands in the wrong place.
- On / off language as in iOS Control Center: levels (volume, brightness, power limits, app
  volume) are white fill bars; choices (chips, position spot, device) light up white with dark
  text; switches are green (`SwitchOnBrush`); each mode is a circle in its own colour (set as the
  button's `Background`). D-pad focus is the glass drop, never a ring. No blue "on".
- Labels and values only: no text the highlight already says ("Shown", "Press A", "Applied",
  section titles), no captions, hints or explanations. Errors are the only status text. A state
  that needs the user (e.g. a restart pending) is an action button, not a note.
- Building blocks in `UI/Theme.xaml`: `FillSlider` (implicit style; `Glyph` / `Icon` / `Label` /
  `ValueText` / `ShowMore`), `ModeButton`, `SwitchButton`, `ChipButton`, `SpotButton`,
  `IconButton`, `TabButton`, `CardButton`, `Divider`, and for grouped cards (iOS Settings style)
  `RowButton` / `RowSwitchButton` with `Ui.IconSquare` icons, `ListRow`, `PickerScreen`.
- Icons are Segoe Fluent Icons glyphs, written as `"\uE8A3"` escapes in C#. When Fluent has no
  fitting glyph, add a 24 × 24 line geometry to Theme.xaml and use `Ui.LineIcon` (circles, rows)
  or `FillSlider.LineIcon` (bars). Brand logos (Xbox, PlayStation) are `Geometry` resources
  (Simple Icons paths) drawn with the `Logo` style, which takes the inherited text colour.
- `FillSlider` text is drawn twice (light + dark) and the dark copy is clipped to the fill, so
  text crossing the fill edge stays readable.

### Liquid Glass touches

- Panel rim: `GlassRimBrush` one device pixel wide (`1 / unit` DIP), lit along the top and the
  top-left corner, faint sides, softer glint bottom-right; just inside it a faint 2-unit band
  (`PanelBandBrush`) and a soft glow fading inward (nested borders), plus a top sheen
  (`PanelSheenBrush`): the glass looks thick. No dark outline (it showed as a black ring over
  bright games).
- The card floats: a soft blurred shadow below it and a faint halo along its edge
  (`ShadowLayer`, `BlurEffect` + `BitmapCache`, so it's blurred once, not per frame). It's
  clipped to the outside of the card, because the card is see-through. The window is bigger
  than the card by the card's `Margin` to make room for it (`PlaceInCorner`); the glass, the
  captures and taps follow the card, and a tap on the shadow counts as outside.
- `FillSlider` (2026-10-27): a split bar. White fill, a sliver of glass as the handle (6 units,
  `HandleFillBrush` / `HandleRimBrush`) and the empty part, with 3-unit gaps, all clipped to
  one rounded shape; near the ends the handle shortens to follow the curve. The fill springs
  to new values, the value rolls in from the direction it moved, D-pad past either end
  stretches the bar like a rubber band (`Nudge`), and `TurnGlyph` turns the brightness sun.
  The track is invisible and only turns taps / drags into values. Runs per frame only while
  moving.
- Selection = `UI/SelectionLens`: a glass drop (`LensRimBrush` rim, `LensFillBrush` fill) on
  `LensLayer` above everything. Spring physics (stiffness 300, damping 24: one small overshoot),
  stretches along its direction by speed, A swells it (`Pulse`). Runs on
  `CompositionTarget.Rendering` only while moving. The focused control's `ContentPresenter`
  grows (circles 1.12, wide controls a few px) with a BackEase: re-rendered, so text stays sharp
  (magnifying the drop's middle makes text blurry). `FocusBrush` is Transparent, so templates'
  focus borders draw nothing.
- Tab pill slides and stretches (`MoveTabPill`, also under "‹ Title"); mode colours fade (`Lit`
  layer in `ModeButton`); switches fade green and the knob springs.
- Screens that rebuild their list (running apps, Wi-Fi, Bluetooth, sound devices, pickers)
  remove the selected row. The drop then fades and raises `Lost`; the panel selects the control
  nearest that spot (`SpatialNavigator.Nearest`). The drop follows the window's logical focus,
  not only keyboard focus, because the panel isn't always the active window.
- Not possible in WPF: bending the game behind the panel's rim (WPF can't see the DWM backdrop);
  bending content under the drop's rim would need a compiled pixel shader.

### Glass: what works and what doesn't

- DWM system backdrops (`DWMWA_SYSTEMBACKDROP_TYPE` acrylic) need a framed window (WindowStyle
  None gives plain grey) and then show a close (X) button unless `WS_SYSMENU` is removed. Their
  corners are fixed at ~8 px, and **they ignore `SetWindowRgn`**: a rounded region gives a square
  blur box around rounded content. `SetWindowCompositionAttribute` acrylic ignores regions too.
- What works (`UI/GlassBackdrop`): a separate click-through popup with
  `WS_EX_NOREDIRECTIONBITMAP` (clear except its visuals) + `DWMWA_USE_HOSTBACKDROPBRUSH`, a
  `Windows.UI.Composition` `DesktopWindowTarget` with a `HostBackdropBrush` sprite clipped by a
  rounded-rectangle geometry. The panel is `AllowsTransparency` (draws its own rounded card and
  tint) and is **owned** by the glass window so it always stays above it. Composition needs a
  DispatcherQueue on the UI thread (`CreateDispatcherQueueController`).
- The glass follows the card's WPF bounds (layout size × DPI), not `GetWindowRect`: during
  `SizeChanged` the window itself still has the old size.
- For window / glass experiments, a throwaway WPF app that opens a striped window, puts the test
  window on top and takes its own `CopyFromScreen` shot is quicker than trying in the real app.

## Lessons

- WPF slider range setup fires `ValueChanged`; guard it, and never write firmware before startup
  has read it.
- Windows silently removes a low-level hook that ever answers slowly. Pre-JIT the hook paths, run
  the hook thread at high priority, probe it every 2 s (marked F24 + 1 px mouse move;
  zero-distance moves never reach hooks) and reinstall if a probe goes unseen.
- Hook coordinates are physical pixels: use `GetPhysicalCursorPos`.
- A key a low-level hook swallows never reaches raw input (`WM_INPUT`) either; mouse movement
  does (measured with a background raw-input reader). Keep the controller's input to keys.
- The keyboard blocker must let system keys through: the Claw's volume buttons are VK 0xAD-0xAF
  (media keys 0xB0-0xB3).
- Ignore OS key auto-repeat (held triggers flipped pages 30×/s); a held direction repeats on the
  hook thread's timer at the gamepad's pace (350 ms, then every 80 ms).
- D-pad navigation (`SpatialNavigator`) scores edge-to-edge distance + 2 × sideways gap. A wide
  control directly above counts as straight ahead, but a control that overlaps you along the
  direction of travel is measured from its centre.
- Keep focusable controls in a row from overlapping each other (tap areas included): overlapping
  neighbours make left / right skip one.
- Hidden controls are skipped by navigation; a remembered focus target that is now hidden falls
  back to the first control.
- WPF `Style BasedOn` needs the base style defined earlier in the XAML file.
- Paged rows: the overlay's numbers are 2 pages of 3 × 2 chips; right past the last column flips
  to page 2, left from its first column flips back; dots show / tap the page.
- Touch: the panel's ScrollViewer (PanningMode) takes every finger slide, so `FillSlider`
  captures the touch itself (Preview touch events) and follows the finger.
- Saying whose the front window is (`RunningApps.Describe`: shell id, process, memory) took ~0.8 s
  with a game in front (measured 2026-10-27): the panel shows at once and fills the switcher's
  tile from the background (`DescribeFront`), reusing the last answer for the same window.
- A see-through (layered) window shows its last picture again for a moment when it's shown next
  time. `HidePanel` moves the panel off-screen at once, lets it draw its reset look (mini app
  switcher, no drop) for a few frames, then hides it (`HideAfterDrawing`).
- Setting a new `LayoutTransform` (even an equal one) re-lays out the whole panel: compare first.
- `Process.ProcessName` and `GetProcessesByName` each take a new snapshot of every process;
  `Process.MainModule` lists the process's modules (slow for games). Use one
  `Process.GetProcesses()` and `QueryFullProcessImageName`.
- Never set `Foreground = null` to mean "inherit": it's a local value and the text draws
  invisibly. Leave the property unset.
- During captures the panel is hidden, so `IsVisible` is false everywhere: capture code checks
  `Visibility`.
- PowerShell `FindWindow($null, ...)` passes an empty string, not null: use `[NullString]::Value`.
