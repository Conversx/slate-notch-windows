using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Slate.Support;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Slate.Media;

/// <summary>
/// Now Playing, straight from Windows.
/// </summary>
/// <remarks>
/// This is the part of the port that is simply better than the Mac original. Windows
/// publishes one system-wide session API — <c>GlobalSystemMediaTransportControlsSessionManager</c>
/// — that reports title, artist, artwork, position and playback state for *every* app
/// that registers transport controls: Spotify, Chrome, Edge, everything. It is public,
/// documented, and event-driven.
///
/// The macOS build needs a private framework Apple has gated, per-app AppleScript for
/// Spotify and Music, and JavaScript evaluated inside browser tabs behind a setting the
/// user has to switch on by hand. None of that exists here.
/// </remarks>
public sealed class MediaModel : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    /// <summary>Only runs while the panel is open — the scrubber is the one thing that
    /// needs a clock, and nothing on screen depends on it while the panel is shut.</summary>
    private readonly DispatcherTimer _tick;

    private NowPlaying _current = NowPlaying.Empty;
    private Color _accent = Colors.Black;
    private string _lastArtworkKey = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public NowPlaying Current
    {
        get => _current;
        private set { _current = value; Raise(); Raise(nameof(HasContent)); }
    }

    /// <summary>Colour pulled from the artwork. Black while nothing is playing, so an
    /// idle bar glows dark rather than announcing itself in a colour that belongs to no
    /// track.</summary>
    public Color Accent
    {
        get => _accent;
        private set { if (_accent != value) { _accent = value; Raise(); } }
    }

    public bool HasContent => _current.HasContent;

    public MediaModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => RefreshTimelineOnly();
    }

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            Log($"session manager unavailable: {ex.Message}");
            return;
        }

        _manager.CurrentSessionChanged += (_, _) => OnUi(AttachToCurrentSession);
        AttachToCurrentSession();
    }

    /// <summary>Scrubber accuracy costs a timer, so only pay for it while it is visible.</summary>
    public void SetPanelOpen(bool open)
    {
        if (open)
        {
            _tick.Start();
            _ = RefreshAsync();
        }
        else
        {
            _tick.Stop();
        }
    }

    // MARK: - Session plumbing

    private void AttachToCurrentSession()
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        _session = _manager?.GetCurrentSession();

        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
        }

        _ = RefreshAsync();
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e)
        => OnUi(() => _ = RefreshAsync());

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e)
        => OnUi(() => _ = RefreshAsync());

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e)
        => OnUi(RefreshTimelineOnly);

    private async Task RefreshAsync()
    {
        var session = _session;
        if (session is null)
        {
            OnUi(() => { Current = NowPlaying.Empty; Accent = Colors.Black; _lastArtworkKey = ""; });
            return;
        }

        GlobalSystemMediaTransportControlsSessionMediaProperties props;
        try
        {
            props = await session.TryGetMediaPropertiesAsync();
        }
        catch (Exception ex)
        {
            Log($"media properties failed: {ex.Message}");
            return;
        }

        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();

        var snapshot = new NowPlaying
        {
            Title = props.Title ?? "",
            Artist = props.Artist ?? "",
            Album = props.AlbumTitle ?? "",
            IsPlaying = playback?.PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Duration = timeline.EndTime - timeline.StartTime,
            Elapsed = timeline.Position - timeline.StartTime,
            SourceId = session.SourceAppUserModelId ?? "",
            SourceName = FriendlyName(session.SourceAppUserModelId ?? ""),
            Artwork = _current.TrackKeyMatches(props, session) ? _current.Artwork : null
        };

        BitmapSource? art = snapshot.Artwork;
        if (art is null && props.Thumbnail is not null)
        {
            art = await LoadThumbnailAsync(props.Thumbnail);
        }

        OnUi(() =>
        {
            var final = new NowPlaying
            {
                Title = snapshot.Title,
                Artist = snapshot.Artist,
                Album = snapshot.Album,
                IsPlaying = snapshot.IsPlaying,
                Duration = snapshot.Duration,
                Elapsed = snapshot.Elapsed,
                SourceId = snapshot.SourceId,
                SourceName = snapshot.SourceName,
                Artwork = art
            };
            Current = final;

            if (final.TrackKey != _lastArtworkKey)
            {
                _lastArtworkKey = final.TrackKey;
                Accent = final.HasContent
                    ? (art is not null ? AccentExtractor.From(art) : AccentExtractor.Fallback)
                    : Colors.Black;
            }
        });
    }

    /// <summary>Position moves every second; nothing else does. Re-reading the whole
    /// session for that would throw the artwork away once a second.</summary>
    private void RefreshTimelineOnly()
    {
        var session = _session;
        if (session is null || !_current.HasContent) return;

        var timeline = session.GetTimelineProperties();
        var playback = session.GetPlaybackInfo();

        Current = new NowPlaying
        {
            Title = _current.Title,
            Artist = _current.Artist,
            Album = _current.Album,
            IsPlaying = playback?.PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Duration = timeline.EndTime - timeline.StartTime,
            Elapsed = timeline.Position - timeline.StartTime,
            SourceId = _current.SourceId,
            SourceName = _current.SourceName,
            Artwork = _current.Artwork
        };
    }

    // MARK: - Transport

    public async void Toggle()  => await Try(s => s.TryTogglePlayPauseAsync());
    public async void Next()    => await Try(s => s.TrySkipNextAsync());
    public async void Previous() => await Try(s => s.TrySkipPreviousAsync());

    public async void Seek(double fraction)
    {
        var session = _session;
        if (session is null || _current.Duration <= TimeSpan.Zero) return;
        var target = TimeSpan.FromSeconds(_current.Duration.TotalSeconds * Math.Clamp(fraction, 0, 1));
        await Try(s => s.TryChangePlaybackPositionAsync(target.Ticks));
    }

    private async Task Try(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> action)
    {
        var session = _session;
        if (session is null) return;
        try { await action(session); }
        catch (Exception ex) { Log($"transport failed: {ex.Message}"); }
        _ = RefreshAsync();
    }

    // MARK: - Artwork

    private static async Task<BitmapSource?> LoadThumbnailAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            var size = (uint)stream.Size;
            if (size == 0) return null;

            var reader = new DataReader(stream);
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);

            using var ms = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log($"thumbnail failed: {ex.Message}");
            return null;
        }
    }

    private static string FriendlyName(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return "";
        var name = sourceId;
        // AUMIDs look like "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic";
        // executables look like "Spotify.exe".
        var bang = name.IndexOf('!');
        if (bang > 0) name = name[..bang];
        var underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name.ToUpperInvariant();
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static void Log(string message) => Support.Diagnostics.Log($"media: {message}");

    public void Dispose()
    {
        _tick.Stop();
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
    }
}

internal static class NowPlayingExtensions
{
    /// <summary>Cheap check for "same track, so keep the artwork we already decoded".</summary>
    public static bool TrackKeyMatches(this NowPlaying current,
        GlobalSystemMediaTransportControlsSessionMediaProperties props,
        GlobalSystemMediaTransportControlsSession session)
        => current.HasContent
           && current.Title == (props.Title ?? "")
           && current.Artist == (props.Artist ?? "")
           && current.SourceId == (session.SourceAppUserModelId ?? "");
}
