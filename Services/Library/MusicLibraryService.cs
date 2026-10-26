using System.IO;
using SongPlayer.Models;

namespace SongPlayer.Services.Library;

/// <summary>
/// Coordinates <see cref="IAudioFileScanner"/>, <see cref="ITrackMetadataReader"/> and
/// <see cref="LibraryStore"/>. It holds no state of its own: the library lives in the view
/// model, this service only moves data in and out of it.
/// </summary>
public sealed class MusicLibraryService : IMusicLibraryService
{
    private readonly IAudioFileScanner _scanner;
    private readonly ITrackMetadataReader _metadataReader;
    private readonly LibraryStore _store;

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="scanner">Finds supported audio files.</param>
    /// <param name="metadataReader">Reads their tags.</param>
    /// <param name="store">Persists the resulting paths.</param>
    public MusicLibraryService(
        IAudioFileScanner scanner,
        ITrackMetadataReader metadataReader,
        LibraryStore store)
    {
        _scanner = scanner;
        _metadataReader = metadataReader;
        _store = store;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Track>> ScanAsync(string folder)
        => Task.Run<IReadOnlyList<Track>>(() =>
        {
            if (!Directory.Exists(folder))
            {
                return [];
            }

            // Metadata reading is synchronous and IO bound, so the whole walk is pushed onto
            // a background thread; the caller stays free to keep the UI responsive.
            var tracks = new List<Track>();

            foreach (string file in _scanner.FindMusicFiles(folder))
            {
                if (_metadataReader.ReadTrack(file) is { } track)
                {
                    tracks.Add(track);
                }
            }

            return tracks;
        });

    /// <inheritdoc />
    public async Task<IReadOnlyList<Track>> LoadAsync()
    {
        IReadOnlyList<LibraryEntry> entries = await _store.LoadEntriesAsync();

        // Paths are re-read one by one so a file deleted since the last session simply
        // disappears from the library instead of breaking the restore. Online entries need no
        // disk access at all: their metadata is the cached copy, and the stream URL is resolved
        // on demand.
        var tracks = new List<Track>(entries.Count);

        foreach (LibraryEntry entry in entries)
        {
            if (!entry.IsFile)
            {
                tracks.Add(entry.ToTrack());
                continue;
            }

            if (_metadataReader.ReadTrack(entry.FilePath!) is { } track)
            {
                tracks.Add(track);
            }
        }

        return tracks;
    }

    /// <inheritdoc />
    public Task SaveAsync(IEnumerable<Track> tracks) => _store.SaveAsync(tracks);
}
