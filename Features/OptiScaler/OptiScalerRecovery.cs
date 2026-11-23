using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClawControl.Core;
using ClawControl.Features.Library;

namespace ClawControl.Features.OptiScaler;

public sealed partial class OptiScalerService
{
    readonly Settings settings;
    readonly LibraryService library;
    readonly List<OptiScalerInstall> records = new();
    readonly SemaphoreSlim operations = new(1);
    const string Parked = ".clawcontrol-disabled";
    static string RecordDir => Path.Combine(PackageDir, "records");

    public string? RecoveryNotice { get; private set; }

    public OptiScalerService(Settings settings, LibraryService library)
    {
        this.settings = settings;
        this.library = library;
        if (Directory.Exists(RecordDir))
            foreach (var file in Directory.EnumerateFiles(RecordDir, "*.json"))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<OptiScalerInstall>(File.ReadAllText(file));
                    if (record == null || !string.Equals(file, RecordPath(record), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Invalid recovery record");
                    records.Add(record);
                }
                catch (Exception ex)
                {
                    Log.Error("Reading protected OptiScaler receipt", ex);
                    RecoveryNotice = "A recovery record could not be read. Check the Claw Control log and the file list beside the game's program.";
                }
            }
        foreach (var legacy in settings.OptiScalerGames)
            if (!records.Any(r => string.Equals(r.Exe, legacy.Exe, StringComparison.OrdinalIgnoreCase)))
                records.Add(legacy);
    }

    public bool Enabled => settings.OptiScalerEnabled;
    public void SetEnabled(bool on) { settings.OptiScalerEnabled = on; settings.SaveNow(); }

    static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
    static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    static string RecordPath(OptiScalerInstall record) => Path.Combine(RecordDir, TextHash(Path.GetFullPath(record.Exe).ToUpperInvariant()) + ".json");
    static void SaveRecord(OptiScalerInstall record)
    {
        Directory.CreateDirectory(RecordDir);
        GameFiles.WriteText(RecordPath(record), JsonSerializer.Serialize(record));
    }

    static string FileInGame(OptiScalerInstall record, string relative)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(record.Exe))!;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || relative.Contains(':') || !Under(path, root))
            throw new IOException("Invalid game file in the recovery record");
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(path)!); dir != null; dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("This folder contains a link. Manage this installation manually.");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("This game file is a link. It was left untouched.");
        return path;
    }

    // Import the private preview's receipt only when its loader and file list match the
    // on-disk marker and the known package. Paths in user settings never authorize elevated writes.
    static void RequireRecord(OptiScalerInstall record)
    {
        if (File.Exists(RecordPath(record)))
        {
            var saved = JsonSerializer.Deserialize<OptiScalerInstall>(File.ReadAllText(RecordPath(record)));
            if (saved == null || saved.Exe != record.Exe || saved.Proxy != record.Proxy || !saved.Hashes.SequenceEqual(record.Hashes))
                throw new IOException("Recovery record changed. Reopen Claw Control before continuing.");
            return;
        }
        var hashes = GameFiles.AsPlayer(() =>
        {
            var package = Path.Combine(PackageDir, PackageVersion);
            if (record.Version != PackageVersion || !DxNames.Contains(record.Proxy) ||
                Hash(FileInGame(record, record.Proxy)) != Hash(Path.Combine(package, "OptiScaler.dll")))
                throw new IOException("Could not verify this older installation. Use its file list for manual recovery.");
            var marker = FileInGame(record, Readme);
            var lines = File.ReadAllLines(marker);
            if (lines.Length < 4 || !lines[0].StartsWith("OptiScaler " + PackageVersion + " (github.com/optiscaler/OptiScaler)") ||
                !record.Files.SequenceEqual(lines.Skip(3)) || !record.Files.Contains(record.Proxy) || !record.Files.Contains("OptiScaler.ini"))
                throw new IOException("The older install record no longer matches the game folder");
            var verified = new Dictionary<string,string>();
            foreach (var entry in record.Files)
            {
                var path = FileInGame(record, entry.TrimEnd('\\'));
                if (entry.EndsWith('\\'))
                {
                    if (entry != @"D3D12_Optiscaler\") throw new IOException("Unknown folder in old install record");
                    continue;
                }
                if (entry == Readme || entry == "OptiScaler.ini") verified.Add(entry, Hash(path));
                else
                {
                    var source = Path.Combine(package, entry == record.Proxy ? "OptiScaler.dll" : entry);
                    if (!Under(Path.GetFullPath(source), package) || (entry != record.Proxy && NotCopied.IsMatch(entry)))
                        throw new IOException("Unknown file in old install record");
                    verified.Add(entry, Hash(source));
                }
            }
            return verified;
        });
        record.Hashes = hashes;
        record.AddedDx12 = false;
        record.Incomplete = false;
        SaveRecord(record);
    }

    public bool IsDisabled(OptiScalerInstall install) =>
        DxNames.Contains(install.Proxy) && File.Exists(Path.Combine(Path.GetDirectoryName(install.Exe)!, install.Proxy + Parked));

    public void RememberExecutable(GameTarget target)
    {
        if (LibraryGame(target) is not { } item) return;
        item.GameProgram = target.Exe;
        settings.SaveSoon();
    }
    public bool CanLaunch(GameTarget target) => LibraryGame(target) != null;
    public bool Launch(GameTarget target) => LibraryGame(target) is { } item && library.Open(item);
    LibraryItem? LibraryGame(GameTarget target) => library.Games.FirstOrDefault(g =>
        SameFolder(target.Folder, g.Folder) || string.Equals(g.GameProgram, target.Exe, StringComparison.OrdinalIgnoreCase) && target.Exe != null);

    public async Task<string?> InstallAsync(GameTarget target, GameScan scan, OptiScalerInstall choices, IProgress<string> progress)
    {
        if (!Enabled || scan.Foreign || scan.Upscalers.Count == 0 || choices.Upscaler is not ("fsr31" or "xess") || choices.FrameGen != "off")
            return "A detected game upscaler and beta opt-in are required. Existing external installs stay under their original manager.";
        if (!await operations.WaitAsync(0)) return "Another game operation is in progress";
        OptiScalerInstall? planned = null;
        try
        {
            if (InstallFor(target) != null) return "Already installed";
            var (package, version, problem) = await PackageAsync(progress);
            if (package == null) return problem;
            progress.Report("Installing");
            planned = new OptiScalerInstall { Name = target.Name, Exe = scan.Exe, Version = version,
                EpicLaunch = target.Launch, Upscaler = choices.Upscaler, Nvidia = choices.Nvidia, FrameGen = choices.FrameGen,
                GameHasDlssg = scan.Dlssg, GameHasFsrFg = scan.FsrFg, Incomplete = true };
            var record = planned;
            await Task.Run(() =>
            {
                var sources = new Dictionary<string,string>();
                var documents = new Dictionary<string,string>();
                GameFiles.AsPlayer(() =>
                {
                    var names = scan.Api == "Vulkan" ? VulkanNames : DxNames;
                    record.Proxy = KnownNames.GetValueOrDefault(Path.GetFileName(scan.Exe)) ??
                        names.FirstOrDefault(n => !File.Exists(FileInGame(record, n))) ?? throw new IOException("No free loader name");
                    foreach (var key in new[]{record.Proxy, record.Proxy + Parked, Readme, "OptiScaler.ini"})
                        if (File.Exists(FileInGame(record, key))) throw new IOException("Existing installation or conflicting file: " + key);
                    sources.Add(record.Proxy, Path.Combine(package, "OptiScaler.dll"));
                    foreach (var source in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories))
                    {
                        string name = Path.GetRelativePath(package, source);
                        if (NotCopied.IsMatch(name) || File.Exists(FileInGame(record, name))) continue;
                        sources.Add(name, source);
                        string? folder = Path.GetDirectoryName(name);
                        if (!string.IsNullOrEmpty(folder) && !Directory.Exists(FileInGame(record, folder)) && !record.Files.Contains(folder + "\\"))
                            record.Files.Add(folder + "\\");
                    }
                    foreach (var (name, source) in sources) record.Hashes.Add(name, Hash(source));
                    documents.Add("OptiScaler.ini", Configure(File.ReadAllText(Path.Combine(package, "OptiScaler.ini")), record));
                    record.Files.AddRange(sources.Keys.Concat(["OptiScaler.ini", Readme]));
                    documents.Add(Readme, "OptiScaler " + version + " installed by Claw Control. Use Graphics > OptiScaler for recovery.\r\n\r\n" +
                        string.Join("\r\n", record.Files) + "\r\n");
                    foreach (var (name, contents) in documents) record.Hashes.Add(name, TextHash(contents));
                    return true;
                });
                SaveRecord(record); // survives a crash, even before user settings are written
                GameFiles.AsPlayer(() =>
                {
                    foreach (var (name, contents) in documents)
                    {
                        using var output = new FileStream(FileInGame(record, name), FileMode.CreateNew);
                        using var writer = new StreamWriter(output);
                        writer.Write(contents);
                    }
                    // Loadable proxy last, after the configuration and all helpers.
                    foreach (var (name, source) in sources.OrderBy(pair => pair.Key == record.Proxy))
                    {
                        var path = FileInGame(record, name);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.Copy(source, path);
                    }
                    return true;
                });
                record.Incomplete = false;
                SaveRecord(record);
            });
            return null;
        }
        catch (UnauthorizedAccessException) { return "Windows denied write access. Use the launcher to move the game to a writable library, then try again."; }
        catch (Exception ex) { return "Installation stopped. " + ex.Message; }
        finally
        {
            if (planned != null && File.Exists(RecordPath(planned))) records.Add(planned);
            Forget(target);
            operations.Release();
        }
    }

    public async Task<string?> ApplyAsync(OptiScalerInstall install, OptiScalerInstall choices)
    {
        if (!Enabled || choices.Upscaler is not ("fsr31" or "xess")) return "Choose FSR 3.1 or XeSS";
        return await ChangeAsync(install, () =>
        {
            var path = FileInGame(install, "OptiScaler.ini");
            var text = Configure(File.ReadAllText(path), choices);
            GameFiles.WriteText(path, text);
            string saved = Hash(path);
            if (saved != TextHash(text)) throw new IOException("Saving the configuration could not be verified");
            install.Hashes["OptiScaler.ini"] = saved;
            install.Upscaler = choices.Upscaler;
            install.FrameGen = choices.FrameGen;
            install.Nvidia = choices.Nvidia;
        });
    }

    async Task<string?> ChangeAsync(OptiScalerInstall install, Action change)
    {
        if (!await operations.WaitAsync(0)) return "Another game operation is in progress";
        try
        {
            await Task.Run(() =>
            {
                RequireRecord(install);
                GameFiles.AsPlayer(() => { change(); return true; });
                SaveRecord(install);
            });
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally { operations.Release(); }
    }

    public Task<string?> KeepLaunchOptionAsync(OptiScalerInstall install) =>
        ChangeAsync(install, () => install.AddedDx12 = false);

    public async Task<string?> ToggleLoaderAsync(OptiScalerInstall install)
    {
        if (await IsRunningAsync(install.Exe)) return "Save and quit the game first";
        return await ChangeAsync(install, () =>
        {
            var loader = FileInGame(install, install.Proxy);
            var parked = FileInGame(install, install.Proxy + Parked);
            bool disabled = File.Exists(parked);
            if (disabled && (!Enabled || install.Upscaler == "fsr4")) throw new IOException("Enable the beta and choose a supported upscaler first");
            var source = disabled ? parked : loader;
            if (Hash(source) != install.Hashes[install.Proxy]) throw new IOException("The loader changed. It was left untouched.");
            File.Move(source, disabled ? loader : parked);
        });
    }

    public async Task<string?> RemoveAsync(OptiScalerInstall install, bool keepChangedFiles = false)
    {
        if (await IsRunningAsync(install.Exe)) return "Save and quit the game, then retry removal";
        if (install.AddedDx12) return "Restore the Epic launch option first, or choose to keep it";
        if (!await operations.WaitAsync(0)) return "Another game operation is in progress";
        try
        {
            string? preserved = await Task.Run(() =>
            {
                RequireRecord(install);
                return GameFiles.AsPlayer(() =>
                {
                    var changed = new List<string>();
                    foreach (var (name, digest) in install.Hashes.OrderByDescending(p => p.Key == install.Proxy))
                    {
                        foreach (var entry in name == install.Proxy ? new[]{name, name + Parked} : new[]{name})
                        {
                            var path = FileInGame(install, entry);
                            try { _ = File.GetAttributes(path); }
                            catch (FileNotFoundException) { continue; }
                            catch (DirectoryNotFoundException) { continue; }
                            if (Hash(path) != digest) { changed.Add(entry); continue; }
                            File.Delete(path);
                        }
                    }
                    foreach (var name in install.Files.Where(n => n.EndsWith('\\')).OrderByDescending(n => n.Length))
                    {
                        var path = FileInGame(install, name.TrimEnd('\\'));
                        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
                    }
                    return changed.Count == 0 ? null : "Changed files preserved: " + string.Join(", ", changed);
                });
            });
            if (preserved != null && !keepChangedFiles) return preserved;
            settings.OptiScalerGames.RemoveAll(i => string.Equals(i.Exe, install.Exe, StringComparison.OrdinalIgnoreCase));
            settings.SaveNow();
            File.Delete(RecordPath(install));
            records.Remove(install);
            lock (_scans) _scans.Clear();
            return preserved == null ? null : "Recovery finished. " + preserved;
        }
        catch (Exception ex) { return "Recovery incomplete. Close the game and retry. " + ex.Message; }
        finally { operations.Release(); }
    }
}
