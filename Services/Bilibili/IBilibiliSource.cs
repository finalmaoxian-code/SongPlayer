namespace SongPlayer.Services.Bilibili;

/// <summary>
/// Turns a Bilibili video id into an audio URL the engine can play.
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
    /// Resolves the audio stream of <paramref name="bvid"/>.
    /// </summary>
    /// <param name="bvid">Bilibili video id, e.g. <c>BV1GJ411x7h7</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved id and its temporary audio URL.</returns>
    /// <exception cref="BilibiliSourceException">The id is malformed or no audio could be resolved.</exception>
    Task<ResolvedAudio> ResolveAsync(string bvid, CancellationToken cancellationToken = default);
}
