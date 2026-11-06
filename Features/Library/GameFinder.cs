using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClawControl.Core;
using Microsoft.Win32;

namespace ClawControl.Features.Library;

/// <summary>
/// Finds the games installed on this Claw, only when the user asks (Find games on this Claw), from
/// the launchers' own files: Steam's library folders and app manifests, Epic's install manifests,
/// and the Xbox app's XboxGames folders. Nothing is read online and no account is needed. Steam's
/// cover art comes from its local cache; tools such as redistributables are left out.
/// </summary>
public static class GameFinder
{
    // Steam "games" that are tools: redistributables, Linux runtimes, Proton, SteamVR.
    static readonly HashSet<string> SteamTools = ["228980", "1070560", "1391110", "1628350", "1493710", "250820", "1826330", "1161040", "2180100", "2805730"];

    public static List<LibraryItem> Find()
    {
        var games = new List<LibraryItem>();
        foreach (var (source, find) in new (string, Func<IEnumerable<LibraryItem>>)[] { ("Steam", Steam), ("Epic Games", Epic), ("Xbox", Xbox) })
        {
            try { games.AddRange(find()); }
            catch (Exception ex) { Log.Error($"Looking for {source} games", ex); }
        }
        Log.Info($"Found {games.Count} games on this Claw");
        return games.DistinctBy(g => g.Key).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---- Steam ----

    static string? SteamFolder()
    {
        string? path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        return path != null && Directory.Exists(path) ? Path.GetFullPath(path) : null;
    }

    static IEnumerable<LibraryItem> Steam()
    {
        if (SteamFolder() is not { } steam) yield break;
        var libraries = new List<string> { steam };
        string folders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(folders))
            foreach (Match m in VdfValue("path").Matches(File.ReadAllText(folders)))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        foreach (string library in libraries.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (string manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text = File.ReadAllText(manifest);
                string? id = Value(text, "appid"), name = Value(text, "name"), dir = Value(text, "installdir");
                if (id == null || name == null || dir == null || SteamTools.Contains(id) || IsTool(name)) continue;
                string folder = Path.Combine(apps, "common", dir);
                if (!Directory.Exists(folder)) continue;
                yield return new LibraryItem
                {
                    Name = name, Source = "Steam", Launch = $"steam://rungameid/{id}", Folder = folder,
                    Cover = SteamCover(steam, id), Manifest = manifest, LastPlayed = SteamLastPlayed(text),
                };
            }
        }
    }

    static bool IsTool(string name) =>
        name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase);

    /// <summary>Steam's portrait cover (600 × 900) from its own cache, in either of its layouts.</summary>
    static string? SteamCover(string steam, string id)
    {
        string cache = Path.Combine(steam, "appcache", "librarycache");
        string flat = Path.Combine(cache, $"{id}_library_600x900.jpg");
        if (File.Exists(flat)) return flat;
        string folder = Path.Combine(cache, id);
        if (!Directory.Exists(folder)) return null;
        return Directory.EnumerateFiles(folder, "library_600x900*.jpg", SearchOption.AllDirectories).FirstOrDefault()
            ?? Directory.EnumerateFiles(folder, "library_capsule*.jpg", SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>When Steam last ran it (its manifest notes it), or null.</summary>
    public static DateTime? SteamLastPlayed(string manifestText) =>
        long.TryParse(Value(manifestText, "LastPlayed"), out long seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime
            : null;

    static string? Value(string vdf, string key) => VdfValue(key).Match(vdf) is { Success: true } m ? m.Groups[1].Value : null;

    static Regex VdfValue(string key) => new($"\"{Regex.Escape(key)}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);

    // ---- Epic ----

    static IEnumerable<LibraryItem> Epic()
    {
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data");
        string manifests = Path.Combine(data, "Manifests");
        if (!Directory.Exists(manifests)) yield break;
        var covers = EpicCovers(Path.Combine(data, "Catalog", "catcache.bin"));
        foreach (string file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            JsonElement root;
            try { root = JsonDocument.Parse(File.ReadAllText(file)).RootElement; }
            catch { continue; }
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) continue;
            if (root.TryGetProperty("AppCategories", out var categories) && categories.ValueKind == JsonValueKind.Array
                && !categories.EnumerateArray().Any(c => c.GetString() == "games")) continue;
            string? name = Text("DisplayName"), folder = Text("InstallLocation"), exe = Text("LaunchExecutable");
            string? ns = Text("CatalogNamespace"), item = Text("CatalogItemId"), app = Text("AppName");
            if (name == null || folder == null || app == null || !Directory.Exists(folder)) continue;
            string program = exe != null ? Path.Combine(folder, exe) : "";
            yield return new LibraryItem
            {
                Name = name, Source = "Epic Games", Folder = folder, Program = File.Exists(program) ? program : null,
                Launch = $"com.epicgames.launcher://apps/{ns}%3A{item}%3A{app}?action=launch&silent=true",
                CoverUrl = item != null ? covers.GetValueOrDefault(item) : null,
            };
        }
    }

    /// <summary>Epic's cover links on this Claw (catalog item id → link).</summary>
    public static Dictionary<string, string> EpicCoverLinks() => EpicCovers(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin"));

    /// <summary>An Epic game's catalog item id, from its launch link (apps/namespace:item:app).</summary>
    public static string? EpicItemId(LibraryItem game)
    {
        const string apps = "com.epicgames.launcher://apps/";
        if (!game.Launch.StartsWith(apps, StringComparison.OrdinalIgnoreCase)) return null;
        var parts = game.Launch[apps.Length..].Split('?')[0].Split("%3A");
        return parts.Length == 3 ? parts[1] : null;
    }

    /// <summary>Epic's catalog cache (base64 JSON on the device): each game's tall cover link, by
    /// catalog item id. The pictures themselves are on Epic's image server.</summary>
    static Dictionary<string, string> EpicCovers(string catalog)
    {
        var covers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(catalog)) return covers;
        try
        {
            using var json = JsonDocument.Parse(Convert.FromBase64String(File.ReadAllText(catalog).Trim()));
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var id) || !item.TryGetProperty("keyImages", out var images) || images.ValueKind != JsonValueKind.Array) continue;
                string? url = null;
                foreach (string wanted in new[] { "DieselGameBoxTall", "OfferImageTall" })
                    url ??= images.EnumerateArray()
                        .Where(i => i.TryGetProperty("type", out var type) && type.GetString() == wanted && i.TryGetProperty("url", out _))
                        .Select(i => i.GetProperty("url").GetString()).FirstOrDefault(u => u?.StartsWith("https://") == true);
                if (url != null && id.GetString() is { } key) covers.TryAdd(key, url);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading Epic's catalog cache", ex);
        }
        return covers;
    }

    // ---- Xbox (the Xbox app's XboxGames folders) ----

    static IEnumerable<LibraryItem> Xbox()
    {
        Dictionary<string, string>? families = null;
        foreach (var drive in DriveInfo.GetDrives())
        {
            string root;
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            }
            catch { continue; }
            if (!Directory.Exists(root)) continue;
            foreach (string game in Directory.EnumerateDirectories(root))
            {
                string content = Path.Combine(game, "Content"), config = Path.Combine(content, "MicrosoftGame.config");
                if (!File.Exists(config)) continue;
                XElement? xml;
                try { xml = XDocument.Load(config).Root; }
                catch { continue; }
                if (xml == null) continue;
                string? identity = xml.Element("Identity")?.Attribute("Name")?.Value;
                var visuals = xml.Element("ShellVisuals");
                var exe = xml.Element("ExecutableList")?.Elements("Executable").FirstOrDefault();
                string? appId = exe?.Attribute("Id")?.Value;
                if (identity == null || appId == null) continue;
                families ??= PackageFamilies();
                if (!families.TryGetValue(identity, out var family)) continue;
                string name = visuals?.Attribute("DefaultDisplayName")?.Value is { } shown && !shown.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)
                    ? shown : Path.GetFileName(game);
                string? logo = new[] { "Square480x480Logo", "Square150x150Logo", "StoreLogo" }
                    .Select(a => visuals?.Attribute(a)?.Value).OfType<string>()
                    .Select(p => Path.Combine(content, p)).FirstOrDefault(File.Exists);
                yield return new LibraryItem
                {
                    Name = name, Source = "Xbox", Folder = content, AppId = $"{family}!{appId}",
                    Launch = $@"shell:AppsFolder\{family}!{appId}", Cover = logo, SquareCover = true,
                };
            }
        }
    }

    /// <summary>Installed packages' names → family names (what a shell id is made of).</summary>
    static Dictionary<string, string> PackageFamilies()
    {
        var families = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var package in new Windows.Management.Deployment.PackageManager().FindPackagesForUser(""))
                families.TryAdd(package.Id.Name, package.Id.FamilyName);
        }
        catch (Exception ex)
        {
            Log.Error("Listing installed packages", ex);
        }
        return families;
    }
}
