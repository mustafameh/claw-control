using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClawControl.Core;
using ClawControl.Features.Library;
using ClawControl.Hardware;

namespace ClawControl.Features.OptiScaler;

/// <summary>OptiScaler as Claw Control put it into one game (saved in settings): what it added and the choices.</summary>
public sealed class OptiScalerInstall
{
    /// <summary>The game's program; OptiScaler sits next to it.</summary>
    public string Exe { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>The name OptiScaler goes by in the game's folder (dxgi.dll, winmm.dll…).</summary>
    public string Proxy { get; set; } = "";
    /// <summary>Everything added, relative to the program's folder (folders end with a backslash).</summary>
    public Dictionary<string, string> Hashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? EpicLaunch { get; set; }
    public string[]? OriginalEpicLines { get; set; }
    public string[]? AppliedEpicLines { get; set; }
    public bool AddedDx12 { get; set; }
    public bool Incomplete { get; set; }
    public List<string> Files { get; set; } = new();
    /// <summary>"fsr31", "fsr4" or "xess".</summary>
    public string Upscaler { get; set; } = "fsr31";
    /// <summary>"off", "fsr" or "xess".</summary>
    public string FrameGen { get; set; } = "off";
    /// <summary>The game is told the GPU is an NVIDIA one (unlocks DLSS-only menus).</summary>
    public bool Nvidia { get; set; } = true;
    /// <summary>What the game ships for frame generation (decides where OptiScaler takes its frames from).</summary>
    public bool GameHasDlssg { get; set; }
    public bool GameHasFsrFg { get; set; }
}

/// <summary>A game the OptiScaler screen offers: its name, program (when certain: running, or
/// OptiScaler is in it), install folder, and the Library's program (often a launcher: only a hint).</summary>
public sealed record GameTarget(string Name, string? Exe, string? Folder, int? ProcessId = null, string? Program = null, string? Launch = null);

/// <summary>What a look through a game's folder found. <see cref="Dx12Switch"/>: the launch option
/// that starts this game in DirectX 12, for games whose upscalers only exist there (else null).</summary>
public sealed record GameScan(string Exe, string Api, IReadOnlyList<string> Upscalers, bool Dlssg, bool FsrFg, string? AntiCheat, bool Foreign, string? Dx12Switch = null);

/// <summary>
/// OptiScaler (github.com/optiscaler/OptiScaler, GPL-3.0) in games. Claw Control doesn't ship
/// it: the first Install downloads the pinned release from its GitHub page, checks it against
/// the release checksum embedded in this app, and unpacks it with Windows' own tar into <see cref="PackageDir"/>. Installing
/// copies OptiScaler next to the game's program under a name Windows loads from there first, plus
/// its helper files, never replacing a file the game already has, and remembers exactly what it
/// added; Remove deletes exactly that. The choices go into OptiScaler.ini and apply when the
/// game next starts. Nothing runs in the background.
/// </summary>
public sealed partial class OptiScalerService
{
    internal const string PackageVersion = "0.9.5";
    const string PackageUrl = "https://github.com/optiscaler/OptiScaler/releases/download/v0.9.5/Optiscaler_0.9.5-final.20261116.7z";
    const string PackageHash = "77e3ed76bed04e1fb32bacb5a72799ff172f3da22ed5231c0cf3e84fe6761662";
    const string Readme = "OptiScaler - added by Claw Control.txt";

    /// <summary>Names OptiScaler can go by, most compatible first (OptiScaler's own setup's list).</summary>
    static readonly string[] DxNames = ["dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll", "wininet.dll", "winhttp.dll"];
    static readonly string[] VulkanNames = ["winmm.dll", "version.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll"];
    /// <summary>Games known to need one name (OptiScaler's wiki).</summary>
    static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ZenlessZoneZero.exe"] = "d3d12.dll",
    };
    /// <summary>Games whose upscalers only appear in DirectX 12, and the launch option that starts
    /// them in it (OptiScaler's wiki; HoYoPlay only offers DX12 on GPUs HoYoverse lists).</summary>
    static readonly Dictionary<string, string> Dx12Switches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ZenlessZoneZero.exe"] = "-use-d3d12",
    };
    /// <summary>Package files that stay out of the game's folder.</summary>
    static readonly Regex NotCopied = new(@"(?i)^(setup_.*\.(bat|sh)|!!.*|licenses\\.*|optiscaler\.(dll|ini))$");
    /// <summary>Programs in a game's folder that aren't the game.</summary>
    static readonly Regex NotTheGame = new(@"(?i)(unins|setup|install|redist|dxsetup|directx|crash(report|handler|pad|sender|dump|_)|bugreport|errorreport|launcher|helper|updat|easyanticheat|battleye|_be\.|start_protected|prereq|dotnet|bootstrap|elevate|cefprocess|webhelper|7z|hpatch|quicksfv|notification|overlay|benchmark|server|editor|cleanup|repair|selector|^hyp)");
    /// <summary>Folders of installers and redistributables, not games.</summary>
    static readonly Regex NotGameFolder = new(@"(?i)\\(_?redist|_commonredist|redistributables?|directx|dotnet|vcredist|installers?|__installer|prereq\w*|support|tools?)\\");

    static readonly HttpClient Http = CreateClient();


    /// <summary>Where downloaded OptiScaler releases are unpacked (one folder per version): in the
    /// program's own folder, which only administrators can change. Claw Control copies from here
    /// into game folders as admin, and some games run as admin, so the files must not be
    /// replaceable by an ordinary program after their checksum was checked.</summary>
    public static string PackageDir => Path.Combine(AppContext.BaseDirectory, "OptiScaler");

    public IReadOnlyList<OptiScalerInstall> Installs => records;

    /// <summary>The games to offer: the game in front (if any, joined with its Library entry)
    /// first, then the Library's games, then games OptiScaler is still in that are neither (so it
    /// can always be removed). After the first: games with OptiScaler, then games whose last look
    /// found an upscaler, then the rest (Library order within each).</summary>
    public List<GameTarget> Games(RunningApp? front)
    {
        var list = new List<GameTarget>();
        var games = library.Games;
        LibraryItem? frontItem = null;
        if (front?.Path is { } path)
        {
            frontItem = Match(front, games);
            list.Add(new GameTarget(frontItem?.Name ?? front.Name, path, frontItem?.Folder, front.ProcessId, frontItem?.Program, frontItem?.Launch));
        }
        var rest = new List<GameTarget>();
        foreach (var game in games)
            if (game != frontItem)
                rest.Add(new GameTarget(game.Name, game.GameProgram is { } known && File.Exists(known) ? known : null, game.Folder, Program: game.Program, Launch: game.Launch));
        foreach (var install in records)
            if (!list.Concat(rest).Any(t => InstallFor(t) == install))
                rest.Add(new GameTarget(install.Name, install.Exe, null));
        list.AddRange(rest);
        list = list.OrderBy(Rank).ToList();
        return list;
    }

    int Rank(GameTarget target)
    {
        if (InstallFor(target) != null) return 0;
        if (!TryCachedScan(target, out var scan)) return 2;
        return scan == null ? 4 : scan.Upscalers.Count > 0 ? 1 : 3;
    }

    /// <summary>The Library entry of a running game: its program is in the entry's folder or is the
    /// program learned for it, it was started by a program in the entry's folder (a launcher such
    /// as Epic's HoYoPlay), or its name is the entry's name ("ZenlessZoneZero" = "Zenless Zone Zero").</summary>
    static LibraryItem? Match(RunningApp app, IReadOnlyList<LibraryItem> games)
    {
        if (app.Path is not { } path) return null;
        var byFolder = games.FirstOrDefault(g => Under(path, g.Folder) || string.Equals(g.GameProgram, path, StringComparison.OrdinalIgnoreCase));
        if (byFolder != null) return byFolder;
        if (ParentOf(app.ProcessId) is { } parent && ImagePath(parent) is { } parentPath)
            if (games.FirstOrDefault(g => Under(parentPath, g.Folder)) is { } byLauncher) return byLauncher;
        string name = Simple(app.Name), file = Simple(Path.GetFileNameWithoutExtension(path));
        return games.FirstOrDefault(g => Simple(g.Name) is { Length: > 2 } n && (n == name || n == file));
    }

    static string Simple(string name) => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The panel opened over a full-screen game: its Library entry remembers the game's
    /// real program, so it's found later with the game closed. Cheap (a few calls); on the UI thread.</summary>
    public void Learn(RunningApp app)
    {
        if (!Enabled) return;
        try
        {
            if (app.Path is not { } path || Match(app, library.Games) is not { } item) return;
            if (string.Equals(item.GameProgram, path, StringComparison.OrdinalIgnoreCase)) return;
            item.GameProgram = path;
            settings.SaveSoon();
            Log.Info($"Library: {item.Name} runs as {path}");
        }
        catch (Exception ex)
        {
            Log.Error("Learning the game in front", ex);
        }
    }

    /// <summary>OptiScaler as Claw Control put it into this game, if it did.</summary>
    public OptiScalerInstall? InstallFor(GameTarget target)
    {
        string? exe = target.Exe ?? (TryCachedScan(target, out var scan) ? scan?.Exe : null);
        return records.FirstOrDefault(i => exe != null && i.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase))
            ?? (target.Folder != null ? records.FirstOrDefault(i => Under(i.Exe, target.Folder)) : null);
    }

    static bool Under(string path, string? folder) =>
        folder != null && path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    static bool SameFolder(string? a, string? b) =>
        a != null && b != null && Path.TrimEndingDirectorySeparator(a).Equals(Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- Looking at a game

    readonly Dictionary<string, GameScan?> _scans = new(StringComparer.OrdinalIgnoreCase);

    static string KeyOf(GameTarget target) => target.Exe ?? target.Folder ?? target.Name;

    /// <summary>What the last look at this game found this run (null inside = its program wasn't found).</summary>
    public bool TryCachedScan(GameTarget target, out GameScan? scan)
    {
        lock (_scans) return _scans.TryGetValue(KeyOf(target), out scan);
    }

    /// <summary>Forget the last look (after installing / removing, or to look again).</summary>
    public void Forget(GameTarget target)
    {
        lock (_scans)
        {
            _scans.Remove(KeyOf(target));
            if (InstallFor(target)?.Exe is { } exe) _scans.Remove(exe);
        }
    }

    /// <summary>Finds the game's program and looks through its folder (off the UI thread; kept for
    /// the run); null if its program can't be found.</summary>
    public Task<GameScan?> ScanAsync(GameTarget target) => Task.Run(() =>
    {
        if (TryCachedScan(target, out var cached)) return cached;
        GameScan? scan;
        try { scan = Scan(target); }
        catch (Exception ex) { Log.Error($"OptiScaler: looking at {target.Name}", ex); return null; }
        lock (_scans) _scans[KeyOf(target)] = scan;
        // Found outside its Library folder (HoYoPlay's list): its Library entry remembers the program.
        if (scan != null && target.Exe == null && target.Folder != null && !Under(scan.Exe, target.Folder)
            && library.Games.FirstOrDefault(g => SameFolder(g.Folder, target.Folder)) is { } item && item.GameProgram == null)
        {
            item.GameProgram = scan.Exe;
            settings.SaveSoon();
        }
        return scan;
    });

    GameScan? Scan(GameTarget target)
    {
        var install = target.Exe != null ? InstallFor(target) : records.FirstOrDefault(i => Under(i.Exe, target.Folder));
        string? exe = target.Exe ?? install?.Exe ?? FindExe(target.Folder, target.Program) ?? HoYoPlayExe(target.Name);
        if (exe == null || !File.Exists(exe)) return null;
        string dir = Path.GetDirectoryName(exe)!;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // OptiScaler's own helper files (its FSR / XeSS) aren't the game's.
        var ours = new HashSet<string>((InstallFor(target with { Exe = exe })?.Files ?? []).Select(f => Path.GetFileName(f.TrimEnd('\\'))), StringComparer.OrdinalIgnoreCase);
        string? antiCheat = null;
        Walk(RootOf(exe, target.Folder), maxDepth: 7, maxEntries: 60000, (name, isDir) =>
        {
            if (ours.Contains(name)) return;
            names.Add(name);
            antiCheat ??= AntiCheatOf(name, isDir);
        });

        var upscalers = new List<string>();
        bool dlss = names.Contains("nvngx_dlss.dll"), dlssg = names.Contains("nvngx_dlssg.dll") || names.Contains("sl.dlss_g.dll");
        bool xess = names.Contains("libxess.dll");
        bool fsr = names.Any(n => n.StartsWith("ffx_fsr", StringComparison.OrdinalIgnoreCase) || n.StartsWith("amd_fidelityfx", StringComparison.OrdinalIgnoreCase));
        bool fsrFg = names.Contains("amd_fidelityfx_framegeneration_dx12.dll") || names.Any(n => n.StartsWith("ffx_frameinterpolation", StringComparison.OrdinalIgnoreCase));
        if (dlss) upscalers.Add("DLSS");
        if (xess) upscalers.Add("XeSS");
        if (fsr) upscalers.Add("FSR");

        // Someone else's OptiScaler: a loader-named file that is really OptiScaler, or its ini.
        bool foreign = DxNames.Concat(VulkanNames).Distinct(StringComparer.OrdinalIgnoreCase).Any(n =>
                !n.Equals(install?.Proxy, StringComparison.OrdinalIgnoreCase) && IsOptiScaler(Path.Combine(dir, n)))
            || (install == null && File.Exists(Path.Combine(dir, "OptiScaler.ini")));

        return new GameScan(exe, Api(exe, dir, target.ProcessId), upscalers, dlssg, fsrFg, antiCheat, foreign,
            Dx12Switches.GetValueOrDefault(Path.GetFileName(exe)));
    }

    static bool IsOptiScaler(string file)
    {
        try { return File.Exists(file) && FileVersionInfo.GetVersionInfo(file).OriginalFilename?.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; }
    }

    /// <summary>The game's program in its install folder: an Unreal "-Shipping" program if there
    /// is one, else a Unity game's (next to UnityPlayer.dll), else the Library's program (unless
    /// it's a launcher), else the biggest program that isn't a launcher, installer or crash reporter.</summary>
    static string? FindExe(string? folder, string? hint)
    {
        if (folder == null || !Directory.Exists(folder)) return hint != null && File.Exists(hint) && !NotTheGame.IsMatch(Path.GetFileName(hint)) ? hint : null;
        var found = new List<FileInfo>();
        Walk(folder, maxDepth: 5, maxEntries: 40000, (_, _) => { }, file =>
        {
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !NotTheGame.IsMatch(Path.GetFileName(file))
                && !NotGameFolder.IsMatch(file[folder.Length..]))
                try { found.Add(new FileInfo(file)); } catch { }
        });
        return (found.FirstOrDefault(f => f.Name.Contains("-Shipping", StringComparison.OrdinalIgnoreCase))
            ?? found.FirstOrDefault(f => File.Exists(Path.Combine(f.DirectoryName!, "UnityPlayer.dll")))
            ?? found.FirstOrDefault(f => hint != null && Path.GetFullPath(hint).Equals(f.FullName, StringComparison.OrdinalIgnoreCase))
            ?? found.OrderByDescending(f => f.Length).FirstOrDefault())?.FullName;
    }

    /// <summary>A HoYoverse game installed by HoYoPlay (also Epic's HoYoPlay): HoYoPlay notes each
    /// game's name and install folder in its gamedata.dat files.</summary>
    static string? HoYoPlayExe(string name)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cognosphere", "HYP");
        if (!Directory.Exists(root)) return null;
        string wanted = Simple(name);
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "gamedata.dat", SearchOption.AllDirectories))
            {
                string text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
                foreach (Match m in Regex.Matches(text, @"""gameShortcutName""\s*:\s*""(?<name>[^""]*)""\s*,\s*""installPath""\s*:\s*""(?<path>(?:[^""\\]|\\.)*)"""))
                {
                    if (Simple(m.Groups["name"].Value) != wanted) continue;
                    string folder = m.Groups["path"].Value.Replace(@"\\", @"\");
                    if (FindExe(folder, null) is { } exe) return exe;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Detail($"Reading HoYoPlay's game list: {ex.Message}");
        }
        return null;
    }

    // ---------------------------------------------------------------- DirectX 12 through Epic

    static string EpicSettingsFile
    {
        get
        {
            string config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved", "Config");
            string editor = Path.Combine(config, "WindowsEditor", "GameUserSettings.ini");
            return File.Exists(editor) ? editor : Path.Combine(config, "Windows", "GameUserSettings.ini");
        }
    }

    /// <summary>"namespace:catalog:app" from a Library entry's Epic link (com.epicgames.launcher://apps/…?action=launch).</summary>
    static string? EpicKey(string? launch)
    {
        if (launch == null || Regex.Match(launch, @"^com\.epicgames\.launcher://apps/([^?/]+)", RegexOptions.IgnoreCase) is not { Success: true } m) return null;
        string key = Uri.UnescapeDataString(m.Groups[1].Value);
        return Regex.IsMatch(key, @"\A[\w.-]+:[\w.-]+:[\w.-]+\z") ? key : null;
    }

    /// <summary>For a game that needs a DirectX 12 launch option and comes from Epic: whether Epic's
    /// launch options for it (Library › … › Manage › Launch Options) include it; null = not offered.</summary>
    public static bool? EpicDx12(GameTarget target, GameScan scan)
    {
        if (scan.Dx12Switch is not { } option || EpicKey(target.Launch) is not { } key) return null;
        try
        {
            if (!File.Exists(EpicSettingsFile)) return false;
            // ReadAllLines drops the line endings (a "\r" left on the value once made this always false).
            var lines = File.ReadAllLines(EpicSettingsFile).Select(l => l.Trim()).ToList();
            string enabledKey = key + "_AdditionalCommandsEnabled=", argsKey = key + "_AdditionalCommands=";
            bool enabled = lines.Any(l => l.Equals(enabledKey + "True", StringComparison.OrdinalIgnoreCase));
            return enabled && lines.FirstOrDefault(l => l.StartsWith(argsKey, StringComparison.OrdinalIgnoreCase)) is { } args
                && args[argsKey.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(option, StringComparer.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Epic's settings text with the option added to / removed from a game's launch options
    /// (its other options kept); null if there's no "[account_Settings]" section. Epic keeps per-game
    /// settings in the "_Settings" section that already names the game (else the first one) as
    /// key_AdditionalCommandsEnabled=True and key_AdditionalCommands=-option ….</summary>
    public static string? WithEpicOption(string text, string key, string option, bool on)
    {
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(newline).ToList();
        var sections = Enumerable.Range(0, lines.Count).Where(i => Regex.IsMatch(lines[i], @"^\[.+_Settings\]\s*$")).ToList();
        if (sections.Count == 0) return null;
        int End(int start) { int e = start + 1; while (e < lines.Count && !lines[e].StartsWith('[')) e++; return e; }
        var matches = sections.Where(s => lines.Skip(s + 1).Take(End(s) - s - 1)
            .Any(l => l.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase))).ToList();
        if (matches.Count > 1 || matches.Count == 0 && sections.Count != 1) return null;
        int header = matches.Count == 1 ? matches[0] : sections[0];
        int end = End(header);
        string enabledKey = key + "_AdditionalCommandsEnabled=", argsKey = key + "_AdditionalCommands=";
        var args = lines.Skip(header + 1).Take(end - header - 1).FirstOrDefault(l => l.StartsWith(argsKey, StringComparison.OrdinalIgnoreCase)) is { } old
            ? old[argsKey.Length..] : "";
        var token = new Regex(@"(?<!\S)" + Regex.Escape(option) + @"(?!\S)", RegexOptions.IgnoreCase);
        if (on && !token.IsMatch(args)) args += (args.Length == 0 ? "" : " ") + option;
        if (!on) args = token.Replace(args, "").TrimEnd();
        // Drop the old two lines, then write them fresh right under the section's header.
        for (int i = end - 1; i > header; i--)
            if (lines[i].StartsWith(enabledKey, StringComparison.OrdinalIgnoreCase) || lines[i].StartsWith(argsKey, StringComparison.OrdinalIgnoreCase)) lines.RemoveAt(i);
        lines.Insert(header + 1, $"{argsKey}{args}");
        lines.Insert(header + 1, $"{enabledKey}{(args.Length > 0 ? "True" : "False")}");
        return string.Join(newline, lines);
    }

    /// <summary>Adds or removes the DirectX 12 launch option in Epic's settings (other options kept).
    /// Epic keeps its settings in memory and writes them on exit, so it must be closed; a problem, or null.</summary>
    public async Task<string?> SetEpicDx12Async(GameTarget target, GameScan? scan, bool on)
    {
        if (InstallFor(target) is not { } install || (scan?.Dx12Switch ?? Dx12Switches.GetValueOrDefault(Path.GetFileName(install.Exe))) is not { } option ||
            EpicKey(install.EpicLaunch ?? target.Launch) is not { } key) return "Epic launch settings could not be located";
        if (!await operations.WaitAsync(0)) return "Another game operation is in progress";
        try
        {
            RequireRecord(install);
            if (on && !Enabled) return "Enable the beta before changing launch settings";
            if (!on && !install.AddedDx12) return "This launch option was set outside this version of Claw Control";
            if (Process.GetProcessesByName("EpicGamesLauncher").Length > 0) return "Quit Epic Games before changing its launch options";
            string original = GameFiles.AsPlayer(() => File.ReadAllText(EpicSettingsFile));
            string enabled = key + "_AdditionalCommandsEnabled=", args = key + "_AdditionalCommands=";
            bool IsGameLine(string line) => line.StartsWith(enabled, StringComparison.OrdinalIgnoreCase) || line.StartsWith(args, StringComparison.OrdinalIgnoreCase);
            string[] GameLines(string text) => text.Replace("\r\n","\n").Split('\n').Where(IsGameLine).ToArray();
            var before = GameLines(original);
            if (before.Count(l => l.StartsWith(args,StringComparison.OrdinalIgnoreCase)) > 1 ||
                before.Count(l => l.StartsWith(enabled,StringComparison.OrdinalIgnoreCase)) > 1)
                return "Multiple Epic accounts have launch options for this game. Review them in Epic.";
            string changed;
            if (on)
            {
                if (before.Any(l => l.StartsWith(args,StringComparison.OrdinalIgnoreCase) && l[args.Length..].Split(' ').Contains(option,StringComparer.OrdinalIgnoreCase)))
                    return "Epic already has that option. It will stay under your control.";
                if (before.Any(l => l.StartsWith(args,StringComparison.OrdinalIgnoreCase) && l.Length > args.Length) &&
                    !before.Any(l => l.Equals(enabled + "True", StringComparison.OrdinalIgnoreCase)))
                    return "Epic has disabled launch arguments for this game. Review them in Epic first.";
                changed = WithEpicOption(original,key,option,true) ?? throw new IOException("Epic settings could not be located");
                install.OriginalEpicLines = before;
                install.AppliedEpicLines = GameLines(changed);
                install.EpicLaunch = target.Launch ?? install.EpicLaunch;
                install.AddedDx12 = true;
                SaveRecord(install);
            }
            else if (before.SequenceEqual(install.OriginalEpicLines ?? [])) changed = original;
            else
            {
                if (!before.SequenceEqual(install.AppliedEpicLines ?? [])) return "Epic launch arguments have changed. Review them in Epic, then choose Keep.";
                string newline = original.Contains("\r\n") ? "\r\n" : "\n";
                var lines = original.Split(newline).ToList();
                int at = lines.FindIndex(IsGameLine);
                if (at < 0) return "Review the launch arguments in Epic";
                lines.RemoveAll(l => IsGameLine(l));
                lines.InsertRange(at,install.OriginalEpicLines ?? []);
                changed = string.Join(newline,lines);
            }
            await Task.Run(() => GameFiles.AsPlayer(() =>
            {
                if (File.ReadAllText(EpicSettingsFile) != original) throw new IOException("Epic settings changed. Check again.");
                if (changed != original) GameFiles.WriteText(EpicSettingsFile, changed);
                return true;
            }));
            install.AddedDx12 = on;
            SaveRecord(install);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally { operations.Release(); }
    }

    // ---------------------------------------------------------------- Running games

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    const uint QueryLimitedInformation = 0x1000;

    static int? ParentOf(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var info = new ProcessBasicInformation();
            return NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                ? (int)info.InheritedFromUniqueProcessId : null;
        }
        finally { CloseHandle(handle); }
    }

    static string? ImagePath(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var name = new StringBuilder(1024);
            int size = name.Capacity;
            return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>Whether this program is running now (one look at processes of that name).</summary>
    public static Task<bool> IsRunningAsync(string exe) => Task.Run(() =>
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            using (process)
                if (string.Equals(ImagePath(process.Id), exe, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    });

    public static Task<bool> LoaderObservedAsync(OptiScalerInstall install) => Task.Run(() =>
    {
        if (!DxNames.Contains(install.Proxy)) return false;
        string expected = Path.Combine(Path.GetDirectoryName(install.Exe)!, install.Proxy);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(install.Exe)))
            using (process)
                try
                {
                    if (string.Equals(ImagePath(process.Id), install.Exe, StringComparison.OrdinalIgnoreCase) &&
                        process.Modules.Cast<ProcessModule>().Any(m => string.Equals(m.FileName, expected, StringComparison.OrdinalIgnoreCase)) &&
                        IsOptiScaler(expected)) return true;
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
        return false;
    });

    /// <summary>Requests a normal exit so the game controls its save and exit prompts.</summary>
    public static async Task CloseGameAsync(string exe)
    {
        var app = await Task.Run(() => RunningApps.List().FirstOrDefault(a => string.Equals(a.Path, exe, StringComparison.OrdinalIgnoreCase)));
        if (app == null) return;
        Log.Info($"OptiScaler: closing {app.Name} so its new settings apply");
        using var process = Process.GetProcessById(app.ProcessId);
        if (string.Equals(ImagePath(process.Id), exe, StringComparison.OrdinalIgnoreCase)) process.CloseMainWindow();
    }

    /// <summary>Where the game's files start: its install folder, or for Unreal games
    /// (…\Binaries\Win64) three folders up, else the program's own folder.</summary>
    static string RootOf(string exe, string? folder)
    {
        if (folder != null && Under(exe, folder)) return folder;
        string dir = Path.GetDirectoryName(exe)!;
        if (Regex.IsMatch(dir, @"(?i)\\Binaries\\Win(64|GDK)$") && Directory.GetParent(dir)?.Parent?.Parent is { } top) return top.FullName;
        return dir;
    }

    static void Walk(string root, int maxDepth, int maxEntries, Action<string, bool> entry, Action<string>? file = null)
    {
        var pending = new Stack<(string Dir, int Depth)>();
        pending.Push((root, 0));
        int seen = 0;
        while (pending.Count > 0 && seen < maxEntries)
        {
            var (dir, depth) = pending.Pop();
            try
            {
                foreach (var child in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (++seen > maxEntries) break;
                    bool isDir = child.Attributes.HasFlag(FileAttributes.Directory);
                    entry(child.Name, isDir);
                    if (isDir) { if (depth < maxDepth && !child.Attributes.HasFlag(FileAttributes.ReparsePoint)) pending.Push((child.FullName, depth + 1)); }
                    else file?.Invoke(child.FullName);
                }
            }
            catch { }  // a folder that can't be read
        }
    }

    static string? AntiCheatOf(string name, bool isDir)
    {
        if (isDir)
            return name.ToLowerInvariant() switch
            {
                "easyanticheat" or "easyanticheat_eos" => "EasyAntiCheat",
                "battleye" => "BattlEye",
                "eaanticheat" => "EA AntiCheat",
                "xigncode" or "xigncode3" => "XIGNCODE3",
                "gameguard" => "GameGuard",
                _ => null,
            };
        string n = name.ToLowerInvariant();
        if (n == "start_protected_game.exe" || n.StartsWith("easyanticheat")) return "EasyAntiCheat";
        if (n.StartsWith("beservice") || n.StartsWith("beclient")) return "BattlEye";
        if (n == "hoyokprotect.sys") return "HoYoKProtect";
        if (n.StartsWith("mhyprot")) return "mhyprot";
        if (n.StartsWith("eaanticheat")) return "EA AntiCheat";
        if (n == "gamemon.des" || n == "gameguard.des") return "GameGuard";
        return null;
    }

    /// <summary>The graphics API now (a Unity game's log changes with each start; a scan is kept for the run).</summary>
    public static Task<string> ApiNowAsync(GameScan scan, int? pid) => Task.Run(() => Api(scan.Exe, Path.GetDirectoryName(scan.Exe)!, pid));

    /// <summary>DX12, DX11, Vulkan: what a Unity game's own log says its last start used, else a
    /// running game's loaded files, else what the program (or Unity's player) imports.</summary>
    static string Api(string exe, string dir, int? pid)
    {
        if (UnityLogApi(exe) is { } logged) return logged;
        var dlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pid is { } id)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                foreach (ProcessModule module in process.Modules) dlls.Add(module.ModuleName);
            }
            catch { }  // protected by its anti-cheat, or gone
        }
        if (dlls.Count == 0)
        {
            dlls.UnionWith(PeImports(exe));
            string unity = Path.Combine(dir, "UnityPlayer.dll");
            if (File.Exists(unity)) dlls.UnionWith(PeImports(unity));
        }
        if (dlls.Contains("vulkan-1.dll") && !dlls.Contains("d3d12.dll") && !dlls.Contains("d3d11.dll")) return "Vulkan";
        if (dlls.Contains("d3d12.dll")) return "DX12";
        if (dlls.Contains("d3d11.dll")) return "DX11";
        if (dlls.Contains("vulkan-1.dll")) return "Vulkan";
        return "";
    }

    /// <summary>A Unity game's last start: <c>Game_Data\app.info</c> names its company and product,
    /// and Unity writes <c>LocalLow\company\product\Player.log</c> with "Direct3D: Version: Direct3D 11.0".</summary>
    static string? UnityLogApi(string exe)
    {
        try
        {
            string info = Path.Combine(Path.GetDirectoryName(exe)!, Path.GetFileNameWithoutExtension(exe) + "_Data", "app.info");
            if (!File.Exists(info)) return null;
            var lines = File.ReadAllLines(info);
            if (lines.Length < 2) return null;
            string localLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
            string log = Path.Combine(localLow, lines[0].Trim(), lines[1].Trim(), "Player.log");
            if (!File.Exists(log)) return null;
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            for (int i = 0; i < 80 && reader.ReadLine() is { } line; i++)
            {
                if (line.Contains("Direct3D 12", StringComparison.Ordinal)) return "DX12";
                if (line.Contains("Direct3D 11", StringComparison.Ordinal)) return "DX11";
                if (line.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) && line.Contains("Version", StringComparison.OrdinalIgnoreCase)) return "Vulkan";
            }
        }
        catch { }  // being written, or not a Unity game
        return null;
    }

    /// <summary>The DLLs a program imports (normal and delay-loaded), read from its PE headers.</summary>
    static List<string> PeImports(string path)
    {
        var result = new List<string>();
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt16() != 0x5A4D) return result;
            fs.Position = 0x3C;
            int pe = br.ReadInt32();
            fs.Position = pe;
            if (br.ReadUInt32() != 0x4550) return result;
            fs.Position = pe + 6;
            int sectionCount = br.ReadUInt16();
            fs.Position = pe + 20;
            int optionalSize = br.ReadUInt16();
            int optional = pe + 24;
            fs.Position = optional;
            int dirs = optional + (br.ReadUInt16() == 0x20B ? 112 : 96);
            fs.Position = dirs + 8;
            uint importRva = br.ReadUInt32();
            fs.Position = dirs + 13 * 8;
            uint delayRva = br.ReadUInt32();
            var sections = new List<(uint Va, uint Size, uint Raw)>();
            for (int i = 0; i < sectionCount; i++)
            {
                fs.Position = optional + optionalSize + i * 40 + 8;
                uint virtualSize = br.ReadUInt32(), va = br.ReadUInt32(), rawSize = br.ReadUInt32(), raw = br.ReadUInt32();
                sections.Add((va, Math.Max(virtualSize, rawSize), raw));
            }
            long Offset(uint rva)
            {
                foreach (var s in sections) if (rva >= s.Va && rva < s.Va + s.Size) return rva - s.Va + s.Raw;
                return -1;
            }
            string Name(uint rva)
            {
                long at = Offset(rva);
                if (at < 0) return "";
                fs.Position = at;
                var sb = new StringBuilder();
                for (int c; (c = fs.ReadByte()) > 0 && sb.Length < 260;) sb.Append((char)c);
                return sb.ToString();
            }
            long imports = importRva != 0 ? Offset(importRva) : -1;
            for (int i = 0; imports >= 0 && i < 2048; i++)
            {
                fs.Position = imports + i * 20;
                uint lookup = br.ReadUInt32();
                fs.Position = imports + i * 20 + 12;
                uint name = br.ReadUInt32();
                if (lookup == 0 && name == 0) break;
                if (name != 0) result.Add(Name(name));
            }
            long delayed = delayRva != 0 ? Offset(delayRva) : -1;
            for (int i = 0; delayed >= 0 && i < 2048; i++)
            {
                fs.Position = delayed + i * 32;
                uint attributes = br.ReadUInt32(), name = br.ReadUInt32();
                if (attributes == 0 && name == 0) break;
                if (name != 0 && (attributes & 1) != 0) result.Add(Name(name));
            }
        }
        catch { }  // not a program we can read
        return result;
    }

    // ---------------------------------------------------------------- The OptiScaler release

    /// <summary>The latest release unpacked (downloading and checking it first if needed): its folder
    /// and version, or a problem to show.</summary>
    async Task<(string? Dir, string Version, string? Problem)> PackageAsync(IProgress<string> progress)
    {
        string dir = Path.Combine(PackageDir, PackageVersion);
        if (File.Exists(Path.Combine(dir, "OptiScaler.dll")) && File.Exists(Path.Combine(dir, "OptiScaler.ini"))) return (dir, PackageVersion, null);
        string archive = Path.Combine(PackageDir, "download.7z");
        try
        {
            Directory.CreateDirectory(PackageDir);
            progress.Report("Downloading");
            using (var reply = await Http.GetAsync(PackageUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                reply.EnsureSuccessStatusCode();
                long total = reply.Content.Headers.ContentLength ?? 0, done = 0;
                await using var source = await reply.Content.ReadAsStreamAsync();
                await using var target = File.Create(archive);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0) progress.Report($"Downloading {done / (double)total:P0}");
                }
            }
            string actual;
            await using (var check = File.OpenRead(archive)) actual = Convert.ToHexString(await SHA256.HashDataAsync(check));
            if (!actual.Equals(PackageHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(archive);
                Log.Error($"OptiScaler {PackageVersion}: the download doesn't match GitHub's checksum");
                return (null, "", "Download failed");
            }
            progress.Report("Unpacking");
            string? unpackProblem = await Task.Run(() => Unpack(archive, dir));
            File.Delete(archive);
            if (unpackProblem != null) return (null, "", unpackProblem);
            Log.Info($"OptiScaler {PackageVersion} downloaded and checked");
            return (dir, PackageVersion, null);
        }
        catch (Exception ex)
        {
            Log.Error("Downloading OptiScaler", ex);
            try { File.Delete(archive); } catch { }
            return (null, "", "No connection");
        }
    }

    /// <summary>Unpacks the release with Windows' tar (libarchive reads 7z); a problem, or null.</summary>
    static string? Unpack(string archive, string dir)
    {
        string temp = dir + ".tmp";
        try
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            Directory.CreateDirectory(temp);
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("-xf");
            start.ArgumentList.Add(archive);
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(temp);
            using var tar = Process.Start(start)!;
            string errors = tar.StandardError.ReadToEnd();
            if (!tar.WaitForExit(120_000) || tar.ExitCode != 0 || !File.Exists(Path.Combine(temp, "OptiScaler.dll")))
            {
                Log.Error($"Unpacking OptiScaler failed: {errors.Trim()}");
                return "Couldn't unpack OptiScaler";
            }
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.Move(temp, dir);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Unpacking OptiScaler", ex);
            return "Couldn't unpack OptiScaler";
        }
    }

    // ---------------------------------------------------------------- Install, change, remove

    static string Configure(string text, OptiScalerInstall install)
    {
        string up = install.Upscaler;
        text = SetKey(text, "Upscalers", "Dx12Upscaler", up == "xess" ? "xess" : "fsr31");
        text = SetKey(text, "Upscalers", "Dx11Upscaler", up == "xess" ? "xess_12" : "fsr31");
        text = SetKey(text, "Upscalers", "VulkanUpscaler", up == "xess" ? "xess" : "fsr31");
        // The A8 beta offers FSR 3.1 and XeSS.
        text = SetKey(text, "FSR", "UpscalerIndex", up == "fsr31" ? "1" : "auto");
        text = SetKey(text, "FSR", "Fsr4Update", "false");
        text = SetKey(text, "FSR", "Fsr4ForceEnableInt8", "0");
        bool fg = install.FrameGen != "off";
        // Frames come from the game's own DLSS / FSR frame generation if it has one, else from the upscaler (with OptiScaler's HUD fix).
        string input = !fg ? "auto" : install.GameHasDlssg ? "dlssg" : install.GameHasFsrFg ? "fsrfg" : "upscaler";
        text = SetKey(text, "FrameGen", "Enabled", fg ? "true" : "false");
        text = SetKey(text, "FrameGen", "FGInput", input);
        text = SetKey(text, "FrameGen", "FGOutput", install.FrameGen == "xess" ? "xefg" : fg ? "fsrfg" : "auto");
        text = SetKey(text, "OptiFG", "HUDFix", input == "upscaler" ? "true" : "auto");
        text = SetKey(text, "Spoofing", "Dxgi", install.Nvidia ? "true" : "false");
        return text;
    }

    /// <summary>Sets key=value inside [section] of an ini text (adds it under the header if missing).</summary>
    static string SetKey(string text, string section, string key, string value)
    {
        var header = Regex.Match(text, $@"^\[{Regex.Escape(section)}\][^\S\r\n]*\r?$", RegexOptions.Multiline);
        if (!header.Success) return text + $"\r\n[{section}]\r\n{key}={value}\r\n";
        int start = header.Index + header.Length;
        var next = Regex.Match(text[start..], @"^\[", RegexOptions.Multiline);
        int end = next.Success ? start + next.Index : text.Length;
        var line = Regex.Match(text[start..end], $@"^{Regex.Escape(key)}[^\S\r\n]*=[^\r\n]*", RegexOptions.Multiline);
        if (line.Success) return text[..(start + line.Index)] + $"{key}={value}" + text[(start + line.Index + line.Length)..];
        return text[..start] + $"\r\n{key}={value}" + text[start..];
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ClawControl/{Updates.Current.ToString(3)}");
        return client;
    }
}
