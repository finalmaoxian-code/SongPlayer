using System.IO;
using System.Text.Json;
using SongPlayer.Models;
using SongPlayer.Services.Bilibili;

namespace SongPlayer.Services.Lyrics;

/// <summary>
/// Small on-disk archive of the timed subtitles that were read for an online source.
/// </summary>
/// <remarks>
/// Subtitles are the one piece of Bilibili metadata that is both expensive to fetch and easy to
/// lose: the player endpoint hides them behind a login for some videos, so a second resolve can
/// legitimately return nothing where the first one returned a full transcript. Keeping the last
/// readable copy under <c>%LOCALAPPDATA%\SongPlayer\captions</c> means a track that played once
/// with subtitles keeps them.
/// <para>
/// Only the subtitle lines are stored — never the audio URL, which is signed with a deadline and
/// re-resolved on every play. Entries are keyed by the stable stream key (<c>bilibili:BV…</c>), so
/// they never go stale in the way a signed URL would.
/// </para>
/// <para>
/// Both operations degrade instead of throwing: a corrupt or unwritable cache must never stop a
/// track from playing.
/// </para>
/// </remarks>
public sealed class CaptionStore
{
    /// <summary>
    /// Folder holding one JSON file per archived stream key.
    /// </summary>
    private static readonly string FolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SongPlayer",
        "captions"
    );

    /// <summary>
    /// Reads the archived subtitles of <paramref name="streamKey"/>.
    /// </summary>
    /// <param name="streamKey">Stable identity of the online track, e.g. <c>bilibili:BV…</c>.</param>
    /// <returns>Timed lines in playback order, or an empty list when nothing is archived.</returns>
    public IReadOnlyList<LyricLine> Load(string streamKey)
    {
        if (BuildPath(streamKey) is not { } path || !File.Exists(path))
        {
            return [];
        }

        try
        {
            string json = File.ReadAllText(path);

            StoredCaption[]? stored = JsonSerializer.Deserialize<StoredCaption[]>(json);

            if (stored is null)
            {
                return [];
            }

            return stored
                .Where(line => line.Seconds >= 0 && !string.IsNullOrWhiteSpace(line.Text))
                .OrderBy(line => line.Seconds)
                .Select(line => new LyricLine(TimeSpan.FromSeconds(line.Seconds), line.Text!.Trim()))
                .ToArray();
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Archives the subtitles of <paramref name="streamKey"/>, replacing any earlier copy.
    /// </summary>
    /// <param name="streamKey">Stable identity of the online track.</param>
    /// <param name="captions">Lines that were just read from the source.</param>
    /// <remarks>
    /// An empty list is ignored rather than written: losing the fetch must not erase a good copy
    /// that was archived on an earlier play.
    /// </remarks>
    public async Task SaveAsync(string streamKey, IReadOnlyList<BilibiliCaption> captions)
    {
        if (captions.Count == 0 || BuildPath(streamKey) is not { } path)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(FolderPath);

            StoredCaption[] stored = captions
                .Where(line => line.Seconds >= 0 && !string.IsNullOrWhiteSpace(line.Text))
                .OrderBy(line => line.Seconds)
                .Select(line => new StoredCaption(line.Seconds, line.Text))
                .ToArray();

            string json = JsonSerializer.Serialize(
                stored,
                new JsonSerializerOptions { WriteIndented = true });

            // Write-then-move keeps the archive readable if the process dies mid-save.
            string tempPath = path + ".tmp";

            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation; failing to write it is not a playback problem.
        }
    }

    /// <summary>
    /// Maps a stream key to its archive file, or <see langword="null"/> when the key cannot be
    /// turned into a safe file name.
    /// </summary>
    /// <param name="streamKey">Stable identity of the online track.</param>
    private static string? BuildPath(string streamKey)
    {
        if (string.IsNullOrWhiteSpace(streamKey))
        {
            return null;
        }

        // "bilibili:BV1GJ411x7h7" -> "BV1GJ411x7h7": the provider prefix is not needed because a
        // future source would bring its own folder.
        int separator = streamKey.LastIndexOf(':');
        string id = separator >= 0 ? streamKey[(separator + 1)..] : streamKey;

        var safe = new System.Text.StringBuilder(id.Length);

        foreach (char character in id)
        {
            safe.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '_');
        }

        if (safe.Length == 0)
        {
            return null;
        }

        return Path.Combine(FolderPath, safe.ToString() + ".json");
    }

    /// <summary>
    /// Shape of one archived line.
    /// </summary>
    /// <param name="Seconds">Start of the line, in seconds.</param>
    /// <param name="Text">Text of the line.</param>
    private sealed record StoredCaption(
        [property: System.Text.Json.Serialization.JsonPropertyName("seconds")] double Seconds,
        [property: System.Text.Json.Serialization.JsonPropertyName("text")] string? Text);
}
