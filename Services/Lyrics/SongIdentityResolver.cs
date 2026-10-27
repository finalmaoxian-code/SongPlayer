using System.IO;
using SongPlayer.Models;

namespace SongPlayer.Services.Lyrics;

/// <summary>
/// Works out what a <see cref="Track"/> might actually be as a song.
/// </summary>
/// <remarks>
/// The two track kinds carry very different information, and the difference is load-bearing:
/// <list type="bullet">
/// <item>A local file can carry real tags, so its title and performer are trustworthy enough to send
/// to an exact-match query.</item>
/// <item>An online video's <see cref="Track.Title"/> is a video title and its
/// <see cref="Track.Artist"/> is the uploader. The uploader is a real identity, but it is not the
/// artist, so it must never reach the database as one — that would silently match the wrong record for
/// every re-upload channel. The video title is a clue, not a fact.</item>
/// </list>
/// Nothing here guesses: an unknown artist stays unknown, and an unknown duration stays absent rather
/// than being sent as zero, which the database would treat as an exact filter.
/// </remarks>
public interface ISongIdentityResolver
{
    /// <summary>Builds the identity for a track.</summary>
    /// <param name="track">Track being played.</param>
    /// <param name="searchClue">Keyword the user originally searched for, when it was kept.</param>
    SongIdentity Resolve(Track track, string? searchClue = null);
}

/// <summary>Default identity resolution rules.</summary>
public sealed class SongIdentityResolver : ISongIdentityResolver
{
    /// <inheritdoc />
    public SongIdentity Resolve(Track track, string? searchClue = null)
    {
        ArgumentNullException.ThrowIfNull(track);

        string? clue = string.IsNullOrWhiteSpace(searchClue) ? null : LyricText.Clean(searchClue);

        return track.IsStream
            ? ResolveStream(track, clue)
            : ResolveFile(track, clue);
    }

    /// <summary>
    /// Online video: the video title is a noisy version of the song name, and the uploader is kept as
    /// a note rather than used as the artist.
    /// </summary>
    /// <remarks>
    /// The interesting case is an upload whose title says nothing about the song: a "re-upload,
    /// sounds great" bracket line, a clickbait sentence, a livestream rip. There the user's own keyword is the better song name, so
    /// it is allowed to replace the title, but only while the title still looks like a video title.
    /// A plainly-named upload (<c>Creep</c>) keeps its title, because in that case a keyword like
    /// "creep radiohead" names the artist more than the song.
    /// <para>
    /// A title can also quote the song name, and that quote outranks every other reading: an upload
    /// whose title wraps the song name in guillemets, with the performer in front and a sentence about
    /// the recording session behind, is telling us exactly what the song is called. Whatever is quoted
    /// therefore becomes the first name to try, and the cleaned whole title stays available as a
    /// fallback.
    /// </para>
    /// </remarks>
    private static SongIdentity ResolveStream(Track track, string? clue)
    {
        string raw = LyricText.Clean(track.Title);
        string withoutNoise = LyricText.RemoveNoise(raw);
        (SongVersion version, string withoutVersion) = LyricText.DetectVersion(withoutNoise);
        (string separatedRight, string? separatedLeft) = LyricText.SplitArtistHint(withoutVersion);

        List<string> alternates = [];

        // A quoted name is the uploader being explicit, so it leads. The separated halves are offered
        // as further readings rather than being promoted to "song" and "artist": in a video title
        // the two halves can be the performer and the song in either order
        // ("Radiohead - Creep" and "Creep - Radiohead" are both real upload shapes), and guessing
        // the direction would both invent an artist and let a wrong guess penalise the correct record.
        (string? quoted, string? quotedArtist) = LyricText.ExtractQuotedName(withoutVersion);

        // A quoted name that is preceded by words is the reliable artist signal there is: it is the
        // "performer《song》" convention, so the words in front are taken at face value.
        string? artist = quotedArtist;
        if (quoted is not null)
        {
            alternates.Add(quoted);
        }

        string outer = (quoted is null ? withoutVersion : LyricText.RemoveQuoted(withoutVersion)).Trim();
        if (outer.Length > 0 && LyricText.Comparable(outer) != LyricText.Comparable(quoted))
        {
            alternates.Add(outer);
        }

        if (separatedLeft is not null)
        {
            AddAlternate(alternates, separatedRight);
            AddAlternate(alternates, separatedLeft);
        }

        string primary = quoted ?? outer;
        if (primary.Length == 0)
        {
            primary = withoutVersion.Length == 0 ? raw : withoutVersion;
        }

        // A search clue is the user telling us what they went looking for, which is often the exact
        // song name and artist.
        IdentitySource source = IdentitySource.VideoTitle;

        if (clue is not null)
        {
            string cleanedClue = LyricText.RemoveNoise(clue);
            (string clueTitle, string? clueArtist) = LyricText.SplitArtistHint(cleanedClue);

            // The title is describing a video rather than naming a song, so the keyword wins as the
            // song name. The version still comes from the title: a live marker there says the timeline is a live
            // one even when the song name has to be taken from the keyword.
            bool clueNamesTheSong = clueTitle.Length > 0
                && LyricText.Similarity(clueTitle, primary) < 0.35;

            if (clueNamesTheSong && LyricText.LooksLikeVideoTitle(raw))
            {
                alternates.Insert(0, primary);
                primary = clueTitle;
                artist = clueArtist ?? artist;

                // The keyword happened to be shaped like "Artist - Song", which is the one case where
                // an online identity is as good as a tag.
                source = clueArtist is not null ? IdentitySource.SearchClue : IdentitySource.VideoTitle;
            }
            else if (artist is null && clueArtist is not null && LyricText.Similarity(clueTitle, primary) >= 0.6)
            {
                // Use the clue's artist only if the clue's song name is the one we are looking for;
                // otherwise the user searched for something else and the clue would fight the title.
                artist = clueArtist;
            }
            else if (LyricText.Comparable(cleanedClue) == LyricText.Comparable(primary))
            {
                // The clue was just the song name, which confirms the title but adds no artist.
                clue = null;
            }
        }

        return new SongIdentity(
            RawTitle: raw,
            SongTitle: primary.Length == 0 ? null : primary,
            Artist: NormalizeName(artist),
            Album: null,
            Duration: track.Duration > TimeSpan.Zero ? track.Duration : null,
            Version: version,
            Source: source,
            SearchClue: clue,
            AlternativeTitles: alternates.Where(candidate => candidate.Length > 0).ToArray());
    }

    /// <summary>
    /// Keeps a reading of the title, as long as it is specific enough to be worth a query.
    /// </summary>
    /// <remarks>
    /// The length caps keep a whole promotional sentence out of the query list: a search for thirty
    /// characters of marketing copy can never match a song name, so it would only cost a request.
    /// </remarks>
    private static void AddAlternate(List<string> alternates, string? candidate)
    {
        string trimmed = candidate?.Trim() ?? string.Empty;
        if (trimmed.Length < 2 || trimmed.Length > 60)
        {
            return;
        }

        if (LyricText.Comparable(trimmed).Length == 0)
        {
            return;
        }

        alternates.Add(trimmed);
    }

    /// <summary>
    /// Local file: tags are trusted when present, and the file name is the fallback.
    /// </summary>
    private static SongIdentity ResolveFile(Track track, string? clue)
    {
        string raw = LyricText.Clean(track.Title);
        string withoutNoise = LyricText.RemoveNoise(raw);
        (SongVersion version, string withoutVersion) = LyricText.DetectVersion(withoutNoise);

        // "Unknown Artist" is a placeholder the reader wrote, not a name, and sending it would make
        // every untagged file look like the same recording.
        string? artist = LyricText.IsPlaceholder(track.Artist) ? null : LyricText.Clean(track.Artist);
        string? album = LyricText.IsPlaceholder(track.Album) ? null : LyricText.Clean(track.Album);

        // A song name containing a separator with no tag to disambiguate it is still just a file name;
        // splitting there would invent an artist, so the title is kept whole.
        string title = withoutVersion.Length == 0 ? raw : withoutVersion;

        return new SongIdentity(
            RawTitle: track.FilePath is { Length: > 0 } path
                ? Path.GetFileNameWithoutExtension(path)
                : raw,
            SongTitle: title.Length == 0 ? null : title,
            Artist: NormalizeName(artist),
            Album: album,
            Duration: track.Duration > TimeSpan.Zero ? track.Duration : null,
            Version: version,
            Source: artist is null ? IdentitySource.FileName : IdentitySource.TagMetadata,
            SearchClue: clue);
    }

    /// <summary>Drops placeholder names and empty strings.</summary>
    private static string? NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) || LyricText.IsPlaceholder(name) ? null : name.Trim();
}
