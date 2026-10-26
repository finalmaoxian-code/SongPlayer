namespace SongPlayer.Models;

/// <summary>
/// Represents a single playable track.
/// Kept intentionally minimal for now; it will grow when the audio engine and the
/// local library are implemented.
/// </summary>
/// <param name="Title">Display title of the track.</param>
/// <param name="Artist">Performing artist, or an empty string when unknown.</param>
/// <param name="Duration">Total length of the track.</param>
public sealed record Track(string Title, string Artist, TimeSpan Duration)
{
    /// <summary>
    /// Placeholder used by the player bar when nothing is loaded.
    /// </summary>
    public static Track Empty { get; } = new("Nothing playing", "—", TimeSpan.Zero);

    /// <summary>
    /// Demo track loaded at start-up so the player bar and the Now Playing page can be
    /// reviewed before the audio engine and the library scanner exist. It carries no audio
    /// data and is dropped as soon as real playback arrives.
    /// </summary>
    public static Track Sample { get; } = new("Midnight Drive", "The Wanderers", TimeSpan.FromSeconds(212));
}
