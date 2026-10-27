using System.Windows;
using ClawControl.Core;
using ClawControl.Features;
using ClawControl.Features.Controller;
using ClawControl.Features.Apps;
using ClawControl.Features.Graphics;
using ClawControl.Features.Network;
using ClawControl.Features.Overlay;
using ClawControl.Features.Performance;
using ClawControl.Features.Sound;
using ClawControl.Hardware;
using ClawControl.Input;
using ClawControl.Monitoring;
using ClawControl.UI;

namespace ClawControl;

/// <summary>
/// Claw Control stays running in the background from login. Everything is built at startup;
/// the MSI Center M button (or launching the exe again) only shows the already-built panel.
/// </summary>
public partial class App : Application
{
    Mutex? _instance;
    EventWaitHandle? _showSignal;
    EventWaitHandle? _quitSignal;
    UserSignal? _captureSignal;
    Settings _settings = null!;
    HardwareWorker? _hardware;
    Brightness? _brightness;
    AudioService? _audio;
    ClawPad? _pad;
    ControllerService _controller = null!;
    GraphicsService _graphics = null!;
    PerformanceService? _performance;
    OverlayWindow? _overlay;
    GamepadInput? _gamepad;
    DesktopInputBlocker? _inputBlocker;
    MsiControllerMode? _controllerMode;
    MsiButtonWatcher? _buttons;
    MsiButtonTakeover? _takeover;
    OverlayController? _perfOverlay;
    volatile string _tdpText = "";
    bool _stopped;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A second launch just asks the running copy to show its panel (or, with --quit, to exit).
        _instance = new Mutex(true, Program.InstanceMutexName, out bool firstInstance);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClawControl.Show");
        _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClawControl.Quit");
        bool quit = e.Args.Contains("--quit");
        if (!firstInstance || quit)
        {
            if (!firstInstance) (quit ? _quitSignal : _showSignal).Set();
            _stopped = true;  // nothing was started, so there is nothing to hand back
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unexpected error", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Fatal error", args.ExceptionObject as Exception);
        SessionEnding += (_, _) => Stop();

        Log.Info($"Starting Claw Control {typeof(App).Assembly.GetName().Version} on {Device.Name} ({Device.Board})");
        if (!Device.IsSupported)
        {
            // Firmware indexes, the controller chip and power limits are the Claw A8's: never
            // write them anywhere else.
            Log.Error($"Not an MSI Claw A8 ({Device.SupportedBoard}); nothing was changed");
            MessageBox.Show($"Claw Control only works on the MSI Claw A8 (board {Device.SupportedBoard}).\n\n" +
                $"This device is {(Device.Name.Length > 0 ? Device.Name : "unknown")} ({(Device.Board.Length > 0 ? Device.Board : "no board code")}), " +
                "so Claw Control closed without changing anything.", "Claw Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            _stopped = true;
            Shutdown();
            return;
        }
        QuitFlag.Clear();
        bool firstRun = !Settings.Exists;
        _settings = Settings.Load();
        Log.Detailed = _settings.DetailedLog;
        _hardware = new HardwareWorker();
        _performance = new PerformanceService(_hardware, new MsiAcpi(), _settings);

        _perfOverlay = new OverlayController(_settings, () => _tdpText);
        _brightness = new Brightness();
        _audio = new AudioService();
        var media = new MediaService();
        _pad = new ClawPad();
        _controller = new ControllerService(_pad, _settings);
        var performancePage = new PerformancePage(_performance, _brightness, _audio, media);
        var soundPage = new SoundPage(_audio);
        performancePage.OpenSoundRequested += () => _overlay?.OpenSubPage(soundPage);
        var net = new Connectivity();
        Action closePanel = () => _overlay?.HidePanel();
        var wifiScreen = new WifiScreen(net, closePanel);
        var bluetoothScreen = new BluetoothScreen(net, closePanel);
        performancePage.SetTopRow(new QuickRow(_controller, net, wifiScreen, bluetoothScreen, page => _overlay?.OpenSubPage(page)));
        var appsMenu = new AppsPowerScreen();
        performancePage.AddScreens(soundPage, wifiScreen, bluetoothScreen, appsMenu);
        performancePage.AddSection(new OverlaySection(_settings, _perfOverlay));
        _graphics = new GraphicsService(_settings);
        var pages = new IPage[]
        {
            performancePage,
            new GraphicsPage(_graphics, page => _overlay?.OpenSubPage(page)),
            new ControllerPage(_controller, page => _overlay?.OpenSubPage(page)),
        };
        _overlay = new OverlayWindow(pages, _settings, net) { SaveSnapshots = e.Args.Contains("--snapshots"), AppsMenu = appsMenu, Switcher = new AppSwitcher() };
        _performance.Changed += UpdateStatus;
        UpdateStatus();
        _overlay.Prewarm();

        _gamepad = new GamepadInput();
        _gamepad.Command += command => Dispatcher.BeginInvoke(() => _overlay.Handle(command, InputSource.Gamepad));
        _inputBlocker = new DesktopInputBlocker();
        _inputBlocker.Command += (command, source) => Dispatcher.BeginInvoke(() => _overlay.Handle(command, source));
        // While the panel is open the controller is a gamepad, whatever MSI mode it was in, so
        // every button works the same. The blocker covers the moment before the switch lands.
        _controllerMode = new MsiControllerMode();
        _inputBlocker.DesktopInputSeen += _controllerMode.Reassert;
        _overlay.IsVisibleChanged += (_, _) =>
        {
            bool open = _overlay.IsVisible;
            _gamepad.Enabled = open;
            if (open)
            {
                _inputBlocker.Open();
                _controller.PanelOpened();  // turns an "off" controller on while the panel is open
                _controllerMode.Enter();
            }
            else
            {
                _inputBlocker.Close();
                // B closes the panel while it's still held: hand the controller back only once
                // it's let go, or the game behind would get that B too.
                var letGo = _inputBlocker.WhenReleasedAsync(TimeSpan.FromSeconds(3));
                _controller.PanelClosed(letGo);  // the real button mapping back, then a mode chosen on the Controller tab
                letGo.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
                {
                    if (!_overlay.IsVisible) _controllerMode.Leave();
                }));
            }
        };

        StartButtonWatcher();
        SetButtonTakeover(_settings.TakeOverMsiButton);
        ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(() => _overlay.ShowPanel()), null, Timeout.Infinite, executeOnlyOnce: false);
        ThreadPool.RegisterWaitForSingleObject(_quitSignal,
            (_, _) => Dispatcher.BeginInvoke(Quit), null, Timeout.Infinite, executeOnlyOnce: true);
        // Debug aid: Local\ClawControl.Capture (settable without admin) renders every page and
        // screen to captures\*.png, since the elevated panel can't be screenshotted from outside.
        try
        {
            _captureSignal = new UserSignal(@"Local\ClawControl.Capture");
            ThreadPool.RegisterWaitForSingleObject(_captureSignal,
                (_, _) => Dispatcher.BeginInvoke(() => _overlay.CaptureAll(Path.Combine(Log.Dir, "captures"))),
                null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            Log.Error("Could not set up the capture signal", ex);
        }

        _ = _performance.InitializeAsync(firstRun);
        // One-time connection to Windows' media sessions, off the UI thread and after the panel
        // is ready, so the first opening already knows what's playing. Nothing is polled.
        Task.Run(media.InitializeAsync);
        // Read the controller chip's settings once, a few seconds after startup and in the
        // background, so the Controller tab and the Quick tab's mode row are ready when opened.
        Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ => _controller.RefreshAsync());
        // Same for AMD's graphics settings and the screen modes (Graphics tab).
        Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(_ => _graphics.RefreshAsync());
        // What starting cost (precompiled code should leave little to compile at run time).
        Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => Log.Info(
            $"Startup: {System.Runtime.JitInfo.GetCompiledMethodCount():N0} methods compiled at run time " +
            $"({System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds:N0} ms), " +
            $"{System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds:N1} s CPU in the first 20 s"));
        _perfOverlay.Apply();  // starts nothing unless the overlay was left on
        if (e.Args.Contains("--show")) _overlay.ShowPanel();
    }

    void StartButtonWatcher()
    {
        try
        {
            _buttons = new MsiButtonWatcher();
            _buttons.Pressed += code =>
            {
                if (code == MsiButtonWatcher.CenterButton && _settings.TakeOverMsiButton)
                {
                    long pressed = System.Diagnostics.Stopwatch.GetTimestamp();
                    Dispatcher.BeginInvoke(() => { if (_overlay != null) { _overlay.PressedAt = pressed; _overlay.Toggle(); } });
                }
            };
            _buttons.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Could not listen for MSI button events", ex);
        }
    }

    void SetButtonTakeover(bool on)
    {
        if (on && _takeover == null)
        {
            _takeover = new MsiButtonTakeover();
            Log.Info("MSI Center M button now opens Claw Control");
        }
        else if (!on && _takeover != null)
        {
            _takeover.Dispose();
            _takeover = null;
            Log.Info("MSI Center M button given back to MSI");
        }
    }

    /// <summary>Keeps the overlay's TDP number current (the panel header no longer shows the mode).</summary>
    void UpdateStatus()
    {
        if (_performance == null) return;
        _tdpText = _performance.Target.ToString();
    }

    void Quit()
    {
        Stop();
        Shutdown();
    }

    /// <summary>Hands everything back: MSI gets its button back, settings are flushed.</summary>
    void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        Log.Info("Stopping");
        _takeover?.Dispose();
        _buttons?.Dispose();
        _inputBlocker?.Close();
        // Quitting can happen with the panel open: put the controller back first.
        _controller?.EndPanelMapping(TimeSpan.FromSeconds(2));
        _controllerMode?.Leave();
        _controllerMode?.WaitForPending(TimeSpan.FromSeconds(3));
        _perfOverlay?.Shutdown(TimeSpan.FromSeconds(3));
        _gamepad?.Dispose();
        _settings?.SaveNow();
        _hardware?.Dispose();
        _brightness?.Dispose();
        _audio?.Dispose();
        _pad?.Dispose();
        _graphics?.Dispose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Stop();
        base.OnExit(e);
    }
}
