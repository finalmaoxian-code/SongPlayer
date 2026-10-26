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
/// <param name="Album">
/// Album tag of the file, or <see langword="null"/> when the tag is missing or the track is
/// not backed by a file. Presentation code falls back to a neutral label.
/// </param>
/// <param name="TrackNumber">
/// One-based position of the track inside its album, or <see langword="null"/> when the tag
/// does not carry one.
/// </param>
/// <param name="StreamKey">
/// Stable identity of an online source, e.g. <c>bilibili:BV1GJ411x7h7</c>, or
/// <see langword="null"/> for local files. Unlike <paramref name="StreamUri"/> this never
/// expires, so it is what an online track is recognised by.
/// </param>
/// <param name="StreamUri">
/// Fully resolved, temporary URL the engine loads for an online source. Bilibili signs these
/// URLs with a deadline, so this is a snapshot that goes stale and must never be persisted;
/// it is re-resolved from <paramref name="StreamKey"/> before each playback.
/// </param>
public sealed record Track(
    string Title,
    string Artist,
    TimeSpan Duration,
    string? FilePath = null,
    string? Album = null,
    int? TrackNumber = null,
    string? StreamKey = null,
    string? StreamUri = null)
{
    /// <summary>
    /// True when the track can be handed to the audio engine, either as a file on disk or as an
    /// already-resolved online stream.
    /// </summary>
    public bool IsPlayable =>
        !string.IsNullOrWhiteSpace(FilePath) || !string.IsNullOrWhiteSpace(StreamUri);

    /// <summary>
    /// True when the track comes from an online source rather than a file on disk.
    /// </summary>
    public bool IsStream => !string.IsNullOrWhiteSpace(StreamKey);

    /// <summary>
    /// Track length as <c>m:ss</c>, ready for the library table.
    /// </summary>
    public string DurationText => TimeSpanFormatting.ToClock(Duration);

    /// <summary>
    /// Album tag, or an em dash when the file carries none.
    /// </summary>
    /// <remarks>
    /// Loose singles are the common case, and an empty table cell reads as a rendering bug, so the
    /// placeholder is produced here instead of by a converter per column.
    /// </remarks>
    public string AlbumText => string.IsNullOrWhiteSpace(Album) ? "—" : Album;

    /// <summary>
    /// Placeholder used by the player bar when nothing is loaded.
    /// </summary>
    public static Track Empty { get; } = new("Nothing playing", "—", TimeSpan.Zero);

    /// <summary>
    /// Demo track loaded at start-up when the library is empty, so the player bar and the Now
    /// Playing page can be reviewed without importing anything. It carries no audio data.
    /// </summary>
    public static Track Sample { get; } = new(
        "Midnight Drive",
        "The Wanderers",
        TimeSpan.FromSeconds(212),
        Album: "Night Roads",
        TrackNumber: 4);
}
