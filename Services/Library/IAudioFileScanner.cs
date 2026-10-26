namespace SongPlayer.Services.Library;

/// <summary>
/// Decides which files are audio the player can handle and walks a folder for them.
/// </summary>
/// <remarks>
/// Isolated from the rest of the library pipeline so the supported-format list has exactly
/// one home and so a test can scan a temporary directory without touching TagLib or JSON.
/// </remarks>
public interface IAudioFileScanner
{
    /// <summary>
    /// True when the file extension belongs to a format the player supports.
    /// </summary>
    /// <param name="path">File path; the file does not have to exist.</param>
    bool IsSupported(string path);

    /// <summary>
    /// Enumerates the supported audio files under <paramref name="folder"/> recursively.
    /// </summary>
    /// <param name="folder">Root folder to walk.</param>
    /// <returns>Absolute paths, in the order the file system returned them.</returns>
    string[] FindMusicFiles(string folder);
}
