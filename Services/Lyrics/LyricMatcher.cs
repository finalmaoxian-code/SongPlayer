using System.Globalization;
using SongPlayer.Models;

namespace SongPlayer.Services.Lyrics;

/// <summary>
/// Why the matcher arrived at its answer, in terms a person can read.
/// </summary>
/// <param name="Record">The record that was scored best.</param>
/// <param name="Score">Its score, 0–100.</param>
/// <param name="RunnerUpScore">Best score among the other records, 0 when there was only one.</param>
/// <param name="IsConfident">True when the record may be used without asking.</param>
/// <param name="Explanation">Short sentence describing the evidence.</param>
/// <param name="Rejections">Records that were excluded outright and why.</param>
public sealed record LyricMatch(
    LrclibRecord? Record,
    double Score,
    double RunnerUpScore,
    bool IsConfident,
    string Explanation,
    IReadOnlyList<string> Rejections
)
{
    /// <summary>The runner-up record, when there was one.</summary>
    public LrclibRecord? RunnerUp { get; init; }

    /// <summary>All scored records, best first, used to build the chooser.</summary>
    public IReadOnlyList<(LrclibRecord Record, double Score)> Ranked { get; init; } = [];
}

/// <summary>
/// Picks which database record may stand in for a track.
/// </summary>
/// <remarks>
/// This is an engineering policy tuned against the live database, not a guarantee the service makes.
/// The thresholds are named constants so they can be argued with in one place. The shape of the policy:
/// <list type="bullet">
/// <item>A record whose song name does not match is never chosen, no matter how good its lyrics are.
/// A perfectly timed lyric for a different song is worse than no lyrics at all.</item>
/// <item>Artist agreement only counts when the track's artist is trusted; comparing against an
/// uploader name would reject every correct record.</item>
/// <item>A version conflict (live take against a studio release, cover against original, sped up or
/// instrumental against a vocal track) disqualifies a record from automatic use: its timestamps belong
/// to a different performance.</item>
/// <item>Duration closeness supports a match but cannot create one, because a video's length includes
/// intro, outro and talking.</item>
/// <item>When two records are close, or the best is merely adequate, the user is asked rather than
/// guessed at.</item>
/// </list>
/// </remarks>
public sealed class LyricMatcher
{
    /// <summary>Song-name similarity at or above this is accepted as the same song.</summary>
    public const double TitleAcceptThreshold = 0.62;

    /// <summary>Song-name similarity at or above this is treated as an exact title hit.</summary>
    public const double TitleStrongThreshold = 0.88;

    /// <summary>Artist similarity at or above this agrees with the track's artist.</summary>
    public const double ArtistAgreeThreshold = 0.7;

    /// <summary>Artist similarity below this actively disagrees.</summary>
    public const double ArtistDisagreeThreshold = 0.35;

    /// <summary>Score a record needs before it may be used without asking.</summary>
    public const double AutoAcceptScore = 78;

    /// <summary>How far ahead of the runner-up the best record must be to be used unasked.</summary>
    public const double AutoAcceptMargin = 8;

    /// <summary>Duration difference that neither helps nor hurts.</summary>
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(4);

    /// <summary>Duration difference beyond which the match is actively doubted.</summary>
    private static readonly TimeSpan DurationMismatch = TimeSpan.FromSeconds(25);

    /// <summary>Weight of each signal in the 0–100 score.</summary>
    private const double TitleWeight = 62;
    private const double ArtistWeight = 24;
    private const double DurationWeight = 10;
    private const double AlbumWeight = 4;

    /// <summary>Penalty applied when the database record is flagged instrumental but the track is sung.</summary>
    private const double InstrumentalPenalty = 20;

    /// <summary>
    /// Scores every record and decides whether the best one may be used automatically.
    /// </summary>
    /// <param name="identity">What the playing track is believed to be.</param>
    /// <param name="records">Records returned by the database.</param>
    public LyricMatch Match(SongIdentity identity, IReadOnlyList<LrclibRecord> records)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(records);

        List<(LrclibRecord Record, double Score)> ranked = [];
        List<string> rejections = [];

        foreach (LrclibRecord record in records)
        {
            if (!record.HasContent)
            {
                // A record with no lyric text and no instrumental flag is a stray row, not an answer.
                continue;
            }

            string recordTitle = record.TrackName ?? string.Empty;
            double titleScore = TitleSimilarity(identity, recordTitle);
            if (titleScore < TitleAcceptThreshold)
            {
                rejections.Add($"{Describe(record)}: name does not match the track.");
                continue;
            }

            SongVersion recordVersion = LyricText.DetectVersion(recordTitle).Version;
            if (SongVersionRules.IsConflict(identity.Version, recordVersion))
            {
                rejections.Add($"{Describe(record)}: different version of the song.");
                continue;
            }

            if (
                record.Instrumental
                && identity.Version is not (SongVersion.Instrumental or SongVersion.Karaoke)
            )
            {
                // Kept in the list but pushed down: an instrumental record for a sung track is not
                // what the user wants, but it is a real answer if nothing else exists.
                rejections.Add($"{Describe(record)}: instrumental only.");
            }

            double score = Score(identity, record, titleScore, recordVersion);
            ranked.Add((record, score));
        }

        ranked.Sort((left, right) => right.Score.CompareTo(left.Score));

        if (ranked.Count == 0)
        {
            return new LyricMatch(
                null,
                0,
                0,
                false,
                "No record with a matching song name was found.",
                rejections
            )
            {
                Ranked = [],
            };
        }

        (LrclibRecord best, double bestScore) = ranked[0];

        // Several rows can score the same while naming the same song by the same performer: the
        // database holds re-uploads, remasters and different lengths of one song, all with the same
        // lyric text. Asking which of two identical answers is meant is a question with no wrong
        // reply, so those rows count as one candidate and the length closest to this track's is
        // preferred. Only a genuinely different candidate can make the choice ambiguous.
        LrclibRecord? runnerUp = null;
        double runnerUpScore = 0;
        foreach ((LrclibRecord record, double score) in ranked)
        {
            if (IsSameCandidate(best, record))
            {
                if (bestScore - score <= DuplicateScoreTolerance)
                {
                    best = CloserLength(best, record, identity.Duration) ? record : best;
                }

                continue;
            }

            runnerUp = record;
            runnerUpScore = score;
            break;
        }

        double margin = bestScore - runnerUpScore;

        string explanation = Describe(best, bestScore);
        bool confident =
            bestScore >= AutoAcceptScore
            && (runnerUp is null || margin >= AutoAcceptMargin)
            && !IsVersionCompromised(identity, best);

        return new LyricMatch(best, bestScore, runnerUpScore, confident, explanation, rejections)
        {
            RunnerUp = runnerUp,
            Ranked = ranked,
        };
    }

    /// <summary>
    /// Similarity between the track's song name and a record name, taking the better of the cleaned
    /// titles and of the two names with version words removed from both sides.
    /// </summary>
    /// <remarks>
    /// Comparing the version-stripped forms matters because a database record for a live take is
    /// routinely stored under the studio title with "(Live)" appended, and stripping both sides lets
    /// the names meet while the version check separately decides whether the timelines agree.
    /// Every name the identity carries is tried and the best one wins: a video title is one string
    /// that can be read as the song, the performer plus the song, or the song plus a description of
    /// the recording session, and the database record matches only one of those readings.
    /// </remarks>
    private static double TitleSimilarity(SongIdentity identity, string recordTitle)
    {
        IReadOnlyList<string> titles = identity.TitlesToTry;
        if (titles.Count == 0)
        {
            titles = [identity.RawTitle];
        }

        double best = 0;
        foreach (string trackTitle in titles)
        {
            double similarity = LyricText.Similarity(trackTitle, recordTitle);
            if (similarity > best)
            {
                best = similarity;
            }

            double stripped = LyricText.Similarity(
                LyricText.DetectVersion(trackTitle).Title,
                LyricText.DetectVersion(recordTitle).Title
            );
            if (stripped > best)
            {
                best = stripped;
            }
        }

        return best;
    }

    /// <summary>Combines the individual signals into one 0–100 score.</summary>
    private static double Score(
        SongIdentity identity,
        LrclibRecord record,
        double titleScore,
        SongVersion recordVersion
    )
    {
        double total = titleScore * TitleWeight;

        if (identity.HasArtist)
        {
            double artistScore = LyricText.Similarity(identity.Artist, record.ArtistName);
            if (artistScore >= ArtistAgreeThreshold)
            {
                total += ArtistWeight * artistScore;
            }
            else if (artistScore < ArtistDisagreeThreshold)
            {
                // A different performer means a different recording in the overwhelming majority of
                // cases. This is a penalty rather than a rejection so that a same-name cover by an
                // unrelated artist can still be offered, just not chosen silently.
                total -= ArtistWeight * 0.75;
            }
        }
        else
        {
            // Without a trusted artist the remaining signals carry the whole decision, so the score is
            // scaled up to keep the acceptance threshold meaningful; the margin rule then does the
            // real work of keeping ambiguous cases out of the automatic path.
            total += ArtistWeight * 0.45;
        }

        if (identity.HasDuration && record.DurationSpan is { } recordDuration)
        {
            TimeSpan delta = (identity.Duration!.Value - recordDuration).Duration();
            if (delta <= DurationTolerance)
            {
                total += DurationWeight;
            }
            else if (delta <= DurationMismatch)
            {
                double span = (DurationMismatch - DurationTolerance).TotalSeconds;
                double closeness = 1 - ((delta - DurationTolerance).TotalSeconds / span);
                total += DurationWeight * Math.Clamp(closeness, 0, 1);
            }
            else
            {
                total -= DurationWeight * 0.5;
            }
        }

        if (
            identity.HasAlbum
            && !string.IsNullOrWhiteSpace(record.AlbumName)
            && LyricText.Similarity(identity.Album, record.AlbumName) >= 0.8
        )
        {
            total += AlbumWeight;
        }

        if (
            record.Instrumental
            && identity.Version is not (SongVersion.Instrumental or SongVersion.Karaoke)
        )
        {
            total -= InstrumentalPenalty;
        }

        // A record whose artist is nothing but a repeat of its own title is how the database stores
        // uncurated rows; those match by name only, so they cannot be trusted silently.
        if (
            !string.IsNullOrWhiteSpace(record.ArtistName)
            && LyricText.Comparable(record.ArtistName) == LyricText.Comparable(record.TrackName)
            && identity.HasArtist
        )
        {
            total -= ArtistWeight * 0.5;
        }

        _ = recordVersion;
        return Math.Clamp(total, 0, 100);
    }

    /// <summary>
    /// True when the record must not be used automatically even if it scored well.
    /// </summary>
    /// <remarks>
    /// Two cases: the record belongs to a different performance version than the track, or it is
    /// instrumental while the track is not. Both put words or timestamps on screen that do not
    /// correspond to what is playing.
    /// </remarks>
    private static bool IsVersionCompromised(SongIdentity identity, LrclibRecord record)
    {
        if (
            record.Instrumental
            && identity.Version is not (SongVersion.Instrumental or SongVersion.Karaoke)
        )
        {
            return true;
        }

        SongVersion recordVersion = LyricText.DetectVersion(record.TrackName).Version;
        return SongVersionRules.IsConflict(identity.Version, recordVersion);
    }

    /// <summary>
    /// How far apart two scores of the same candidate may be before they stop counting as one answer.
    /// </summary>
    private const double DuplicateScoreTolerance = 2;

    /// <summary>
    /// True when two records describe the same song by the same performer, so choosing between them
    /// is not a real choice.
    /// </summary>
    private static bool IsSameCandidate(LrclibRecord left, LrclibRecord right) =>
        string.Equals(
            LyricText.Comparable(left.TrackName),
            LyricText.Comparable(right.TrackName),
            StringComparison.Ordinal
        )
        && string.Equals(
            LyricText.Comparable(left.ArtistName),
            LyricText.Comparable(right.ArtistName),
            StringComparison.Ordinal
        )
        && LyricText.DetectVersion(left.TrackName).Version
            == LyricText.DetectVersion(right.TrackName).Version;

    /// <summary>
    /// True when <paramref name="candidate"/> is a better guess than <paramref name="current"/> by
    /// length alone, which is the only thing left to decide between two rows of the same song.
    /// </summary>
    private static bool CloserLength(
        LrclibRecord current,
        LrclibRecord candidate,
        TimeSpan? wanted
    )
    {
        if (wanted is not { } length || candidate.DurationSpan is not { } offered)
        {
            return false;
        }

        TimeSpan offeredDelta = (length - offered).Duration();
        if (current.DurationSpan is not { } held)
        {
            return true;
        }

        return offeredDelta < (length - held).Duration();
    }

    /// <summary>One-line description of what the match rests on.</summary>
    private static string Describe(LrclibRecord record, double score) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"\"{record.TrackName}\" by {record.ArtistName ?? "unknown artist"} scored {score:0}"
        );

    /// <summary>Describes a record for a rejection note.</summary>
    private static string Describe(LrclibRecord record) =>
        $"\"{record.TrackName}\" by {record.ArtistName ?? "unknown artist"}";
}
