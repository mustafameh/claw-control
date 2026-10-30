using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClawControl.Core;

/// <summary>A newer version on GitHub: its number, what's new, and its setup file.</summary>
public sealed record UpdateInfo(string Version, string Notes, string SetupUrl, string? SumsUrl);

/// <summary>
/// New versions from GitHub Releases, the only place Claw Control connects to: at most once a
/// day, just after the panel opens, in the background (a few seconds' limit; no connection, no
/// answer: nothing happens). What it found is kept in settings, so the header's update icon
/// stays until the update is installed. Installing downloads the release's setup, checks it
/// against the release's SHA256SUMS.txt and runs it; the setup closes Claw Control, replaces
/// it and starts it again. Nothing installs without the user pressing Update.
/// </summary>
public sealed class Updates(Settings settings)
{
    const string LatestRelease = "https://api.github.com/repos/mustafameh/claw-control/releases/latest";
    static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);

    static readonly HttpClient Http = CreateClient();
    int _checking;

    public static Version Current => typeof(Updates).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>A newer version than this one, if one was found (kept between runs).</summary>
    public UpdateInfo? Available => settings.Update is { } update && IsNewer(update.Version) ? update : null;

    /// <summary>The first time this version is offered: the panel shows the Update screen once.</summary>
    public bool ShouldPrompt => Available is { } update && settings.UpdatePrompted != update.Version;

    public void MarkPrompted()
    {
        if (Available is not { } update) return;
        settings.UpdatePrompted = update.Version;
        settings.SaveSoon();
    }

    /// <summary>A newer version was found (any thread).</summary>
    public event Action? Found;

    /// <summary>The panel opened: asks GitHub if the last look was more than a day ago.</summary>
    public void CheckIfDue()
    {
        if (DateTime.UtcNow - settings.LastUpdateCheck < CheckEvery) return;
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var reply = await Http.GetAsync(LatestRelease, limit.Token);
                settings.LastUpdateCheck = DateTime.UtcNow;
                settings.SaveSoon();
                if (!reply.IsSuccessStatusCode) return;
                using var json = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(limit.Token));
                var root = json.RootElement;
                string version = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
                if (!IsNewer(version)) return;
                string? setup = null, sums = null;
                foreach (var asset in root.GetProperty("assets").EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                    if (name.StartsWith("ClawControl-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) setup = url;
                    else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = url;
                }
                if (setup == null) return;
                string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
                settings.Update = new UpdateInfo(version, notes, setup, sums);
                settings.SaveSoon();
                Log.Info($"Update available: {version}");
                Found?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Detail($"Update check: {ex.Message}");  // offline or GitHub unreachable: try again tomorrow
            }
            finally
            {
                Interlocked.Exchange(ref _checking, 0);
            }
        });
    }

    /// <summary>Downloads the setup (progress 0-1), checks it and runs it; false (and why) if it couldn't.</summary>
    public async Task<(bool Ok, string? Problem)> InstallAsync(UpdateInfo update, IProgress<double> progress)
    {
        try
        {
            string file = Path.Combine(Path.GetTempPath(), Path.GetFileName(new Uri(update.SetupUrl).LocalPath));
            using (var reply = await Http.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                reply.EnsureSuccessStatusCode();
                long total = reply.Content.Headers.ContentLength ?? 0, done = 0;
                await using var source = await reply.Content.ReadAsStreamAsync();
                await using var target = File.Create(file);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0) progress.Report(done / (double)total);
                }
            }
            // Only what the release says it ships.
            if (update.SumsUrl != null)
            {
                string sums = await Http.GetStringAsync(update.SumsUrl);
                string name = Path.GetFileName(file);
                string? expected = sums.Split('\n').Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .FirstOrDefault(p => p.Length == 2 && p[1].Equals(name, StringComparison.OrdinalIgnoreCase))?[0];
                string actual;
                await using (var check = File.OpenRead(file)) actual = Convert.ToHexString(await SHA256.HashDataAsync(check));
                if (expected == null || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                    Log.Error($"Update {update.Version}: the download doesn't match its checksum");
                    return (false, "Download failed");
                }
            }
            Log.Info($"Installing update {update.Version}");
            // Claw Control runs as admin, so the setup does too (no prompt). It closes this copy.
            Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
            return (true, null);
        }
        catch (Exception ex)
        {
            Log.Error($"Installing update {update.Version}", ex);
            return (false, "No connection");
        }
    }

    static bool IsNewer(string version) => Version.TryParse(version, out var found) && found > Trim(Current);

    /// <summary>1.2.3.0 → 1.2.3 (assembly versions carry a fourth part, release tags don't).</summary>
    static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    static HttpClient CreateClient()
    {
        // Limits per call: a few seconds for the check, minutes for the download.
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ClawControl/{Current.ToString(3)}");
        return client;
    }
}
