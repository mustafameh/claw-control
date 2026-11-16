using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClawControl.Core;

namespace ClawControl.Features.Library;

/// <summary>A picture offered on a game's Cover screen: what it's called, a small one to show, and
/// the one to keep (a link, or a file on this Claw).</summary>
public sealed record CoverChoice(string Label, string Preview, string Full);

/// <summary>
/// Cover suggestions from Steam's store, found by the game's name (or its Steam id): the store's
/// public search and app pages, no account or key. Asked only when a game's Cover screen opens;
/// kept for the run. Also turns any picture into a 2 : 3 cover file.
/// </summary>
public static class CoverFinder
{
    const string Assets = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/";
    static readonly HttpClient Http = CreateHttp();
    static readonly Dictionary<string, List<CoverChoice>> Found = new();
    static readonly Dictionary<string, ImageSource?> Previews = new();
    /// <summary>Store results that aren't the game itself.</summary>
    static readonly Regex Extra = new(@"soundtrack|\bost\b|\bdlc\b|artbook|art book|season pass|demo\b|playtest|wallpaper|costume|skin\b|pack\b|upgrade|bundle", RegexOptions.IgnoreCase);

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ClawControl/{typeof(CoverFinder).Assembly.GetName().Version?.ToString(3)}");
        return http;
    }

    /// <summary>Steam covers for the game (its own first, then up to two other store matches), its
    /// store art and three screenshots. Empty if Steam can't be reached or has nothing.</summary>
    public static Task<List<CoverChoice>> SuggestAsync(LibraryItem game)
    {
        int? steamId = Regex.Match(game.Launch, @"^steam://rungameid/(\d+)$") is { Success: true } steam ? int.Parse(steam.Groups[1].Value) : null;
        return FindAsync(game.Key, game.Name, steamId, Queries(game.Name), 3);
    }

    /// <summary>What the user typed on the Cover screen: the covers of up to six store matches, then
    /// the first one's art and screenshots.</summary>
    public static Task<List<CoverChoice>> SearchAsync(string text) =>
        FindAsync("search:" + text.Trim().ToLowerInvariant(), text.Trim(), null, [text.Trim()], 6);

    static async Task<List<CoverChoice>> FindAsync(string key, string name, int? steamId, IEnumerable<string> queries, int games)
    {
        lock (Found)
            if (Found.TryGetValue(key, out var known)) return known;
        var choices = new List<CoverChoice>();
        try
        {
            var ids = new List<(int Id, string Name)>();
            if (steamId is int own) ids.Add((own, name));
            foreach (string query in queries)
            {
                var matches = await StoreSearchAsync(query);
                if (matches.Count == 0) continue;
                ids.AddRange(matches.Where(m => ids.All(i => i.Id != m.Id)));
                break;
            }
            ids = ids.Take(games).ToList();
            var covers = await Task.WhenAll(ids.Select(async m => await ExistsAsync(Assets + m.Id + "/library_600x900.jpg") ? m : default));
            foreach (var (id, title) in covers.Where(c => c.Id != 0))
            {
                string url = Assets + id + "/library_600x900.jpg";
                choices.Add(new CoverChoice(choices.Count == 0 && steamId != null ? "Steam cover" : title, url, url));
            }
            if (ids.Count > 0)
            {
                int best = ids[0].Id;
                if (await ExistsAsync(Assets + best + "/library_hero.jpg")) choices.Add(new CoverChoice("Steam art", Assets + best + "/library_hero.jpg", Assets + best + "/library_hero.jpg"));
                choices.AddRange((await ScreenshotsAsync(best)).Take(3).Select((s, i) => new CoverChoice($"Screenshot {i + 1}", s.Thumb, s.Full)));
            }
            Log.Info($"Cover suggestions for {name}: {choices.Count}");
        }
        catch (Exception ex)
        {
            Log.Info($"Cover suggestions for {name} not found: {ex.Message}");
            return choices;  // not kept: tried again next time
        }
        lock (Found) Found[key] = choices;
        return choices;
    }

    /// <summary>Names to search for, best first: without a repack's "[…]" / "(…)" notes, version tags
    /// and shortcut words ("Launch …", "… Launcher"); then a word fewer from the end, and from the start.</summary>
    static IEnumerable<string> Queries(string name)
    {
        string clean = Regex.Replace(name, @"\[[^\]]*\]|\([^)]*\)|\bv\d+(\.\d+)+\b", " ");
        clean = Regex.Replace(clean, @"^\s*(launch|play|start|run)\s+|\s+(launcher|game)\s*$", " ", RegexOptions.IgnoreCase);
        // Steam's search finds nothing for "Title - Subtitle" when the store says "Title: Subtitle": separators go.
        clean = Regex.Replace(clean, @"\s[-–—:|]+\s|:", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        if (clean.Length == 0) clean = name;
        var queries = new List<string> { clean };
        var words = clean.Split(' ');
        if (words.Length >= 3)
        {
            queries.Add(string.Join(' ', words[..^1]));
            queries.Add(string.Join(' ', words[1..]));
        }
        return queries.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    static string Plain(string name) => Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]", "");

    /// <summary>The store's matches, the game itself (same name) first, extras (soundtracks, DLC) left out.</summary>
    static async Task<List<(int Id, string Name)>> StoreSearchAsync(string name)
    {
        string json = await Http.GetStringAsync($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(name)}&l=english&cc=US");
        using var doc = JsonDocument.Parse(json);
        var found = new List<(int, string)>();
        if (doc.RootElement.TryGetProperty("items", out var items))
            foreach (var item in items.EnumerateArray())
                if (item.TryGetProperty("id", out var id) && item.TryGetProperty("name", out var title) && title.GetString() is { } text && !Extra.IsMatch(text))
                    found.Add((id.GetInt32(), text));
        // The game itself first: same name, or one that ends with it ("ABC: Full Name" for "Full Name").
        string wanted = Plain(name);
        return found.OrderByDescending(f => Plain(f.Item2) == wanted).ThenByDescending(f => Plain(f.Item2).EndsWith(wanted)).ToList();
    }

    static async Task<List<(string Thumb, string Full)>> ScreenshotsAsync(int id)
    {
        string json = await Http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={id}&filters=screenshots");
        using var doc = JsonDocument.Parse(json);
        var shots = new List<(string, string)>();
        if (doc.RootElement.TryGetProperty(id.ToString(), out var app) && app.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("screenshots", out var list))
            foreach (var shot in list.EnumerateArray())
                if (shot.TryGetProperty("path_thumbnail", out var thumb) && shot.TryGetProperty("path_full", out var full))
                    shots.Add((thumb.GetString()!, full.GetString()!));
        return shots;
    }

    static async Task<bool> ExistsAsync(string url)
    {
        try
        {
            using var answer = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
            return answer.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A picture (a link or a file) about <paramref name="height"/> pixels tall, kept for
    /// the run; null if it couldn't be read.</summary>
    public static async Task<ImageSource?> PreviewAsync(string source, int height)
    {
        string key = $"{source}|{height}";
        lock (Previews)
            if (Previews.TryGetValue(key, out var known)) return known;
        ImageSource? picture = null;
        try
        {
            byte[] bytes = source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? await Http.GetByteArrayAsync(source)
                : await File.ReadAllBytesAsync(source);
            picture = await Task.Run(() => Decode(bytes, height));
        }
        catch (Exception ex)
        {
            Log.Detail($"Cover preview {source}: {ex.Message}");
        }
        lock (Previews) Previews[key] = picture;
        return picture;
    }

    static BitmapSource Decode(byte[] bytes, int? height)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(bytes);
        if (height is { } h) image.DecodePixelHeight = h;
        image.CacheOption = BitmapCacheOption.OnLoad;
        // Not IgnoreImageCache: with a stream (no address) WPF throws "Value cannot be null (key)".
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Makes a cover file from a picture (a link or a file): the middle 2 : 3 of it, at
    /// most 600 × 900, as a JPEG in <paramref name="folder"/>. Returns the file.</summary>
    public static async Task<string> SaveCoverAsync(string source, string folder, string name)
    {
        byte[] bytes = source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? await Http.GetByteArrayAsync(source)
            : await File.ReadAllBytesAsync(source);
        return await Task.Run(() =>
        {
            BitmapSource image = Decode(bytes, null);
            int w = image.PixelWidth, h = image.PixelHeight;
            int cw = w, ch = (int)Math.Round(w * 1.5);
            if (ch > h) (ch, cw) = (h, (int)Math.Round(h / 1.5));
            BitmapSource cover = new CroppedBitmap(image, new Int32Rect((w - cw) / 2, (h - ch) / 2, cw, ch));
            if (cw > 600) cover = new TransformedBitmap(cover, new ScaleTransform(600.0 / cw, 600.0 / cw));
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(cover));
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, name + ".jpg");
            using (var stream = File.Create(file)) encoder.Save(stream);
            return file;
        });
    }
}
