using SongPlayer.Models;

namespace SongPlayer.Services.Lyrics;

/// <summary>
/// The demo lyrics, shown only for the built-in sample track.
/// </summary>
/// <remarks>
/// This exists so the design-time preview and the sample track have something to render. It must never
/// answer for a real track: pretending a real song has lyrics would hide exactly the failure the user
/// needs to see.
/// </remarks>
public static class SampleLyricProvider
{
    /// <summary>The demo lines.</summary>
    private static readonly LyricLine[] Lines =
    [
        new(TimeSpan.FromSeconds(0), "City lights are running through the glass"),
        new(TimeSpan.FromSeconds(12), "Every street is humming like a wire"),
        new(TimeSpan.FromSeconds(24), "We were only passing through the dark"),
        new(TimeSpan.FromSeconds(36), "Carrying a little of the fire"),
        new(TimeSpan.FromSeconds(48), "Midnight drive, the radio is low"),
        new(TimeSpan.FromSeconds(60), "Telling us the things we used to know"),
        new(TimeSpan.FromSeconds(72), "Keep the window open for the sound"),
        new(TimeSpan.FromSeconds(84), "Nothing left to hold us to the ground"),
        new(TimeSpan.FromSeconds(96), "Every light is fading into grey"),
        new(TimeSpan.FromSeconds(108), "Morning coming up the other way"),
        new(TimeSpan.FromSeconds(120), "Midnight drive, the radio is low"),
        new(TimeSpan.FromSeconds(132), "Telling us the things we used to know"),
        new(TimeSpan.FromSeconds(144), "Keep the window open for the sound"),
        new(TimeSpan.FromSeconds(156), "Nothing left to hold us to the ground"),
        new(TimeSpan.FromSeconds(168), "Nothing left to hold us to the ground"),
        new(TimeSpan.FromSeconds(180), "Hold us to the ground"),
    ];

    /// <summary>
    /// Returns the demo lyrics when the track is the sample track, otherwise nothing.
    /// </summary>
    public static IReadOnlyList<LyricLine> For(Track track) =>
        ReferenceEquals(track, Track.Sample) ? Lines : [];
}
