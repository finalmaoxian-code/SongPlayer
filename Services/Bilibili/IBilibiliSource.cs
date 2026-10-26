namespace SongPlayer.Services.Bilibili;

/// <summary>
/// Turns a Bilibili video id into a playable audio stream together with the metadata that came
/// along with it.
/// </summary>
/// <remarks>
/// An interface rather than a concrete class so the shell can be exercised without a running
/// bridge, and so a second online source can implement the same contract later.
/// </remarks>
public interface IBilibiliSource
{
    /// <summary>
    /// Checks whether the local bridge is reachable.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the bridge answers its health check.</returns>
    /// <remarks>
    /// Called before resolving so a missing bridge produces "start the bridge" instead of a
    /// twenty-second timeout buried in an unrelated error.
    /// </remarks>
    Task<bool> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves <paramref name="bvid"/> into a playable stream and its metadata.
    /// </summary>
    /// <param name="bvid">Bilibili video id, e.g. <c>BV1GJ411x7h7</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The audio URL, title, uploader, cover and any readable subtitles.</returns>
    /// <exception cref="BilibiliSourceException">The id is malformed or no audio could be resolved.</exception>
    /// <remarks>
    /// Metadata is best-effort on the bridge side: a video whose details cannot be read still
    /// resolves, in which case the title falls back to the id and the artwork to <c>null</c>. Only
    /// a missing audio stream is treated as a failure.
    /// </remarks>
    Task<ResolvedMedia> ResolveAsync(string bvid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches Bilibili for videos matching <paramref name="keyword"/>.
    /// </summary>
    /// <param name="keyword">Free text: a song name, an artist, or both.</param>
    /// <param name="limit">Maximum number of candidates to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Candidate videos in the order the endpoint ranked them.</returns>
    /// <exception cref="BilibiliSourceException">The bridge is missing or the search failed.</exception>
    /// <remarks>
    /// Returns candidates rather than a single answer on purpose. The search endpoint mixes official
    /// uploads, remasters, resolution badges and unrelated videos, and nothing in the payload says
    /// which one is the song, so the choice is left to the user. An empty list is a valid result.
    /// </remarks>
    Task<IReadOnlyList<BilibiliSearchResult>> SearchAsync(
        string keyword,
        int limit,
        CancellationToken cancellationToken = default);
}
