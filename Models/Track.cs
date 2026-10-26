namespace SongPlayer.Models;

/// <summary>
/// Represents a single playable track.
/// Kept intentionally minimal for now; it will grow when the audio engine and the
/// local library are implemented.
/// </summary>
/// <param name="Title">Display title of the track.</param>
/// <param name="Artist">Performing artist, or an empty string when unknown.</param>
/// <param name="Duration">Total length of the track.</param>
/// <param name="FilePath">
/// Absolute path of the audio file on disk, or <see langword="null"/> for the demo track
/// that carries no audio data. This is also what the library is persisted as, so a track
/// without a path is never written to <c>library.json</c>.
/// </param>
public sealed record Track(
    string Title,
    string Artist,
    TimeSpan Duration,
    string? FilePath = null)
{
    /// <summary>
    /// True when the track is backed by a real file rather than placeholder data.
    /// </summary>
    public bool IsPlayable => !string.IsNullOrWhiteSpace(FilePath);

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
