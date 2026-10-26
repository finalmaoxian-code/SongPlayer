using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SongPlayer.Models;

namespace SongPlayer.Services.Library;

/// <summary>
/// Persists the library as a list of <see cref="LibraryEntry"/> values in
/// <c>%LOCALAPPDATA%\SongPlayer\library.json</c>.
/// </summary>
/// <remarks>
/// Titles and durations of local files are not stored: they are cheap to re-read from the tags and
/// would go stale as soon as a file is retagged. Online tracks are the exception — their metadata
/// is cached next to the stream key so a restored list shows real rows instead of placeholders, and
/// the key is re-resolved before playback anyway.
/// <para>
/// Both operations degrade to an empty result or a no-op instead of throwing, because a corrupt
/// cache must never stop the app from starting. The reader also accepts the older
/// <c>string[]</c> layout, so a library written by a previous build still loads.
/// </para>
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
    /// Options used for both directions, so the property names cannot drift between a write and a
    /// read of the same file.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Restores the saved entries, or an empty array when nothing is stored or the file is
    /// unreadable.
    /// </summary>
    public async Task<IReadOnlyList<LibraryEntry>> LoadEntriesAsync()
    {
        if (!File.Exists(DatabasePath))
        {
            return [];
        }

        try
        {
            string json = await File.ReadAllTextAsync(DatabasePath);

            return Parse(json);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Writes the entries, de-duplicated case-insensitively by key.
    /// </summary>
    /// <param name="tracks">Tracks to persist; ones that are neither a file nor a stream are skipped.</param>
    public async Task SaveAsync(IEnumerable<Track> tracks)
    {
        LibraryEntry[] entries = tracks
            .Select(LibraryEntry.FromTrack)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .DistinctBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string directory = Path.GetDirectoryName(DatabasePath)!;
        Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(entries, SerializerOptions);

        // Write-then-move keeps the database readable if the process dies mid-save.
        string tempPath = DatabasePath + ".tmp";

        await File.WriteAllTextAsync(tempPath, json);

        File.Move(tempPath, DatabasePath, overwrite: true);
    }

    /// <summary>
    /// Reads either the current object layout or the older array-of-paths layout.
    /// </summary>
    /// <param name="json">File contents.</param>
    /// <returns>The parsed entries; unknown shapes yield an empty list.</returns>
    private static IReadOnlyList<LibraryEntry> Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var entries = new List<LibraryEntry>();

        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            // The first version of this file stored bare strings. Keeping that readable costs one
            // branch and saves every existing user from re-importing their library.
            if (element.ValueKind == JsonValueKind.String)
            {
                if (element.GetString() is { Length: > 0 } path)
                {
                    entries.Add(new LibraryEntry(path, null, null, null, null, null));
                }

                continue;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            LibraryEntry? entry = element.Deserialize<LibraryEntry>(SerializerOptions);

            if (entry is not null && !string.IsNullOrWhiteSpace(entry.Key))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }
}
