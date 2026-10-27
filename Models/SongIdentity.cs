namespace SongPlayer.Models;

/// <summary>
/// Which recording of a song a lyric timeline is supposed to line up with.
/// </summary>
/// <remarks>
/// A lyric database stores one timeline per record, and a live cut runs on a different clock than
/// the studio take it was written for. Telling the two apart is what keeps a cover from being
/// auto-matched to the original's karaoke timing, so the distinction travels with the identity
/// instead of being rediscovered by every caller.
/// </remarks>
public enum SongVersion
{
    /// <summary>Nothing in the title says which recording this is.</summary>
    Unknown,

    /// <summary>A normal studio recording.</summary>
    Studio,

    /// <summary>Recorded in front of an audience.</summary>
    Live,

    /// <summary>An unplugged or otherwise rearranged acoustic take.</summary>
    Acoustic,

    /// <summary>Performed by somebody other than the credited artist.</summary>
    Cover,

    /// <summary>No vocals at all.</summary>
    Instrumental,

    /// <summary>A remix, bootleg or otherwise re-produced version.</summary>
    Remix,

    /// <summary>Sped up, nightcore or slowed down.</summary>
    SpedUp,

    /// <summary>A karaoke backing track.</summary>
    Karaoke,
}

/// <summary>
/// Where a piece of identity data came from, which is also how much it can be trusted.
/// </summary>
public enum IdentitySource
{
    /// <summary>Read from the file's own tags.</summary>
    TagMetadata,

    /// <summary>Derived from the file name, which is a guess by definition.</summary>
    FileName,

    /// <summary>
    /// Parsed out of an online video title. The uploader typed it by hand, so it describes the
    /// video rather than the song.
    /// </summary>
    VideoTitle,

    /// <summary>
    /// Only the keyword the user searched for is known. A clue, never a confirmed identity.
    /// </summary>
    SearchClue,
}

/// <summary>
/// What the player believes it is playing, expressed as searchable lyric metadata.
/// </summary>
/// <remarks>
/// This exists because the two track kinds lie in different ways. A local file has real tags but
/// may have none at all, and <c>"Unknown Artist"</c> is a placeholder rather than a value. An
/// online track has a video title — frequently a remaster label, a resolution badge or a lyric-video
/// caption — and an uploader, who is an account and not an artist.
/// <para>
/// Every field except <see cref="RawTitle"/> is nullable on purpose: "no artist" is information,
/// and sending an invented one to a lyric database trades a possible match for a confident wrong
/// answer. <see cref="SearchClue"/> keeps the keyword the user actually typed, because that was a
/// human describing the song even when the upload's title was not.
/// </para>
/// </remarks>
/// <param name="RawTitle">Title exactly as the source reported it, before any cleaning.</param>
/// <param name="SongTitle">Cleaned song name, or <see langword="null"/> when nothing usable is left.</param>
/// <param name="Artist">Performing artist, or <see langword="null"/> when unknown or untrusted.</param>
/// <param name="Album">Album name, or <see langword="null"/> when the source carries none.</param>
/// <param name="Duration">Length the source reported, or <see langword="null"/> when unknown.</param>
/// <param name="Version">Recording kind extracted from the title, or <see cref="SongVersion.Unknown"/>.</param>
/// <param name="Source">Field that carried the title, which bounds how much it can be trusted.</param>
/// <param name="SearchClue">Keyword the user searched for when this track was picked, if any.</param>
/// <param name="AlternativeTitles">
/// Other names the same song may be stored under, most specific first. A video title is one string
/// that often names the song, the performer and the video at once, and a database record matches
/// at most one of those readings; the matcher tries every spelling instead of trusting the first.
/// </param>
public sealed record SongIdentity(
    string RawTitle,
    string? SongTitle,
    string? Artist,
    string? Album,
    TimeSpan? Duration,
    SongVersion Version,
    IdentitySource Source,
    string? SearchClue,
    IReadOnlyList<string>? AlternativeTitles = null)
{
    /// <summary>True when there is a song name worth searching for.</summary>
    public bool HasSongTitle => !string.IsNullOrWhiteSpace(SongTitle);

    /// <summary>True when the artist is known and safe to send to a lyric database.</summary>
    public bool HasArtist => !string.IsNullOrWhiteSpace(Artist);

    /// <summary>True when the album is known, which only ever helps as a tie-breaker.</summary>
    public bool HasAlbum => !string.IsNullOrWhiteSpace(Album);

    /// <summary>True when the length is known; a zero or missing length is never sent.</summary>
    public bool HasDuration => Duration is { } length && length > TimeSpan.Zero;

    /// <summary>
    /// True when the title came from the file's own metadata rather than from an upload.
    /// </summary>
    public bool IsTitleTrusted => Source is IdentitySource.TagMetadata or IdentitySource.FileName;

    /// <summary>
    /// Length in seconds, or <see langword="null"/> when it is unknown or zero.
    /// </summary>
    /// <remarks>
    /// The lyric API treats a duration as an exact-match filter, so <c>0</c> would make every
    /// lookup fail instead of being ignored.
    /// </remarks>
    public double? DurationSeconds => HasDuration ? Duration!.Value.TotalSeconds : null;

    /// <summary>
    /// Every name worth asking the database about, most specific first, without duplicates.
    /// </summary>
    /// <remarks>
    /// The main name leads so that a lookup that succeeds on it never sends a second request; the
    /// alternates exist for the case where the leading reading was the wrong one. Empty entries are
    /// dropped so no caller has to filter.
    /// </remarks>
    public IReadOnlyList<string> TitlesToTry
    {
        get
        {
            List<string> titles = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            void Add(string? candidate)
            {
                string trimmed = candidate?.Trim() ?? string.Empty;
                if (trimmed.Length > 0 && seen.Add(trimmed))
                {
                    titles.Add(trimmed);
                }
            }

            Add(SongTitle);
            if (AlternativeTitles is { } alternatives)
            {
                foreach (string alternative in alternatives)
                {
                    Add(alternative);
                }
            }

            return titles;
        }
    }
}

/// <summary>
/// Rules for comparing recording kinds.
/// </summary>
/// <remarks>
/// Kept next to the enum rather than inside the matcher because the answer is a fact about the
/// versions, not a knob the matcher may tune: the studio take and the live take of the same song
/// do not share a timeline, and no score should be able to talk itself out of that.
/// </remarks>
public static class SongVersionRules
{
    /// <summary>
    /// True when two recording kinds may share a lyric timeline.
    /// </summary>
    /// <param name="track">Kind detected on the playing track.</param>
    /// <param name="candidate">Kind detected on the lyric record.</param>
    /// <remarks>
    /// <see cref="SongVersion.Unknown"/> is treated as compatible with everything, because most
    /// titles simply do not say. That makes the pair compatible but not *confirmed*: the matcher
    /// gives it a lower score rather than treating silence as agreement.
    /// </remarks>
    public static bool AreCompatible(SongVersion track, SongVersion candidate)
        => track == candidate
            || track == SongVersion.Unknown
            || candidate == SongVersion.Unknown;

    /// <summary>
    /// True when the difference is strong enough that an automatic match must not happen.
    /// </summary>
    /// <param name="track">Kind detected on the playing track.</param>
    /// <param name="candidate">Kind detected on the lyric record.</param>
    public static bool IsConflict(SongVersion track, SongVersion candidate)
        => !AreCompatible(track, candidate)
            && track != SongVersion.Unknown
            && candidate != SongVersion.Unknown;
}
