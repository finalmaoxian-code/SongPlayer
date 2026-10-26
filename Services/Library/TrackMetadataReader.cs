using System.IO;
using SongPlayer.Models;

namespace SongPlayer.Services.Library;

/// <summary>
/// TagLib-backed metadata reader. It only ever opens the file for reading.
/// </summary>
/// <remarks>
/// A single damaged file must never abort a library scan, so every recoverable failure is
/// converted into a <see langword="null"/> result rather than an exception. The filter is
/// explicit instead of a blanket <c>catch</c> so genuine programming errors still surface.
/// </remarks>
public sealed class TrackMetadataReader : ITrackMetadataReader
{
    private readonly IAudioFileScanner _scanner;

    /// <summary>
    /// Creates the reader.
    /// </summary>
    /// <param name="scanner">Used to reject files whose extension is not supported.</param>
    public TrackMetadataReader(IAudioFileScanner scanner)
    {
        _scanner = scanner;
    }

    /// <inheritdoc />
    public Track? ReadTrack(string path)
    {
        try
        {
            if (!File.Exists(path) || !_scanner.IsSupported(path))
            {
                return null;
            }

            string fullPath = Path.GetFullPath(path);

            using var audio = TagLib.File.Create(fullPath);

            string title = string.IsNullOrWhiteSpace(audio.Tag.Title)
                ? Path.GetFileNameWithoutExtension(fullPath)
                : audio.Tag.Title.Trim();

            string artist = string.IsNullOrWhiteSpace(audio.Tag.FirstPerformer)
                ? "Unknown Artist"
                : audio.Tag.FirstPerformer.Trim();

            TimeSpan duration = audio.Properties?.Duration ?? TimeSpan.Zero;

            // Album and track number are optional tags: a loose single usually carries neither,
            // so they stay null and the view model substitutes its own neutral labels.
            string? album = string.IsNullOrWhiteSpace(audio.Tag.Album)
                ? null
                : audio.Tag.Album.Trim();

            uint trackNumber = audio.Tag.Track;

            return new Track(
                title,
                artist,
                duration,
                fullPath,
                album,
                trackNumber > 0 ? (int)trackNumber : null);
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or NotSupportedException
                        or TagLib.CorruptFileException
                        or TagLib.UnsupportedFormatException
            )
        {
            // Corrupt or unreadable file: skip it instead of breaking the whole scan.
            return null;
        }
    }
}
