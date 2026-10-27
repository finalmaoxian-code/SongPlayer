using SongPlayer.Models;
using SongPlayer.Services.Bilibili;

namespace SongPlayer.Services.Lyrics;

/// <summary>
/// Options for one lyric lookup.
/// </summary>
/// <param name="ForceRefresh">Ignore cached content and ask the database again.</param>
/// <param name="RecordId">Specific record to use, chosen by the user.</param>
/// <param name="ClearManualChoice">Forget a previously chosen record before looking up.</param>
public sealed record LyricLookupOptions(
    bool ForceRefresh = false,
    long? RecordId = null,
    bool ClearManualChoice = false)
{
    /// <summary>Default options: use the cache when it is valid.</summary>
    public static LyricLookupOptions Default { get; } = new();
}

/// <summary>
/// Finds lyrics for a track.
/// </summary>
/// <remarks>
/// Asynchronous and cancellable because a lookup crosses the network, and every answer is a
/// <see cref="LyricLookupResult"/> rather than a list, so "no lyrics", "instrumental", "not sure which
/// song this is" and "the database was unreachable" stay distinguishable all the way to the screen.
/// Implementations must never throw for ordinary failure and must never touch the UI thread.
/// </remarks>
public interface ILyricProvider
{
    /// <summary>
    /// Looks up lyrics using the default options.
    /// </summary>
    Task<LyricLookupResult> GetLyricsAsync(Track track, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up lyrics with explicit options, used for re-matching and for applying a manual choice.
    /// </summary>
    Task<LyricLookupResult> GetLyricsAsync(
        Track track,
        LyricLookupOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the stored lyric time offset for a track, in milliseconds. Positive shifts lyrics later.
    /// </summary>
    Task<int> GetOffsetMillisecondsAsync(Track track, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the lyric time offset for a track, in milliseconds.
    /// </summary>
    Task SetOffsetMillisecondsAsync(Track track, int milliseconds, CancellationToken cancellationToken = default);
}

/// <summary>
/// Small on-disk archive of the timed subtitles read for an online source.
/// </summary>
/// <remarks>
/// Extracted as an interface so the subtitle path can be exercised without touching
/// <c>%LOCALAPPDATA%</c>, and so the lyric provider states exactly what it needs from the archive
/// rather than depending on the concrete store. Implementations must never throw for ordinary IO
/// problems: a subtitle archive is a nicety, and a read-only profile must not stop a track playing.
/// </remarks>
public interface ICaptionArchive
{
    /// <summary>Reads the archived subtitles of a stream key, or an empty list when none exist.</summary>
    IReadOnlyList<LyricLine> Load(string streamKey);

    /// <summary>Stores subtitles for a stream key, ignoring empty input.</summary>
    Task SaveAsync(string streamKey, IReadOnlyList<BilibiliCaption> captions);
}
