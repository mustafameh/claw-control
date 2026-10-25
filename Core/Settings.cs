using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClawControl.Core;

/// <summary>User choices that survive restarts, stored as JSON next to the log.</summary>
public sealed class Settings
{
    public string Mode { get; set; } = "Custom";
    public int CustomSpl { get; set; } = 20;
    public int CustomSppt { get; set; } = 33;
    public int CustomFppt { get; set; } = 43;
    public bool TakeOverMsiButton { get; set; } = true;
    public bool OverlayEnabled { get; set; }
    public string OverlayPosition { get; set; } = "TopLeft";
    public List<string> OverlayItems { get; set; } = new(Monitoring.Metrics.Defaults);
    public string LastPage { get; set; } = "performance";
    /// <summary>The user turned the built-in controller off (it's turned back on while the panel is open).</summary>
    public bool ControllerOff { get; set; }
    /// <summary>Last controller mode seen outside the panel (chip value: 1 Xbox, 2 PlayStation, 4 Desktop).</summary>
    public int ControllerMode { get; set; } = 1;
    /// <summary>The Triggers screen edits LT and RT together.</summary>
    public bool TriggersTogether { get; set; } = true;
    /// <summary>Sharpening strength to go back to when the Sharpen circle turns it on again.</summary>
    public int Sharpness { get; set; } = 50;
    public Dictionary<string, string> LastFocusByPage { get; set; } = new();
    /// <summary>Log every press, key and command too (for bug reports; see Log.Detail).</summary>
    public bool DetailedLog { get; set; }

    [JsonIgnore] public static bool Exists => File.Exists(FilePath);

    static readonly string FilePath = Path.Combine(Log.Dir, "settings.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    static readonly object Gate = new();
    Timer? _saveTimer;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Error("Reading settings failed, using defaults", ex);
        }
        return new Settings();
    }

    /// <summary>Saves on a background thread, coalescing bursts such as a slider being dragged.</summary>
    public void SaveSoon()
    {
        _saveTimer ??= new Timer(_ => SaveNow());
        _saveTimer.Change(500, Timeout.Infinite);
    }

    public void SaveNow()
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, Options));
                File.Move(FilePath + ".tmp", FilePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
        }
    }
}
