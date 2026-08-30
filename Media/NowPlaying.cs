using System.Windows.Media.Imaging;

namespace Slate.Media;

public sealed class NowPlaying
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public bool IsPlaying { get; init; }
    public TimeSpan Duration { get; init; }
    public TimeSpan Elapsed { get; init; }
    /// <summary>Whatever Windows calls the owning app, e.g. "Spotify.exe" or a browser AUMID.</summary>
    public string SourceId { get; init; } = "";
    public string SourceName { get; init; } = "";
    public BitmapSource? Artwork { get; init; }

    public bool HasContent => !string.IsNullOrWhiteSpace(Title);

    /// <summary>Identity for "is this still the same track", ignoring the ticking clock.</summary>
    public string TrackKey => $"{SourceId}|{Title}|{Artist}|{Album}";

    public double Progress =>
        Duration.TotalSeconds > 0
            ? Math.Clamp(Elapsed.TotalSeconds / Duration.TotalSeconds, 0, 1)
            : 0;

    public static readonly NowPlaying Empty = new();
}

public static class TimeFormat
{
    /// <summary>"3:07" — the only time format the panel ever shows.</summary>
    public static string Clock(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }
}
