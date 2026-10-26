using SongPlayer.Models;

namespace SongPlayer.Services.Bilibili;

/// <summary>
/// One video returned by a keyword search, before it is resolved into a playable stream.
/// </summary>
/// <remarks>
/// Deliberately not a <see cref="Track"/>: a search hit has no audio URL and may never be played, and
/// turning every hit into a track would put rows in the UI that look playable but are not. The
/// conversion happens once, for the one candidate the user picked.
/// <para>
/// None of these fields are authoritative. The title is whatever the uploader typed — which is
/// frequently a remaster label, a resolution badge or a music-video caption rather than a song name —
/// and the uploader is an account, not an artist. That is exactly why search shows a list of
/// candidates instead of picking the first result: only a human can tell which upload is the song.
/// </para>
/// </remarks>
/// <param name="Bvid">Video id, the value that has to be resolved to get audio.</param>
/// <param name="Title">Video title with the search endpoint's highlight markup removed.</param>
/// <param name="Uploader">Account that uploaded the video.</param>
/// <param name="Duration">Video length, or <see cref="TimeSpan.Zero"/> when it could not be read.</param>
/// <param name="CoverUri">Thumbnail URL, or <see langword="null"/> when the result carried none.</param>
/// <param name="Description">Short description as returned by search; often empty.</param>
public sealed record BilibiliSearchResult(
    string Bvid,
    string Title,
    string Uploader,
    TimeSpan Duration,
    Uri? CoverUri,
    string Description)
{
    /// <summary>
    /// Length as <c>m:ss</c>, ready for the candidate list.
    /// </summary>
    public string DurationText => TimeSpanFormatting.ToClock(Duration);

    /// <summary>
    /// True when the thumbnail can be shown, so the row can fall back to a glyph without a
    /// converter in the template.
    /// </summary>
    public bool HasCover => CoverUri is not null;

    /// <summary>
    /// Tooltip combining everything the row does not have space for.
    /// </summary>
    /// <remarks>
    /// The description is the field that most often reveals what a hit actually is ("full album
    /// version", "live at …"), so it is kept reachable rather than dropped after being parsed.
    /// </remarks>
    public string Tooltip => string.IsNullOrWhiteSpace(Description)
        ? $"{Title} · {Uploader} · {Bvid}"
        : $"{Title}\n{Bvid} · {Uploader}\n\n{Description}";
}
