using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SongPlayer.Models;

namespace SongPlayer.Services.Lyrics;

/// <summary>
/// What is remembered about one track's lyrics.
/// </summary>
/// <remarks>
/// The record deliberately stores the lyric body as well as the match, because re-fetching a body on
/// every track change would hammer a free service for data that does not change. It also stores the
/// user's own decisions — a chosen record and a time offset — which must survive restarts and outrank
/// whatever the matcher would say.
/// </remarks>
public sealed record LyricCacheEntry
{
    /// <summary>Version of this shape; older entries are discarded rather than misread.</summary>
    public int SchemaVersion { get; init; } = LyricCacheStore.SchemaVersion;

    /// <summary>Stable identity of the track this entry belongs to.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Outcome kind, as the name of <see cref="LyricKind"/>.</summary>
    public string Kind { get; init; } = nameof(LyricKind.NotFound);

    /// <summary>Provider name, as the name of <see cref="LyricSourceKind"/>.</summary>
    public string Source { get; init; } = nameof(LyricSourceKind.None);

    /// <summary>Database record identifier, when one was chosen.</summary>
    public long? RecordId { get; init; }

    /// <summary>Song name of the chosen record.</summary>
    public string? MatchedTitle { get; init; }

    /// <summary>Artist of the chosen record.</summary>
    public string? MatchedArtist { get; init; }

    /// <summary>Album of the chosen record.</summary>
    public string? MatchedAlbum { get; init; }

    /// <summary>Length of the chosen record, in seconds.</summary>
    public int? MatchedDurationSeconds { get; init; }

    /// <summary>Score that decided the match.</summary>
    public int MatchScore { get; init; }

    /// <summary>Why the match was made.</summary>
    public string? Explanation { get; init; }

    /// <summary>Raw LRC body, re-parsed on load so parser fixes apply to cached entries too.</summary>
    public string? SyncedLyrics { get; init; }

    /// <summary>Untimed lyric text.</summary>
    public string? PlainText { get; init; }

    /// <summary>When the entry was written.</summary>
    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>True when the entry records "this track has no lyrics", which expires.</summary>
    public bool Negative { get; init; }

    /// <summary>User's lyric time offset in milliseconds; positive shifts lyrics later.</summary>
    public int OffsetMilliseconds { get; init; }

    /// <summary>Record the user picked by hand, which outranks automatic matching.</summary>
    public long? ManualRecordId { get; init; }

    /// <summary>Length of the chosen record as a time span.</summary>
    [JsonIgnore]
    public TimeSpan? MatchedDuration =>
        MatchedDurationSeconds is { } seconds && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>The parsed outcome kind.</summary>
    [JsonIgnore]
    public LyricKind ResultKind =>
        Enum.TryParse(Kind, ignoreCase: true, out LyricKind kind) ? kind : LyricKind.NotFound;

    /// <summary>The parsed provider.</summary>
    [JsonIgnore]
    public LyricSourceKind ResultSource =>
        Enum.TryParse(Source, ignoreCase: true, out LyricSourceKind source)
            ? source
            : LyricSourceKind.None;
}

/// <summary>
/// Stores lyric lookups, manual choices and per-track offsets on disk.
/// </summary>
/// <remarks>
/// One file per track keeps writes small and independent, so a single corrupt entry cannot take the
/// whole feature down: a bad file is deleted and treated as a miss. Every operation is best-effort by
/// design — a read-only profile, a full disk or a locked file must never stop a song from playing.
/// </remarks>
public sealed class LyricCacheStore
{
    /// <summary>Current entry shape version.</summary>
    /// <remarks>
    /// Bumped whenever the meaning of a stored entry changes, because an old entry is then no longer
    /// evidence about the current behaviour. Version 2 followed a fix to how a video title is read as
    /// a song name: entries written before it recorded "no lyrics" for tracks that the corrected rules
    /// can find, and a remembered miss would hide the fix for days.
    /// </remarks>
    public const int SchemaVersion = 2;

    /// <summary>How long a "no lyrics" answer is trusted before the database is asked again.</summary>
    private static readonly TimeSpan NegativeLifetime = TimeSpan.FromDays(3);

    /// <summary>How long a positive entry is used before it is refreshed in the background.</summary>
    private static readonly TimeSpan PositiveLifetime = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _folder;

    /// <summary>Creates a store under the given folder, defaulting to the app data directory.</summary>
    public LyricCacheStore(string? folder = null)
    {
        _folder =
            folder
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SongPlayer",
                "lyrics"
            );
    }

    /// <summary>
    /// Builds the stable key for a track.
    /// </summary>
    /// <remarks>
    /// The key is never derived from the title alone: two uploads of the same song by different people,
    /// or a live version and a studio version, would then share one entry and overwrite each other. A
    /// stream is identified by its stream key, a file by its normalized full path, and the key is hashed
    /// so it is safe as a file name regardless of what characters the path contains.
    /// </remarks>
    public static string KeyFor(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);

        string identity =
            !string.IsNullOrWhiteSpace(track.StreamKey) ? "stream:" + track.StreamKey.Trim()
            : track.FilePath is { Length: > 0 } path ? "file:" + NormalizePath(path)
            : "track:"
                + LyricText.Comparable(track.Title)
                + "|"
                + LyricText.Comparable(track.Artist);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToLowerInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    /// <summary>Reads an entry, or <see langword="null"/> when there is none or it is unusable.</summary>
    public async Task<LyricCacheEntry?> LoadAsync(
        string key,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        string path = BuildPath(key);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string json = await File.ReadAllTextAsync(path, cancellationToken)
                .ConfigureAwait(false);
            LyricCacheEntry? entry = JsonSerializer.Deserialize<LyricCacheEntry>(json, JsonOptions);

            if (entry is null || entry.SchemaVersion != SchemaVersion || entry.Key != key)
            {
                DeleteQuietly(path);
                return null;
            }

            if (entry.Negative && IsStale(entry, NegativeLifetime))
            {
                return null;
            }

            return entry;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or JsonException
                        or NotSupportedException
                        or ArgumentException
            )
        {
            // A damaged file must not block the song: drop it and let the caller query afresh.
            DeleteQuietly(path);
            return null;
        }
    }

    /// <summary>Writes an entry, replacing any previous one atomically.</summary>
    public async Task SaveAsync(
        LyricCacheEntry entry,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(entry.Key))
        {
            return;
        }

        string path = BuildPath(entry.Key);
        string temporary = path + ".tmp";

        try
        {
            Directory.CreateDirectory(_folder);
            string json = JsonSerializer.Serialize(entry, JsonOptions);
            await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);

            // Replace rather than truncate: a crash mid-write must leave the old entry readable.
            File.Move(temporary, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            DeleteQuietly(temporary);
        }
    }

    /// <summary>Removes an entry so the next lookup starts from nothing.</summary>
    public Task RemoveAsync(string key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            DeleteQuietly(BuildPath(key));
        }

        return Task.CompletedTask;
    }

    /// <summary>True when a positive entry is old enough to refresh.</summary>
    public static bool IsRefreshDue(LyricCacheEntry entry) =>
        !entry.Negative && IsStale(entry, PositiveLifetime);

    /// <summary>True when the entry has outlived the given lifetime.</summary>
    private static bool IsStale(LyricCacheEntry entry, TimeSpan lifetime) =>
        DateTimeOffset.UtcNow - entry.FetchedAt > lifetime;

    /// <summary>Maps a key onto its file.</summary>
    private string BuildPath(string key) => Path.Combine(_folder, key + ".json");

    /// <summary>Deletes a file, ignoring the usual reasons deletion can fail.</summary>
    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Nothing useful to do: a stale file is only a cache miss next time.
        }
    }

    /// <summary>Normalizes a path so the same file always hashes to the same key.</summary>
    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }
}
