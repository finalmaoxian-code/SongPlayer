using System.Collections.Generic;

namespace SongPlayer.Models;

/// <summary>
/// Placeholder lyrics used by the Now Playing page while no lyric parser exists yet.
/// </summary>
/// <remarks>
/// The eventual implementation will read an <c>.lrc</c> file stored next to the audio file.
/// Because the view only consumes a list of <see cref="LyricLine"/>, swapping the source for
/// a real parser later requires no change to the view layer.
/// </remarks>
public static class SampleLyrics
{
    /// <summary>
    /// Timestamped lines belonging to the sample track, used to demonstrate the karaoke
    /// highlight on the Now Playing page.
    /// </summary>
    public static IReadOnlyList<LyricLine> ForSampleTrack { get; } = new[]
    {
        new LyricLine(TimeSpan.FromSeconds(0), "City lights are running through the glass"),
        new LyricLine(TimeSpan.FromSeconds(12), "Every mile another colour passes"),
        new LyricLine(TimeSpan.FromSeconds(24), "Radio is humming low and slow"),
        new LyricLine(TimeSpan.FromSeconds(36), "I don't need a place to go"),
        new LyricLine(TimeSpan.FromSeconds(48), "Midnight drive, we're chasing nothing"),
        new LyricLine(TimeSpan.FromSeconds(60), "Headlights on an empty road"),
        new LyricLine(TimeSpan.FromSeconds(72), "Midnight drive, the engine humming"),
        new LyricLine(TimeSpan.FromSeconds(84), "Carrying a lighter load"),
        new LyricLine(TimeSpan.FromSeconds(96), "Morning finds us somewhere further out"),
        new LyricLine(TimeSpan.FromSeconds(108), "Every turn another thing to doubt"),
        new LyricLine(TimeSpan.FromSeconds(120), "But the wheel feels good beneath my hands"),
        new LyricLine(TimeSpan.FromSeconds(132), "And the skyline understands"),
        new LyricLine(TimeSpan.FromSeconds(144), "Midnight drive, we're chasing nothing"),
        new LyricLine(TimeSpan.FromSeconds(156), "Headlights on an empty road"),
        new LyricLine(TimeSpan.FromSeconds(168), "Midnight drive, the engine humming"),
        new LyricLine(TimeSpan.FromSeconds(180), "Carrying a lighter load"),
    };
}
