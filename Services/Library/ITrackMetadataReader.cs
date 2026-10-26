using SongPlayer.Models;

namespace SongPlayer.Services.Library;

/// <summary>
/// Reads the tags of an audio file into a <see cref="Track"/> without touching the file.
/// </summary>
public interface ITrackMetadataReader
{
    /// <summary>
    /// Reads one file.
    /// </summary>
    /// <param name="path">Absolute path of an audio file.</param>
    /// <returns>
    /// The track, or <see langword="null"/> when the file is missing, has an unsupported
    /// extension, or carries tags that cannot be read.
    /// </returns>
    Track? ReadTrack(string path);
}
