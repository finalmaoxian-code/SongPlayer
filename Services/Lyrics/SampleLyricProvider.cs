using SongPlayer.Models;

namespace SongPlayer.Services.Lyrics;

/// <summary>
/// Stand-in provider that returns the built-in demo lyrics for the sample track.
/// </summary>
/// <remarks>
/// This exists so the karaoke highlight on the Now Playing page can be reviewed before the
/// <c>.lrc</c> parser lands. It only answers for the demo track; real files get no lyrics
/// yet, which is what the empty hint in the overlay is for.
/// </remarks>
public sealed class SampleLyricProvider : ILyricProvider
{
    /// <summary>
    /// Timestamped lines belonging to the sample track.
    /// </summary>
    private static readonly LyricLine[] SampleLines =
    {
        new(TimeSpan.FromSeconds(0), "City lights are running through the glass"),
        new(TimeSpan.FromSeconds(12), "Every mile another colour passes"),
        new(TimeSpan.FromSeconds(24), "Radio is humming low and slow"),
        new(TimeSpan.FromSeconds(36), "I don't need a place to go"),
        new(TimeSpan.FromSeconds(48), "Midnight drive, we're chasing nothing"),
        new(TimeSpan.FromSeconds(60), "Headlights on an empty road"),
        new(TimeSpan.FromSeconds(72), "Midnight drive, the engine humming"),
        new(TimeSpan.FromSeconds(84), "Carrying a lighter load"),
        new(TimeSpan.FromSeconds(96), "Morning finds us somewhere further out"),
        new(TimeSpan.FromSeconds(108), "Every turn another thing to doubt"),
        new(TimeSpan.FromSeconds(120), "But the wheel feels good beneath my hands"),
        new(TimeSpan.FromSeconds(132), "And the skyline understands"),
        new(TimeSpan.FromSeconds(144), "Midnight drive, we're chasing nothing"),
        new(TimeSpan.FromSeconds(156), "Headlights on an empty road"),
        new(TimeSpan.FromSeconds(168), "Midnight drive, the engine humming"),
        new(TimeSpan.FromSeconds(180), "Carrying a lighter load"),
    };

    /// <inheritdoc />
    public IReadOnlyList<LyricLine> GetLyrics(Track track)
        => ReferenceEquals(track, Track.Sample) ? SampleLines : [];
}
