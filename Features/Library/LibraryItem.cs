using System.Text.Json.Serialization;

namespace ClawControl.Features.Library;

/// <summary>A game or app in the Library (saved in settings).</summary>
public sealed class LibraryItem
{
    public string Name { get; set; } = "";
    /// <summary>Where it came from: Steam, Epic Games, Xbox, Program or App.</summary>
    public string Source { get; set; } = "";
    /// <summary>What opens it: steam://…, com.epicgames.launcher://…, shell:AppsFolder\… or a program's path.</summary>
    public string Launch { get; set; } = "";
    /// <summary>Where it's installed, when known: a program running from here means it's running.</summary>
    public string? Folder { get; set; }
    /// <summary>Its program file, for the icon (and to tell it's running).</summary>
    public string? Program { get; set; }
    /// <summary>Its shell id (Start menu apps, Xbox games), for the icon and to tell it's running.</summary>
    public string? AppId { get; set; }
    /// <summary>Its cover picture on this Claw (Steam's art), or a square logo (Xbox).</summary>
    public string? Cover { get; set; }
    public bool SquareCover { get; set; }
    /// <summary>Where its cover can be downloaded (Epic keeps only links on the device); the
    /// picture is saved next to the settings and becomes <see cref="Cover"/>.</summary>
    public string? CoverUrl { get; set; }
    /// <summary>Steam's file about the game (it notes when it was last played).</summary>
    public string? Manifest { get; set; }
    public DateTime? LastPlayed { get; set; }

    [JsonIgnore] public string Key => Launch;
}
