using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClawControl.Core;
using Windows.Media.Control;

namespace ClawControl.Hardware;

/// <summary>Where a track is: its position at a moment (UTC), its length, and whether the app takes seeks.</summary>
public sealed record MediaTimeline(TimeSpan Start, TimeSpan Position, DateTime At, TimeSpan Length, bool CanSeek)
{
    /// <summary>The position now: apps report it now and then, so while playing it's counted on from then.</summary>
    public TimeSpan Now(bool playing)
    {
        var position = playing ? Position + (DateTime.UtcNow - At) : Position;
        return position < TimeSpan.Zero ? TimeSpan.Zero : position > Length ? Length : position;
    }
}

/// <summary>The cover as shown: the picture, a blurred copy for the tile's glow, and its main colour (bright, for the bars).</summary>
public sealed record MediaCover(ImageSource Art, ImageSource Glow, Color Tint);

public sealed record MediaInfo(string Title, string Artist, string App, bool Playing, bool CanPrevious, bool CanNext,
    MediaCover? Cover, MediaTimeline? Timeline);

/// <summary>
/// What's playing (Spotify, a YouTube tab, ...) through Windows' media sessions, the same source
/// as the media card in Windows' own volume flyout. Windows tracks it anyway; we only ask while
/// the panel is open and listen for changes only then. Nothing is polled.
/// </summary>
public sealed class MediaService
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _watched;
    bool _listening;
    int _version;
    string? _trackKey;
    int _coverHash;
    MediaCover? _cover;

    /// <summary>Last known state, shown straight away when the panel opens. Null = nothing playing.</summary>
    public MediaInfo? Current { get; private set; }

    /// <summary>Current changed. Raised on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Connects to Windows' media sessions once, in the background after startup.</summary>
    public async Task InitializeAsync()
    {
        try { _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch (Exception ex) { Log.Error("Connecting to media sessions", ex); }
        if (_wanted) StartListening();  // panel opened before we were connected
    }

    bool _wanted;

    public void StartListening()
    {
        _wanted = true;
        if (_manager == null || _listening) return;
        _listening = true;
        _manager.CurrentSessionChanged += OnSessionChanged;
        Watch(_manager.GetCurrentSession());
        _ = RefreshAsync();
    }

    public void StopListening()
    {
        _wanted = false;
        if (_manager == null || !_listening) return;
        _listening = false;
        _manager.CurrentSessionChanged -= OnSessionChanged;
        Watch(null);
    }

    void OnSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        if (!_listening) return;
        Watch(sender.GetCurrentSession());
        _ = RefreshAsync();
    }

    void Watch(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_watched != null)
        {
            _watched.MediaPropertiesChanged -= OnMediaChanged;
            _watched.PlaybackInfoChanged -= OnPlaybackChanged;
            _watched.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        _watched = session;
        if (session != null)
        {
            session.MediaPropertiesChanged += OnMediaChanged;
            session.PlaybackInfoChanged += OnPlaybackChanged;
            session.TimelinePropertiesChanged += OnTimelineChanged;
        }
    }

    void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = RefreshAsync();
    // Play / pause and the position change often: only those are read again (the title and cover stay).
    void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => RefreshPlayback(s);
    void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => RefreshPlayback(s);

    async Task RefreshAsync()
    {
        int version = Interlocked.Increment(ref _version);
        MediaInfo? info = null;
        string? newTrack = null;
        try
        {
            var session = _manager?.GetCurrentSession();
            if (session != null)
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var playback = session.GetPlaybackInfo();
                string title = props?.Title ?? "";
                if (title.Length > 0)
                {
                    string artist = props!.Artist ?? "";
                    string key = $"{session.SourceAppUserModelId}|{title}|{artist}";
                    if (key != _trackKey)
                    {
                        _trackKey = newTrack = key;
                        _cover = null;
                    }
                    // Read every time the app says something changed: browsers often send the
                    // cover after the title (Brave in the background first sends its own icon).
                    // The same picture again is skipped once its bytes are read.
                    await LoadCoverAsync(props);
                    info = new MediaInfo(title, artist, AppName(session.SourceAppUserModelId),
                        IsPlaying(playback),
                        playback?.Controls.IsPreviousEnabled ?? false,
                        playback?.Controls.IsNextEnabled ?? false,
                        _cover, ReadTimeline(session, playback));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading what's playing", ex);
        }
        if (version != _version) return;  // a newer refresh is on its way
        Current = info;
        Changed?.Invoke();
        if (newTrack != null) _ = CheckCoverAgain(newTrack);
    }

    /// <summary>A new track: look at its cover again a little later, in case the app swapped it without saying.</summary>
    async Task CheckCoverAgain(string track)
    {
        foreach (int wait in new[] { 1500, 4000 })
        {
            await Task.Delay(wait);
            if (!_listening || track != _trackKey) return;
            await RefreshAsync();
        }
    }

    void RefreshPlayback(GlobalSystemMediaTransportControlsSession session)
    {
        if (!_listening) return;
        if (Current is not { } current)
        {
            _ = RefreshAsync();
            return;
        }
        try
        {
            var playback = session.GetPlaybackInfo();
            Current = current with
            {
                Playing = IsPlaying(playback),
                CanPrevious = playback?.Controls.IsPreviousEnabled ?? false,
                CanNext = playback?.Controls.IsNextEnabled ?? false,
                Timeline = ReadTimeline(session, playback),
            };
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Reading the play state", ex);
        }
    }

    static bool IsPlaying(GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback) =>
        playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    /// <summary>Null when the app gives no length (a live stream, some players).</summary>
    static MediaTimeline? ReadTimeline(GlobalSystemMediaTransportControlsSession session, GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback)
    {
        var timeline = session.GetTimelineProperties();
        if (timeline == null) return null;
        var length = timeline.EndTime - timeline.StartTime;
        if (length < TimeSpan.FromSeconds(1)) return null;
        // When the app last reported the position; a missing or odd time means "just now".
        var now = DateTime.UtcNow;
        var at = timeline.LastUpdatedTime.UtcDateTime;
        if (at > now || at < now.AddDays(-1)) at = now;
        bool canSeek = playback?.Controls.IsPlaybackPositionEnabled ?? false;
        return new MediaTimeline(timeline.StartTime, timeline.Position - timeline.StartTime, at, length, canSeek);
    }

    async Task LoadCoverAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail == null)
        {
            _cover = null;
            _coverHash = 0;
            return;
        }
        try
        {
            using var winStream = await props.Thumbnail.OpenReadAsync();
            using var stream = winStream.AsStreamForRead();
            var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            var hash = new HashCode();
            hash.AddBytes(memory.GetBuffer().AsSpan(0, (int)memory.Length));
            int code = hash.ToHashCode();
            if (code == _coverHash && _cover != null) return;
            _coverHash = code;
            _cover = MakeCover(memory);
        }
        catch (Exception ex)
        {
            Log.Error("Loading album art", ex);
        }
    }

    const int GlowSize = 24;

    /// <summary>The cover small enough for the tile (expanded 84 units, ~126 px on the Claw), a
    /// blurred 24 px copy that the tile stretches into its glow, and the cover's main colour.
    /// Done once per picture, never per frame.</summary>
    static MediaCover MakeCover(MemoryStream memory)
    {
        memory.Position = 0;
        var art = Decode(memory, 192);
        memory.Position = 0;
        var small = new FormatConvertedBitmap(Decode(memory, GlowSize), PixelFormats.Bgra32, null, 0);
        int width = small.PixelWidth, height = small.PixelHeight;
        var pixels = new byte[width * height * 4];
        small.CopyPixels(pixels, width * 4, 0);
        var tint = MainColour(pixels);
        // Blurred twice with a small box: smooth once stretched over the whole tile.
        for (int pass = 0; pass < 2; pass++) BoxBlur(pixels, width, height, 3);
        var glow = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        glow.Freeze();
        return new MediaCover(art, glow, tint);
    }

    static BitmapSource Decode(Stream stream, int width)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.DecodePixelWidth = width;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    static readonly Color SoftWhite = Color.FromRgb(0xE6, 0xE8, 0xEE);

    /// <summary>The cover's most colourful common colour, made bright enough to show on the dark
    /// glass (the bars). A grey cover gives a soft white.</summary>
    static Color MainColour(byte[] bgra)
    {
        double r = 0, g = 0, b = 0, total = 0;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            double pb = bgra[i] / 255.0, pg = bgra[i + 1] / 255.0, pr = bgra[i + 2] / 255.0;
            double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
            double saturation = max <= 0 ? 0 : (max - min) / max;
            // Colourful and not too dark counts most.
            double weight = 0.05 + saturation * saturation * max;
            r += pr * weight; g += pg * weight; b += pb * weight; total += weight;
        }
        if (total <= 0) return SoftWhite;
        RgbToHsl(r / total, g / total, b / total, out double h, out double s, out _);
        if (s < 0.12) return SoftWhite;
        return HslToRgb(h, Math.Clamp(s * 1.3, 0.6, 1), 0.66);
    }

    /// <summary>Box blur, across then down, edges repeated.</summary>
    static void BoxBlur(byte[] bgra, int width, int height, int radius)
    {
        var copy = (byte[])bgra.Clone();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int d = -radius; d <= radius; d++)
                        sum += copy[(y * width + Math.Clamp(x + d, 0, width - 1)) * 4 + c];
                    bgra[(y * width + x) * 4 + c] = (byte)(sum / (2 * radius + 1));
                }
        Array.Copy(bgra, copy, bgra.Length);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int d = -radius; d <= radius; d++)
                        sum += copy[(Math.Clamp(y + d, 0, height - 1) * width + x) * 4 + c];
                    bgra[(y * width + x) * 4 + c] = (byte)(sum / (2 * radius + 1));
                }
    }

    static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        double d = max - min;
        if (d < 1e-6) { h = s = 0; return; }
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h /= 6;
    }

    static Color HslToRgb(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Hue(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1 / 2.0) return q;
            if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
            return p;
        }
        byte Channel(double t) => (byte)Math.Round(Math.Clamp(Hue(t), 0, 1) * 255);
        return Color.FromRgb(Channel(h + 1 / 3.0), Channel(h), Channel(h - 1 / 3.0));
    }

    /// <summary>"Spotify.exe" / "SpotifyAB.SpotifyMusic_x!Spotify" / "Brave" -> "Spotify" / "Brave".</summary>
    static string AppName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        string name = id[(id.LastIndexOf('!') + 1)..];
        name = Path.GetFileNameWithoutExtension(name);
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : "";
    }

    public void PlayPause() => Send(s => s.TryTogglePlayPauseAsync());
    public void Previous() => Send(s => s.TrySkipPreviousAsync());
    public void Next() => Send(s => s.TrySkipNextAsync());

    /// <summary>Jumps to a spot in the track (from its start). Shown there at once; the app confirms later.</summary>
    public void Seek(TimeSpan position)
    {
        if (Current is not { Timeline: { CanSeek: true } timeline } current) return;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > timeline.Length) position = timeline.Length;
        Current = current with { Timeline = timeline with { Position = position, At = DateTime.UtcNow } };
        Changed?.Invoke();
        long ticks = (timeline.Start + position).Ticks;
        Send(s => s.TryChangePlaybackPositionAsync(ticks));
    }

    void Send(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> command)
    {
        var session = _manager?.GetCurrentSession();
        if (session == null) return;
        Task.Run(async () =>
        {
            try { await command(session); }
            catch (Exception ex) { Log.Error("Media control", ex); }
        });
    }
}
