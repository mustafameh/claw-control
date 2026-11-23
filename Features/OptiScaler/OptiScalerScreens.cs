using System.Windows;
using System.Windows.Controls;
using ClawControl.Features.Graphics;
using ClawControl.Hardware;
using ClawControl.Features.Library;
using ClawControl.UI;

namespace ClawControl.Features.OptiScaler;

public sealed class OptiScalerScreen : UserControl, IPage
{
    readonly OptiScalerService service;
    readonly Func<RunningApp?> front;
    readonly Action<IPage> open;
    readonly Action closePanel, findGames;
    readonly StackPanel rows = new();
    readonly Dictionary<string, OptiScalerGameScreen> pages = new(StringComparer.OrdinalIgnoreCase);
    int generation;

    public string Id => "optiscaler";
    public string Title => "OptiScaler beta";
    public FrameworkElement View => this;
    public event Action? Changed;

    public OptiScalerScreen(OptiScalerService service, Func<RunningApp?> front, Action<IPage> open, Action closePanel, Action findGames)
    {
        this.service = service;
        this.front = front;
        this.open = open;
        this.closePanel = closePanel;
        this.findGames = findGames;
        Content = rows;
    }

    internal static TextBlock Note(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 15,
        Margin = new Thickness(12, 7, 12, 7)
    };

    internal static Button ButtonFor(string label, Action click) =>
        Ui.Card(Note(label), click, minHeight: 46);

    public void OpenLibraryGame(LibraryItem item) =>
        OpenGame(new GameTarget(item.Name, item.GameProgram, item.Folder, Program: item.Program, Launch: item.Launch));

    public bool CanManage(LibraryItem item) => service.Enabled ||
        service.InstallFor(new GameTarget(item.Name, item.GameProgram, item.Folder)) != null;

    void OpenGame(GameTarget target)
    {
        string key = target.Exe ?? target.Folder ?? target.Name;
        if (!pages.TryGetValue(key, out var page))
        {
            page = new OptiScalerGameScreen(service, target, open, closePanel, () => Changed?.Invoke());
            pages.Add(key, page);
        }
        page.Target = target;
        open(page);
    }

    public async void OnShown()
    {
        int current = ++generation;
        rows.Children.Clear();
        if (service.RecoveryNotice is { } notice) rows.Children.Add(Note(notice));
        if (!service.Enabled)
        {
            rows.Children.Add(Note("Optional per-game upscaling for offline games. Installing into a game downloads OptiScaler. Online games can ban accounts for modified files."));
            rows.Children.Add(ButtonFor("Enable OptiScaler beta", () => { service.SetEnabled(true); OnShown(); Changed?.Invoke(); }));
        }
        var targets = service.Enabled ? service.Games(front()) :
            service.Installs.Select(i => new GameTarget(i.Name, i.Exe, null, Launch: i.EpicLaunch)).ToList();
        var pending = new List<(GameTarget Target, ListRow Row)>();
        bool heading = false, otherGames = false;
        foreach (var target in targets)
        {
            var install = service.InstallFor(target);
            if (install != null && !heading)
            {
                rows.Children.Add(Note("Manage installed games"));
                heading = true;
            }
            if (install == null && heading && !otherGames)
            {
                rows.Children.Add(Note("Other games"));
                otherGames = true;
            }
            var row = new ListRow(null, null, target.Name);
            row.Button.Click += (_, _) => OpenGame(target);
            rows.Children.Add(ListRow.Card(row));
            row.Value.Text = install == null ? "Checking…" : service.IsDisabled(install) ? "Disabled" : install.Incomplete ? "Recover" : "Installed";
            if (install == null) pending.Add((target,row));
        }
        if (service.Enabled)
        {
            rows.Children.Add(ButtonFor("Find games on this Claw", findGames));
            rows.Children.Add(ButtonFor("Turn beta off", () => { service.SetEnabled(false); OnShown(); Changed?.Invoke(); }));
            if (service.Installs.Count > 0) rows.Children.Add(Note("Turning the beta off keeps game files in place. Installed games remain available for recovery."));
        }
        foreach (var (target,row) in pending)
        {
            var scan = await service.ScanAsync(target);
            if (current != generation) return;
            row.Value.Text = scan == null ? "Locate game" : scan.Foreign ? "Externally managed" :
                scan.Upscalers.Count == 0 ? "Unconfirmed" : string.Join(" · ",scan.Upscalers);
        }
    }
}

public sealed class OptiScalerGameScreen : UserControl, IPage
{
    readonly OptiScalerService service;
    readonly Action closePanel, changed;
    readonly Button keepChanged, locate, primary, upscaler, frameGen, advanced, expose, dx12, keepDx12, requestExit, toggle, remove, check;
    readonly TextBlock status, instruction, feedback, facts;
    readonly PickerScreen picker;
    readonly OptiScalerInstall draft = new();
    GameScan? scan;
    bool loaderObserved, hasChangedFiles;
    readonly TextBlock pickerMessage = OptiScalerScreen.Note("");
    bool running, busy, expanded, armed, removeArmed;
    string api = "";
    int generation;
    public GameTarget Target { get; set; }
    public string Id => "optiscaler-game";
    public string Title => Target.Name;
    public FrameworkElement View => this;
    OptiScalerInstall? Install => service.InstallFor(Target);
    OptiScalerInstall Choices => Install ?? draft;

    public OptiScalerGameScreen(OptiScalerService service, GameTarget target, Action<IPage> open, Action closePanel, Action changed)
    {
        this.service = service;
        Target = target;
        this.closePanel = closePanel;
        this.changed = changed;
        status = OptiScalerScreen.Note("");
        instruction = OptiScalerScreen.Note("");
        feedback = OptiScalerScreen.Note("");
        facts = OptiScalerScreen.Note("");
        picker = new PickerScreen("optiscaler-upscaler","Upscaler",
            () => [[("FSR 3.1",(object)"fsr31"),("XeSS",(object)"xess")]], () => Choices.Upscaler,
            value => Save(c => c.Upscaler = (string)value)) { Footer = pickerMessage };
        primary = Make("", Primary);
        upscaler = Make("", () => open(picker));
        frameGen = Make("Turn off legacy frame generation", () => Save(c => c.FrameGen = "off"));
        advanced = Make("Advanced settings", () => { expanded = !expanded; Refresh(); });
        expose = Make("", () => Save(c => c.Nvidia = !c.Nvidia));
        dx12 = Make("", ChangeDx12);
        keepDx12 = Make("Keep Epic launch option on removal", KeepDx12);
        requestExit = Make("Request game exit", RequestExit);
        toggle = Make("", Toggle);
        remove = Make("", Remove);
        keepChanged = Make("Finish removal and keep changed files", () => RemoveFiles(true));
        locate = Make("Locate game executable…", LocateGame);
        check = Make("Check setup again", () => { service.Forget(Target); OnShown(); });
        Content = Ui.Stack(status, feedback, instruction, primary, locate, upscaler, requestExit, toggle, remove, keepChanged, dx12, keepDx12, advanced, expose, frameGen, facts, check);
    }

    static Button Make(string label, Action action) => OptiScalerScreen.ButtonFor(label, action);
    static void Label(Button button, string text) => ((TextBlock)button.Content).Text = text;
    static void Show(UIElement element, bool show) => element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    void Message(string text) { feedback.Text = pickerMessage.Text = text; Show(feedback,text.Length > 0); }

    public async void OnShown()
    {
        int current = ++generation;
        if (busy) return;
        armed = removeArmed = false;
        Refresh();
        try
        {
            var result = await service.ScanAsync(Target);
            var exe = Install?.Exe ?? result?.Exe;
            var isRunning = exe != null && await OptiScalerService.IsRunningAsync(exe);
            var currentApi = result == null ? "" : await OptiScalerService.ApiNowAsync(result, Target.ProcessId);
            bool observed = isRunning && Install is { } i && await OptiScalerService.LoaderObservedAsync(i);
            if (current != generation) return;
            loaderObserved = observed;
            scan = result;
            running = isRunning;
            api = currentApi;
            Refresh();
        }
        catch (Exception ex) { Message(ex.Message); }
    }

    void Refresh()
    {
        var i = Install;
        bool disabled = i != null && service.IsDisabled(i);
        bool canInstall = service.Enabled && scan is { Foreign: false, Upscalers.Count: > 0 };
        bool legacy = i?.Upscaler == "fsr4";
        string selected = Choices.Upscaler == "xess" ? "XeSS" : "FSR 3.1";
        status.Text = i == null ? scan == null ? "Game location unconfirmed" : scan.Foreign ? "Managed outside Claw Control" :
            canInstall ? "Available to try · offline games only" : "Compatibility unconfirmed" :
            i.Incomplete ? "Installation interrupted · use Remove to recover" : disabled ? "Loader disabled" : loaderObserved ? $"Loader detected in game · {i.Version}" : $"Installed · OptiScaler {i.Version}";
        var input = scan?.Upscalers.FirstOrDefault(x => x == "DLSS" && Choices.Nvidia)
            ?? scan?.Upscalers.FirstOrDefault(x => x is "FSR" or "XeSS");
        instruction.Text = legacy ? "This older profile requests FSR 4. Switch to FSR 3.1 before playing." :
            disabled ? "The loader is parked. Start the game to try its own graphics settings." :
            scan == null ? "Start the game and open Claw Control over it, then check again. Installed games can be removed from here." :
            scan.Foreign ? "Use the original installer to manage this copy." :
            scan.Dx12Switch != null && api == "DX11" ? "Last launch used DirectX 11. This game needs DirectX 12 for its upscalers." :
            input == null ? "No usable in-game upscaling option was detected." :
            $"In the game’s graphics menu, select {input}. OptiScaler is configured to use {selected} on the next launch.";
        Label(primary, i == null ? armed ? "Confirm install for this offline game" : "Install OptiScaler" :
            legacy && service.Enabled ? "Switch to FSR 3.1" : running ? "Return to game" : service.CanLaunch(Target) ? "Launch game" : "Return to desktop");
        Show(locate,i == null && scan == null && service.Enabled);
        locate.IsEnabled = !busy;
        primary.IsEnabled = !busy && (i != null || canInstall && input != null);
        Label(upscaler, "Upscaler · " + (legacy ? "Legacy FSR 4" : selected));
        Show(upscaler, service.Enabled && (canInstall || i != null));
        Label(expose, "Expose DLSS options · " + (Choices.Nvidia ? "On" : "Off"));
        Show(expose, expanded && service.Enabled);
        Show(frameGen, service.Enabled && Choices.FrameGen != "off");
        Show(requestExit, running && i != null);
        Label(toggle, disabled ? "Enable loader" : "Disable loader");
        Show(toggle,i != null && !i.Incomplete && (!disabled || service.Enabled && !legacy));
        Label(remove, removeArmed ? "Press again to confirm removal" : "Remove OptiScaler");
        Show(remove,i != null);
        Show(keepChanged,i != null && hasChangedFiles);
        keepChanged.IsEnabled = !busy;
        var epic = scan != null ? OptiScalerService.EpicDx12(WithLaunch(),scan) : null;
        Show(dx12,i != null && (i.AddedDx12 || service.Enabled && epic == false));
        Label(dx12,i?.AddedDx12 == true ? "Restore Epic launch option" : "Start in DirectX 12 through Epic");
        Show(keepDx12,i?.AddedDx12 == true);
        Label(advanced,expanded ? "Hide details" : "Advanced settings and details");
        facts.Text = $"Graphics API: {(api.Length > 0 ? api : "unknown")}\nAnti-cheat: {scan?.AntiCheat ?? "none detected"}\nFrame generation: {Choices.FrameGen}\nActive upscaler: unverified";
        Show(facts,expanded);
        foreach (var b in new[]{upscaler,advanced,expose,frameGen,requestExit,toggle,remove,dx12,keepDx12,check}) b.IsEnabled = !busy;
        Show(feedback,feedback.Text.Length > 0);
    }

    GameTarget WithLaunch() => Target with { Launch = Target.Launch ?? Install?.EpicLaunch };
    async void Run(Func<Task<string?>> operation, string success, string activity = "Working…")
    {
        if (busy) return;
        busy = true;
        ++generation;
        Refresh();
        Message(activity);
        if (IsVisible) feedback.BringIntoView();
        try
        {
            string result = await operation() ?? success;
            ClawControl.Core.Log.Info($"OptiScaler · {Target.Name} · {result}");
            Message(result);
        }
        catch (Exception ex)
        {
            ClawControl.Core.Log.Error($"OptiScaler · {Target.Name}", ex);
            Message(ex.Message);
        }
        finally
        {
            busy = false;
            changed();
            OnShown();
            if (IsVisible) _ = Dispatcher.BeginInvoke(() => { if (IsVisible) feedback.BringIntoView(); },
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    void Primary()
    {
        if (Install is { } i)
        {
            if (i.Upscaler == "fsr4" && service.Enabled) Save(c => c.Upscaler = "fsr31");
            else if (!running && service.CanLaunch(Target) && !service.Launch(Target)) Message("The launcher could not start this game");
            else closePanel();
            return;
        }
        if (scan == null || !service.Enabled) return;
        if (!armed)
        {
            armed = true;
            Message($"Online games can ban accounts for modified files. Anti-cheat: {scan.AntiCheat ?? "none detected"}. Install only for offline play.");
            Refresh();
            return;
        }
        Run(() => service.InstallAsync(Target,scan,draft,new Progress<string>(message => { if (busy) Message(message); })), "Installed. Start or restart the game to apply.");
    }

    void Save(Action<OptiScalerInstall> edit)
    {
        if (busy || !service.Enabled) return;
        if (Install is not { } i) { edit(draft); Refresh(); picker.Refresh(); return; }
        var choice = new OptiScalerInstall { Upscaler = i.Upscaler, FrameGen = i.FrameGen, Nvidia = i.Nvidia,
            GameHasDlssg = i.GameHasDlssg, GameHasFsrFg = i.GameHasFsrFg };
        edit(choice);
        Run(async () => { string? error = await service.ApplyAsync(i,choice); picker.Refresh(); return error; }, "Saved for next launch.");
    }

    void LocateGame()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Select this game's Windows executable", Filter = "Windows executable (*.exe)|*.exe", CheckFileExists = true };
        if (picker.ShowDialog() != true) return;
        Target = Target with { Exe = picker.FileName };
        service.RememberExecutable(Target);
        OnShown();
    }

    void RequestExit()
    {
        if (Install is { } i) Run(async () => { await OptiScalerService.CloseGameAsync(i.Exe); return null; }, "Exit requested. Return to the game to finish saving or confirm exit.");
    }
    void Toggle()
    {
        if (Install is not { } i) return;
        bool enabling = service.IsDisabled(i);
        Run(() => service.ToggleLoaderAsync(i),
            enabling ? "Loader enabled for the next launch." : "Loader disabled. You can start the game now.",
            enabling ? "Enabling loader…" : "Disabling loader…");
    }
    void Remove()
    {
        if (Install is not { } i) return;
        if (!removeArmed) { removeArmed = true; Refresh(); return; }
        RemoveFiles(false);
    }
    void RemoveFiles(bool keep)
    {
        if (Install is not { } i) return;
        Run(async () =>
        {
            var result = await service.RemoveAsync(i,keep);
            hasChangedFiles = result?.StartsWith("Changed files preserved:") == true;
            return result;
        }, "Removed the managed OptiScaler files.", "Removing OptiScaler…");
    }
    void ChangeDx12()
    {
        if (Install is not { } i) return;
        Run(() => service.SetEpicDx12Async(WithLaunch(),scan,!i.AddedDx12),"Epic launch option saved.");
    }
    void KeepDx12()
    {
        if (Install is { } i) Run(() => service.KeepLaunchOptionAsync(i),"Epic will keep this launch option when you remove OptiScaler.");
    }
}
