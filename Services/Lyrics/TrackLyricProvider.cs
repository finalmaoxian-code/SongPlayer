using SongPlayer.Models;

namespace SongPlayer.Services.Lyrics;

/// <summary>
/// Lyric source used by the shell: archived stream subtitles first, then whatever the fallback
/// provider knows.
/// </summary>
/// <remarks>
/// Online tracks get their lines from <see cref="CaptionStore"/>, which holds the subtitles the
/// bridge read on an earlier play. Everything else falls through to the wrapped provider — today
/// the sample lyrics, later a <c>.lrc</c> reader for local files.
/// <para>
/// Composition instead of a second interface: the overlay only ever wants "the lines for this
/// track", and pushing source selection into the provider keeps <see cref="ILyricProvider"/>
/// unchanged for every caller.
/// </para>
/// </remarks>
public sealed class TrackLyricProvider : ILyricProvider
{
    private readonly CaptionStore _captions;
    private readonly ILyricProvider _fallback;

    /// <summary>
    /// Creates the provider.
    /// </summary>
    /// <param name="captions">Archive of the subtitles read for online tracks.</param>
    /// <param name="fallback">Provider consulted when the archive has nothing.</param>
    public TrackLyricProvider(CaptionStore captions, ILyricProvider fallback)
    {
        _captions = captions;
        _fallback = fallback;
    }

    /// <inheritdoc />
    public IReadOnlyList<LyricLine> GetLyrics(Track track)
    {
        if (track.IsStream && track.StreamKey is { } streamKey)
        {
            IReadOnlyList<LyricLine> archived = _captions.Load(streamKey);

            if (archived.Count > 0)
            {
                return archived;
            }
        }

        return _fallback.GetLyrics(track);
    }
}
