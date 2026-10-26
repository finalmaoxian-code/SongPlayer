using SongPlayer.Models;

namespace SongPlayer.Services.Library;

/// <summary>
/// The library facade the view models talk to: scan a folder, restore the saved library and
/// persist it again.
/// </summary>
public interface IMusicLibraryService
{
    /// <summary>
    /// Scans a folder and reads the tags of every supported file it contains.
    /// </summary>
    /// <param name="folder">Root folder to scan.</param>
    /// <returns>
    /// One track per readable file. Missing or unreadable files are skipped, and the array is
    /// empty when the folder itself does not exist.
    /// </returns>
    Task<IReadOnlyList<Track>> ScanAsync(string folder);

    /// <summary>
    /// Restores the tracks that were saved by a previous session.
    /// </summary>
    /// <remarks>
    /// A restored online track has no <see cref="Track.StreamUri"/> yet: the URL expires, so it is
    /// re-resolved from <see cref="Track.StreamKey"/> the first time the track is played.
    /// </remarks>
    Task<IReadOnlyList<Track>> LoadAsync();

    /// <summary>
    /// Saves the library so the next session can restore it.
    /// </summary>
    /// <param name="tracks">
    /// Tracks to persist. Local files are stored as their path; online tracks as their stream key
    /// plus the metadata read the last time they were resolved.
    /// </param>
    Task SaveAsync(IEnumerable<Track> tracks);
}
