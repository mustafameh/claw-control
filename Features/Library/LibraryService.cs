using System.Diagnostics;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClawControl.Core;
using ClawControl.Hardware;

namespace ClawControl.Features.Library;

/// <summary>
/// The Library's games and pinned apps (kept in settings): adding and removing them, which of
/// them are running, opening them, and their covers and icons. Nothing runs in the background:
/// the Library tab asks while it's shown.
/// </summary>
public sealed class LibraryService(Settings settings)
{
    readonly Dictionary<string, ImageSource?> _pictures = new();

    public IReadOnlyList<LibraryItem> Apps => settings.LibraryApps;

    /// <summary>The games, last played first; ones never played after them, in the order added.</summary>
    public IReadOnlyList<LibraryItem> Games => settings.LibraryGames.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ToList();

    public bool Has(LibraryItem item) => settings.LibraryGames.Concat(settings.LibraryApps).Any(i => i.Key == item.Key);

    public void AddGames(IEnumerable<LibraryItem> games)
    {
        settings.LibraryGames.AddRange(games.Where(g => !Has(g)));
        settings.SaveSoon();
    }

    public void Pin(StartMenuApp app)
    {
        var item = new LibraryItem { Name = app.Name, Source = "App", Launch = $@"shell:AppsFolder\{app.AppId}", AppId = app.AppId, Program = app.Program };
        if (Has(item)) return;
        settings.LibraryApps.Add(item);
        settings.SaveSoon();
    }

    public bool IsPinned(StartMenuApp app) => settings.LibraryApps.Any(a => a.AppId == app.AppId);

    public void Unpin(StartMenuApp app)
    {
        settings.LibraryApps.RemoveAll(a => a.AppId == app.AppId);
        settings.SaveSoon();
    }

    /// <summary>An installed app added as a game (one with its own installer: not in Steam, Epic or
    /// Xbox). Anything running from its program's folder counts as it running.</summary>
    public void AddGame(StartMenuApp app)
    {
        if (IsGame(app)) return;
        settings.LibraryGames.Add(new LibraryItem
        {
            Name = app.Name, Source = "Installed", Launch = $@"shell:AppsFolder\{app.AppId}", AppId = app.AppId,
            Program = app.Program, Folder = app.Program != null ? Path.GetDirectoryName(app.Program) : null,
        });
        settings.SaveSoon();
    }

    public bool IsGame(StartMenuApp app) => settings.LibraryGames.Any(g => g.AppId == app.AppId);

    public void RemoveGame(StartMenuApp app)
    {
        settings.LibraryGames.RemoveAll(g => g.AppId == app.AppId);
        settings.SaveSoon();
    }

    public void Remove(LibraryItem item)
    {
        settings.LibraryGames.Remove(item);
        settings.LibraryApps.Remove(item);
        settings.SaveSoon();
        // A cover downloaded for it goes too.
        try
        {
            if (item.Cover != null && item.CoverUrl != null && item.Cover.StartsWith(CoversFolder, StringComparison.OrdinalIgnoreCase)) File.Delete(item.Cover);
        }
        catch { }  // in use: it's small
        Forget(item);
    }

    // ---- Downloaded covers (Epic keeps only links to them on the device) ----

    static readonly string CoversFolder = Path.Combine(Log.Dir, "covers");
    static readonly HttpClient Http = CreateHttp();
    /// <summary>Games whose cover was tried this run (a failed one is tried again next run, not on every look).</summary>
    readonly HashSet<string> _coverTried = new();

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ClawControl/{typeof(LibraryService).Assembly.GetName().Version?.ToString(3)}");
        return http;
    }

    /// <summary>Downloads the covers games in the Library are missing (Epic's: from Epic's image
    /// server, no account needed) and keeps them on the Claw; returns the games that got one.</summary>
    public async Task<List<LibraryItem>> FetchCoversAsync()
    {
        var got = new List<LibraryItem>();
        Dictionary<string, string>? links = null;
        foreach (var game in settings.LibraryGames.ToList())
        {
            if (game.Cover != null && File.Exists(game.Cover)) continue;
            if (game.CoverUrl == null && GameFinder.EpicItemId(game) is { } id)
                game.CoverUrl = (links ??= await Task.Run(GameFinder.EpicCoverLinks)).GetValueOrDefault(id);
            if (game.CoverUrl == null || !_coverTried.Add(game.Key)) continue;
            try
            {
                // A smaller picture than the 1200 × 1600 original (the server scales it).
                string url = game.CoverUrl.Contains('?') ? game.CoverUrl : game.CoverUrl + "?h=600&w=450&resize=1";
                byte[] picture = await Http.GetByteArrayAsync(url);
                Directory.CreateDirectory(CoversFolder);
                string file = Path.Combine(CoversFolder, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(game.Key)))[..16] + ".jpg");
                await File.WriteAllBytesAsync(file, picture);
                game.Cover = file;
                Forget(game);
                got.Add(game);
                Log.Info($"Cover for {game.Name} downloaded ({picture.Length / 1024} KB)");
            }
            catch (Exception ex)
            {
                Log.Info($"Cover for {game.Name} not downloaded: {ex.Message}");
            }
        }
        if (got.Count > 0) settings.SaveSoon();
        return got;
    }

    /// <summary>Drops its kept pictures (a new cover came in, or it's gone).</summary>
    void Forget(LibraryItem item)
    {
        lock (_pictures)
            foreach (var key in _pictures.Keys.Where(k => k.StartsWith(item.Key + "|", StringComparison.Ordinal)).ToList())
                _pictures.Remove(key);
    }

    /// <summary>A program the user picked (Add a program).</summary>
    public static LibraryItem FromProgram(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        try
        {
            if (FileVersionInfo.GetVersionInfo(path) is { FileDescription: { Length: > 0 } described } && described.Length < 60) name = described.Trim();
        }
        catch { }  // its file name it is
        return new LibraryItem { Name = name, Source = "Program", Launch = path, Program = path, Folder = Path.GetDirectoryName(path) };
    }

    /// <summary>Steam notes when a game was last played: picks that up for games started from Steam itself.</summary>
    public void ReadLastPlayed()
    {
        bool changed = false;
        foreach (var game in settings.LibraryGames.ToList())
        {
            if (game.Manifest == null || !File.Exists(game.Manifest)) continue;
            try
            {
                if (GameFinder.SteamLastPlayed(File.ReadAllText(game.Manifest)) is { } played && played > (game.LastPlayed ?? DateTime.MinValue))
                    (game.LastPlayed, changed) = (played, true);
            }
            catch { }  // being written by Steam: next time
        }
        if (changed) settings.SaveSoon();
    }

    /// <summary>Which Library items are running now (by their shell id, program or install folder).</summary>
    public Dictionary<string, RunningApp> Running(List<RunningApp> running)
    {
        var found = new Dictionary<string, RunningApp>();
        foreach (var item in settings.LibraryGames.Concat(settings.LibraryApps))
            if (running.FirstOrDefault(app => IsRunning(item, app)) is { } app) found[item.Key] = app;
        return found;
    }

    static bool IsRunning(LibraryItem item, RunningApp app)
    {
        if (item.AppId != null && string.Equals(app.AppId, item.AppId, StringComparison.OrdinalIgnoreCase)) return true;
        if (app.Path == null) return false;
        if (item.Program != null && string.Equals(app.Path, item.Program, StringComparison.OrdinalIgnoreCase)) return true;
        return item.Folder != null && app.Path.StartsWith(Path.TrimEndingDirectorySeparator(item.Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens it the way the shell would (through Explorer, so it doesn't start with Claw
    /// Control's admin rights); notes when it was played. False if Windows refused.</summary>
    public bool Open(LibraryItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.Launch}\"") { UseShellExecute = false })?.Dispose();
            item.LastPlayed = DateTime.Now;
            settings.SaveSoon();
            Log.Info($"Opening {item.Name} from the Library ({item.Source})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Opening {item.Name}", ex);
            return false;
        }
    }

    /// <summary>The picture if it was loaded before (so a rebuilt tab shows it at once).</summary>
    public bool TryKnownPicture(LibraryItem item, int width, out ImageSource? picture)
    {
        lock (_pictures) return _pictures.TryGetValue($"{item.Key}|{width}", out picture);
    }

    /// <summary>Its cover (Steam's art, an Xbox logo) at about <paramref name="width"/> pixels, else
    /// its icon; loaded once and kept. Call off the UI thread.</summary>
    public ImageSource? Picture(LibraryItem item, int width)
    {
        string key = $"{item.Key}|{width}";
        lock (_pictures)
            if (_pictures.TryGetValue(key, out var known)) return known;
        ImageSource? picture = null;
        if (item.Cover != null && File.Exists(item.Cover))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(item.Cover);
                image.DecodePixelWidth = width;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.EndInit();
                image.Freeze();
                picture = image;
            }
            catch (Exception ex)
            {
                Log.Detail($"Cover of {item.Name}: {ex.Message}");
            }
        }
        picture ??= RunningApps.ShellIcon(item.AppId, item.Program, 96);
        lock (_pictures) _pictures[key] = picture;
        return picture;
    }

    /// <summary>A Start menu app's icon (the Add an app list). Call off the UI thread.</summary>
    public static ImageSource? IconOf(StartMenuApp app) => RunningApps.ShellIcon(app.AppId, app.Program, 64);
}
