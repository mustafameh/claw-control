using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClawControl.Core;
using Windows.Media.Control;

namespace ClawControl.Hardware;

public sealed record MediaInfo(string Title, string Artist, string App, bool Playing, bool CanPrevious, bool CanNext, ImageSource? Art);

/// <summary>
/// What's playing (Spotify, a YouTube tab, ...) through Windows' media sessions, the same source
/// as the media card in Windows' own volume flyout. Windows tracks it anyway; we only ask while
/// the panel is open and listen for track changes only then. Nothing is polled.
/// </summary>
public sealed class MediaService
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _watched;
    bool _listening;
    int _version;
    string? _artKey;
    ImageSource? _art;

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
        }
        _watched = session;
        if (session != null)
        {
            session.MediaPropertiesChanged += OnMediaChanged;
            session.PlaybackInfoChanged += OnPlaybackChanged;
        }
    }

    void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = RefreshAsync();
    void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ = RefreshAsync();

    async Task RefreshAsync()
    {
        int version = Interlocked.Increment(ref _version);
        MediaInfo? info = null;
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
                    // The cover is only fetched when the track changes, and kept small.
                    string artKey = $"{session.SourceAppUserModelId}|{title}|{artist}";
                    if (artKey != _artKey)
                    {
                        _artKey = artKey;
                        _art = await LoadArtAsync(props);
                    }
                    info = new MediaInfo(title, artist, AppName(session.SourceAppUserModelId),
                        playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        playback?.Controls.IsPreviousEnabled ?? false,
                        playback?.Controls.IsNextEnabled ?? false,
                        _art);
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
    }

    static async Task<ImageSource?> LoadArtAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail == null) return null;
        try
        {
            using var winStream = await props.Thumbnail.OpenReadAsync();
            using var stream = winStream.AsStreamForRead();
            var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            memory.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = memory;
            image.DecodePixelWidth = 96;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Error("Loading album art", ex);
            return null;
        }
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
