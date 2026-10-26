using System.IO;

namespace SongPlayer.Services.Library;

/// <summary>
/// File-system implementation of <see cref="IAudioFileScanner"/>.
/// </summary>
/// <remarks>
/// Two behaviours here are deliberate:
/// inaccessible subfolders are skipped instead of aborting the whole walk, and reparse
/// points (symlinks, junctions) are not followed, which prevents infinite recursion when a
/// folder links back to one of its own parents.
/// </remarks>
public sealed class AudioFileScanner : IAudioFileScanner
{
    /// <summary>
    /// Lower-case extensions the player can decode, compared case-insensitively.
    /// </summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".flac",
        ".wav",
        ".m4a",
    };

    /// <inheritdoc />
    public bool IsSupported(string path)
        => Extensions.Contains(Path.GetExtension(path));

    /// <inheritdoc />
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
}
