using SongPlayer.Models;

namespace SongPlayer.Services.Lyrics;

/// <summary>
/// Supplies the timed lyrics of a track.
/// </summary>
/// <remarks>
/// The view only ever consumes a list of <see cref="LyricLine"/>, so replacing the sample
/// provider with a real <c>.lrc</c> parser is a local change behind this interface.
/// </remarks>
public interface ILyricProvider
{
    /// <summary>
    /// Returns the lyrics of a track.
    /// </summary>
    /// <param name="track">Track whose lyrics are requested.</param>
    /// <returns>
    /// Timed lines in playback order, or an empty list when no lyrics are available.
    /// </returns>
    IReadOnlyList<LyricLine> GetLyrics(Track track);
}
