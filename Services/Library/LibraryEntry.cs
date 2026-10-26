using System.Text.Json.Serialization;
using SongPlayer.Models;

namespace SongPlayer.Services.Library;

/// <summary>
/// One row of <c>library.json</c>: either a path to a file on disk, or the durable identity of an
/// online source plus the metadata that was read the last time it was resolved.
/// </summary>
/// <remarks>
/// The split exists because the two kinds of track age differently. A local file is identified by
/// its path and its tags are cheap to re-read, so only the path is stored. An online track has no
/// path and its stream URL expires within hours, so only <see cref="StreamKey"/> is durable — but a
/// bare key would restore a list of empty rows, so the title, uploader, length and thumbnail are
/// kept alongside it as a *cache* that the next resolve overwrites. Nothing here is authoritative
/// except the key.
/// <para>
/// <see cref="StreamUri"/> is deliberately absent: a signed CDN URL that outlived its deadline is
/// worse than no URL at all, because it fails during playback instead of before it.
/// </para>
/// </remarks>
/// <param name="FilePath">Absolute path of a local audio file, or <see langword="null"/>.</param>
/// <param name="StreamKey">Stable online identity, e.g. <c>bilibili:BV1GJ411x7h7</c>, or <see langword="null"/>.</param>
/// <param name="Title">Last known title of an online track.</param>
/// <param name="Artist">Last known uploader of an online track.</param>
/// <param name="DurationSeconds">Last known length of an online track, in seconds.</param>
/// <param name="CoverUri">Last known artwork address of an online track.</param>
public sealed record LibraryEntry(
    string? FilePath,
    string? StreamKey,
    string? Title,
    string? Artist,
    int? DurationSeconds,
    string? CoverUri)
{
    /// <summary>
    /// Captures a track for storage.
    /// </summary>
    /// <param name="track">Track to store.</param>
    /// <returns>
    /// The entry, or <see langword="null"/> when the track is neither a file nor an online source
    /// (the demo track, for instance).
    /// </returns>
    public static LibraryEntry? FromTrack(Track track)
    {
        if (!string.IsNullOrWhiteSpace(track.FilePath))
        {
            return new LibraryEntry(track.FilePath, null, null, null, null, null);
        }

        if (!string.IsNullOrWhiteSpace(track.StreamKey))
        {
            return new LibraryEntry(
                null,
                track.StreamKey,
                track.Title,
                track.Artist,
                (int)Math.Round(track.Duration.TotalSeconds),
                track.CoverUri);
        }

        return null;
    }

    /// <summary>
    /// Rebuilds the track this entry describes.
    /// </summary>
    /// <returns>
    /// A track whose <see cref="Track.StreamUri"/> is still missing, because the URL has to be
    /// resolved again before it can be played.
    /// </returns>
    public Track ToTrack()
    {
        int duration = DurationSeconds ?? 0;

        return new Track(
            string.IsNullOrWhiteSpace(Title) ? $"Bilibili · {ShortId()}" : Title!,
            string.IsNullOrWhiteSpace(Artist) ? "Bilibili" : Artist!,
            duration > 0 ? TimeSpan.FromSeconds(duration) : TimeSpan.Zero,
            StreamKey: StreamKey,
            CoverUri: CoverUri);
    }

    /// <summary>
    /// True when this entry points at a file on disk.
    /// </summary>
    [JsonIgnore]
    public bool IsFile => !string.IsNullOrWhiteSpace(FilePath);

    /// <summary>
    /// Identity used to de-duplicate entries and to detect a rename-free update of the same track.
    /// </summary>
    [JsonIgnore]
    public string Key => IsFile ? FilePath! : StreamKey ?? string.Empty;

    /// <summary>
    /// Trailing part of the stream key, used for a readable fallback title.
    /// </summary>
    private string ShortId()
    {
        if (string.IsNullOrWhiteSpace(StreamKey))
        {
            return "unknown";
        }

        int separator = StreamKey.LastIndexOf(':');

        return separator >= 0 && separator < StreamKey.Length - 1
            ? StreamKey[(separator + 1)..]
            : StreamKey;
    }
}
