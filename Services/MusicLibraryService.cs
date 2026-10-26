using SongPlayer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SongPlayer.Services;

public sealed class MusicLibraryService
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".flac",
        ".wav",
        ".m4a",
    };

    private static readonly string DatabasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SongPlayer",
        "library.json"
    );

    public bool IsSupported(string path)
    {
        return Extensions.Contains(Path.GetExtension(path));
    }

    // find supported audio files recursively
    public string[] FindMusicFiles(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        return [.. Directory.EnumerateFiles(folder, "*", options).Where(IsSupported)];
    }

    // Read metadata without modifying the audio file
    public Track? ReadTrack(string path)
    {
        try
        {
            if (!File.Exists(path) || !IsSupported(path))
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

            return new Track(title, artist, duration);
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
            // Corrupt or unsupported file: skip it instead of breaking the whole scan.
            return null;
        }
    }


    // Restore saved paths.
    public async Task<string[]> LoadPathsAsync()
    {
        if (!File.Exists(DatabasePath))
            return [];

        try
        {
            string json = await File.ReadAllTextAsync(DatabasePath);

            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return [];
        }
    }

    // Save the library as a list of file paths.
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

        string tempPath = DatabasePath + ".tmp";

        await File.WriteAllTextAsync(tempPath, json);

        File.Move(tempPath, DatabasePath, overwrite: true);
    }
}
