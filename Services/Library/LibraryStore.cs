using System.IO;
using System.Text.Json;
using SongPlayer.Models;

namespace SongPlayer.Services.Library;

/// <summary>
/// Persists the library as a list of file paths in
/// <c>%LOCALAPPDATA%\SongPlayer\library.json</c>.
/// </summary>
/// <remarks>
/// Only paths are stored: titles and durations are cheap to re-read from the tags and would
/// go stale as soon as a file is retagged. Both operations degrade to an empty result or a
/// no-op instead of throwing, because a corrupt cache must never stop the app from starting.
/// </remarks>
public sealed class LibraryStore
{
    /// <summary>
    /// Full path of the JSON database.
    /// </summary>
    private static readonly string DatabasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SongPlayer",
        "library.json"
    );

    /// <summary>
    /// Restores the saved paths, or an empty array when nothing is stored or the file is
    /// unreadable.
    /// </summary>
    public async Task<string[]> LoadPathsAsync()
    {
        if (!File.Exists(DatabasePath))
        {
            return [];
        }

        try
        {
            string json = await File.ReadAllTextAsync(DatabasePath);

            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Writes the paths that are backed by a real file, de-duplicated case-insensitively.
    /// </summary>
    /// <param name="tracks">Tracks to persist; ones without a path are ignored.</param>
    public async Task SavePathsAsync(IEnumerable<Track> tracks)
    {
        string directory = Path.GetDirectoryName(DatabasePath)!;
        Directory.CreateDirectory(directory);

        string[] paths = tracks
            .Select(track => track.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string json = JsonSerializer.Serialize(
            paths,
            new JsonSerializerOptions { WriteIndented = true });

        // Write-then-move keeps the database readable if the process dies mid-save.
        string tempPath = DatabasePath + ".tmp";

        await File.WriteAllTextAsync(tempPath, json);

        File.Move(tempPath, DatabasePath, overwrite: true);
    }
}
