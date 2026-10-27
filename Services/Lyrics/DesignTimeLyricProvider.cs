using SongPlayer.Models;

namespace SongPlayer.Services.Lyrics;

/// <summary>
/// Answers with the demo lyrics, without touching the network or the disk.
/// </summary>
/// <remarks>
/// Used only for the XAML design-time preview and for the shell's parameterless fallback. It never runs
/// in the composed application, and it must never answer for a real track: a real song that silently
/// received demo lyrics would look like a successful match while being wrong about every word.
/// </remarks>
public sealed class DesignTimeLyricProvider : ILyricProvider
{
    /// <inheritdoc />
    public Task<LyricLookupResult> GetLyricsAsync(Track track, CancellationToken cancellationToken = default) =>
        Task.FromResult(DesignTimeResult());

    /// <inheritdoc />
    public Task<LyricLookupResult> GetLyricsAsync(
        Track track,
        LyricLookupOptions options,
        CancellationToken cancellationToken = default) => Task.FromResult(DesignTimeResult());

    /// <inheritdoc />
    public Task<int> GetOffsetMillisecondsAsync(Track track, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    /// <inheritdoc />
    public Task SetOffsetMillisecondsAsync(Track track, int milliseconds, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Builds the demo result shown by the previewer.</summary>
    private static LyricLookupResult DesignTimeResult() =>
        LyricLookupResult.Synced(
            SampleLyricProvider.For(Track.Sample),
            LyricSourceKind.Sample,
            recordId: null,
            title: Track.Sample.Title,
            artist: Track.Sample.Artist,
            album: Track.Sample.Album,
            duration: Track.Sample.Duration,
            score: 100,
            explanation: "Design-time preview lyrics.",
            message: null);
}
