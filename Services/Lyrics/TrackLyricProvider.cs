using SongPlayer.Models;

namespace SongPlayer.Services.Lyrics;

/// <summary>
/// Orchestrates a lyric lookup: cache, manual choice, database, matching, parsing.
/// </summary>
/// <remarks>
/// The order of preference is deliberate and is what makes "correct a wrong match" work:
/// <list type="number">
/// <item>A record the user picked by hand always wins, so a correction sticks even across restarts.
/// It is fetched by identifier, so a later rename in the database does not lose the choice.</item>
/// <item>Cached content is reused while it is valid, which keeps ordinary track changes off the
/// network entirely.</item>
/// <item>Otherwise the database is queried, matched, and the outcome cached.</item>
/// </list>
/// Nothing here throws for an ordinary failure: an unreachable database becomes
/// <see cref="LyricKind.Failed"/> with a presentable message, which the overlay shows as a retryable
/// state without ever interrupting playback.
/// </remarks>
public sealed class TrackLyricProvider : ILyricProvider
{
    /// <summary>How many candidates are offered when the user has to choose.</summary>
    private const int SelectionCandidateCount = 5;

    /// <summary>How many search results a single lookup may consider.</summary>
    private const int SearchResultLimit = 10;

    /// <summary>How many different spellings of the song name one lookup may search for.</summary>
    /// <remarks>
    /// A video title is a single string that can name the song, the performer and the video at once,
    /// so more than one reading has to be tried. The cap keeps a lookup bounded: three searches is
    /// enough for the real cases (quoted name, surrounding words, the user's keyword) and still far
    /// from a request storm.
    /// </remarks>
    private const int MaximumSearchKeywords = 3;

    private readonly ILrclibClient _client;
    private readonly LyricMatcher _matcher;
    private readonly LyricCacheStore _cache;
    private readonly ISongIdentityResolver _identities;
    private readonly ICaptionArchive _captions;

    /// <summary>Creates the provider.</summary>
    public TrackLyricProvider(
        ILrclibClient client,
        LyricMatcher matcher,
        LyricCacheStore cache,
        ISongIdentityResolver identities,
        ICaptionArchive captions
    )
    {
        _client = client;
        _matcher = matcher;
        _cache = cache;
        _identities = identities;
        _captions = captions;
    }

    /// <inheritdoc />
    public Task<LyricLookupResult> GetLyricsAsync(
        Track track,
        CancellationToken cancellationToken = default
    ) => GetLyricsAsync(track, LyricLookupOptions.Default, cancellationToken);

    /// <inheritdoc />
    public async Task<LyricLookupResult> GetLyricsAsync(
        Track track,
        LyricLookupOptions options,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(options);

        // The demo track is the only track the built-in sample may answer for. A real song never falls
        // back to demo lyrics, because that would hide a genuine "not found" behind fake content.
        if (ReferenceEquals(track, Track.Sample))
        {
            IReadOnlyList<LyricLine> demo = SampleLyricProvider.For(track);
            return demo.Count > 0
                ? LyricLookupResult.Synced(
                    demo,
                    LyricSourceKind.Sample,
                    explanation: "Built-in demo lyrics."
                )
                : LyricLookupResult.NotFound("The demo track has no lyrics.");
        }

        if (!track.IsPlayable && !track.IsStream)
        {
            return LyricLookupResult.NotFound("There is nothing playing to look up.");
        }

        try
        {
            return await LookupAsync(track, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the caller moving on to another track, not a failure to report.
            throw;
        }
        catch (Exception)
        {
            // A lookup must never be able to take the player down; it degrades to a retryable message.
            return LyricLookupResult.Failed("Looking up lyrics failed unexpectedly.");
        }
    }

    /// <summary>Runs the cache, choice, query and match sequence.</summary>
    private async Task<LyricLookupResult> LookupAsync(
        Track track,
        LyricLookupOptions options,
        CancellationToken cancellationToken
    )
    {
        string key = LyricCacheStore.KeyFor(track);
        SongIdentity identity = _identities.Resolve(track, track.SearchClue);

        // The entry is always loaded, even for a forced refresh: it carries the user's offset and manual
        // choice, which belong to the track rather than to one lookup, and dropping them on a re-match
        // would silently undo a correction the user already made.
        LyricCacheEntry? entry = await _cache
            .LoadAsync(key, cancellationToken)
            .ConfigureAwait(false);

        if (entry is not null && options.ClearManualChoice && entry.ManualRecordId is not null)
        {
            entry = entry with { ManualRecordId = null };
            await _cache.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        // An explicit record wins outright: it is either the user's own choice or a candidate they just
        // picked from the list.
        long? wantedRecord =
            options.RecordId ?? (options.ClearManualChoice ? null : entry?.ManualRecordId);
        if (wantedRecord is { } recordId)
        {
            LyricLookupResult chosen = await LoadRecordAsync(
                    recordId,
                    track,
                    identity,
                    options.RecordId is not null,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (chosen.Kind is LyricKind.Synced or LyricKind.Plain or LyricKind.Instrumental)
            {
                await RememberAsync(track, key, entry, chosen, recordId, cancellationToken)
                    .ConfigureAwait(false);
                return chosen;
            }

            // The chosen record has vanished or has no content, so fall through to a fresh lookup
            // rather than leaving the user stranded on a dead choice.
        }

        if (entry is not null && !options.ForceRefresh)
        {
            if (entry.Negative)
            {
                // A recent "no such song" answer is reused so skipping through tracks does not re-query
                // the same missing record repeatedly. The entry expires by itself, and an explicit
                // re-match bypasses it, so it can never become a permanent wrong answer.
                return LyricLookupResult.NotFound(
                    $"No lyrics found for \"{identity.SongTitle ?? identity.RawTitle}\"."
                );
            }

            if (TryBuildFromCache(entry, identity, cancellationToken) is { } cached)
            {
                return cached;
            }
        }

        // Video subtitles are used only when the video actually carries them. They never take the
        // place of a real lyric record, and they are labelled as what they are.
        if (await TryCaptionsAsync(track, cancellationToken).ConfigureAwait(false) is { } captions)
        {
            await RememberAsync(track, key, entry, captions, null, cancellationToken)
                .ConfigureAwait(false);
            return captions;
        }

        LrclibQuery query = BuildQuery(identity);
        if (!query.HasAny)
        {
            return LyricLookupResult.NotFound(
                "There is not enough information in the track to search for lyrics."
            );
        }

        LyricMatch match;
        try
        {
            match = await QueryAndMatchAsync(query, identity, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LrclibException exception)
        {
            // Transport trouble is never cached: caching it would turn a temporary outage into
            // "this song has no lyrics" for days.
            return LyricLookupResult.Failed(exception.Message);
        }

        LyricLookupResult result = ToResult(match, identity, track);
        await RememberAsync(track, key, entry, result, null, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Queries the database and matches, trying an exact lookup before a search.
    /// </summary>
    /// <remarks>
    /// An exact query is only attempted when the title is trustworthy, and it is tried first because it
    /// answers with one curated record instead of a list of near-duplicates. A search follows when the
    /// exact query finds nothing or when the identity is too thin for one.
    /// <para>
    /// A search is then repeated for each reading of the song name, and the results accumulate, so a
    /// record that only one reading can find is still matched by the same scoring rules. Searching
    /// stops early on a confident match; otherwise the best scored attempt wins, which keeps the
    /// "pick the right one" list populated with everything the attempts turned up.
    /// </para>
    /// </remarks>
    private async Task<LyricMatch> QueryAndMatchAsync(
        LrclibQuery query,
        SongIdentity identity,
        CancellationToken cancellationToken
    )
    {
        if (query.CanQueryExactly && identity.IsTitleTrusted)
        {
            LrclibRecord? exact = await _client
                .GetAsync(query, cancellationToken)
                .ConfigureAwait(false);
            if (exact is not null)
            {
                LyricMatch exactMatch = _matcher.Match(identity, [exact]);
                if (
                    exactMatch.IsConfident
                    || exactMatch.Record is not null
                        && exactMatch.Score >= LyricMatcher.TitleAcceptThreshold
                )
                {
                    return exactMatch;
                }
            }
        }

        List<LrclibRecord> found = [];
        HashSet<long> seen = [];
        LyricMatch? best = null;

        foreach (string keyword in SearchKeywords(identity))
        {
            IReadOnlyList<LrclibRecord> results = await _client
                .SearchAsync(query with { Keyword = keyword }, cancellationToken)
                .ConfigureAwait(false);

            foreach (LrclibRecord record in results.Take(SearchResultLimit))
            {
                if (seen.Add(record.Id))
                {
                    found.Add(record);
                }
            }

            LyricMatch attempt = _matcher.Match(identity, found.ToArray());
            if (attempt.IsConfident)
            {
                return attempt;
            }

            if (best is null || attempt.Score > best.Score)
            {
                best = attempt;
            }
        }

        return best ?? _matcher.Match(identity, []);
    }

    /// <summary>
    /// The different strings worth sending to the search endpoint, best reading first.
    /// </summary>
    /// <remarks>
    /// The leading name travels with the artist, because when both are known they disambiguate better
    /// than either alone. Alternate readings keep the artist out: the performer was read off the same
    /// title the alternate came from, so pairing them would just repeat the guess the alternate exists
    /// to work around.
    /// </remarks>
    private static IEnumerable<string> SearchKeywords(SongIdentity identity)
    {
        IReadOnlyList<string> titles = identity.TitlesToTry;
        if (titles.Count == 0)
        {
            if (identity.HasArtist)
            {
                yield return identity.Artist!;
            }

            yield break;
        }

        for (int index = 0; index < titles.Count && index < MaximumSearchKeywords; index++)
        {
            string title = titles[index];
            bool withArtist = index == 0 && identity.HasArtist;
            yield return withArtist ? $"{title} {identity.Artist}" : title;
        }
    }

    /// <summary>Fetches one record by identifier and turns it into a result.</summary>
    private async Task<LyricLookupResult> LoadRecordAsync(
        long recordId,
        Track track,
        SongIdentity identity,
        bool wasExplicit,
        CancellationToken cancellationToken
    )
    {
        try
        {
            LrclibRecord? record = await _client
                .GetByIdAsync(recordId, cancellationToken)
                .ConfigureAwait(false);
            if (record is null)
            {
                return LyricLookupResult.NotFound("That lyric record is no longer available.");
            }

            return ToResult(
                record,
                identity,
                track,
                score: 100,
                explanation: wasExplicit
                    ? "Using the lyric record you chose."
                    : "Using your saved choice for this track."
            );
        }
        catch (LrclibException exception)
        {
            return LyricLookupResult.Failed(exception.Message);
        }
    }

    /// <summary>Turns a chosen record into a result, parsing or degrading as needed.</summary>
    private static LyricLookupResult ToResult(
        LrclibRecord record,
        SongIdentity identity,
        Track track,
        double score = 0,
        string? explanation = null
    )
    {
        string? title = record.TrackName;
        string? artist = record.ArtistName;
        string? album = record.AlbumName;
        TimeSpan? duration = record.DurationSpan;

        // A timed body that parses to nothing is not an error: fall back to the plain text, which is a
        // real answer and better than showing an empty card.
        if (record.HasSynced)
        {
            LrcDocument document = LrcParser.Parse(record.SyncedLyrics);
            if (!document.IsEmpty)
            {
                return LyricLookupResult.Synced(
                    document.Lines,
                    LyricSourceKind.Lrclib,
                    record.Id,
                    title,
                    artist,
                    album,
                    duration,
                    (int)Math.Round(score),
                    explanation
                );
            }
        }

        if (record.HasPlain)
        {
            return LyricLookupResult.Plain(
                record.PlainLyrics!,
                LyricSourceKind.Lrclib,
                record.Id,
                title,
                artist,
                album,
                duration,
                (int)Math.Round(score),
                explanation ?? "The record has lyrics but no timing."
            );
        }

        if (record.Instrumental)
        {
            return LyricLookupResult.Instrumental(
                LyricSourceKind.Lrclib,
                record.Id,
                title,
                artist,
                album,
                duration,
                (int)Math.Round(score),
                explanation ?? "The lyric database lists this track as instrumental."
            );
        }

        _ = identity;
        _ = track;
        return LyricLookupResult.NotFound("The record has no lyric text.");
    }

    /// <summary>Turns a match into a presentable result.</summary>
    private static LyricLookupResult ToResult(LyricMatch match, SongIdentity identity, Track track)
    {
        if (match.Record is not { } record)
        {
            return LyricLookupResult.NotFound(
                $"No lyrics found for \"{identity.SongTitle ?? identity.RawTitle}\"."
            );
        }

        // An instrumental record must never be laid over a sung track's timeline, which is why the
        // matcher refuses to accept it automatically. When it is nonetheless the only plausible record,
        // asking the user to pick from a list of one is a worse answer than saying what the database
        // knows: this song is instrumental.
        if (match.Ranked.Count == 1 && record.Instrumental)
        {
            return ToResult(record, identity, track, match.Score, match.Explanation);
        }

        if (!match.IsConfident)
        {
            // Close finish or merely adequate score: offer the alternatives instead of guessing. The
            // record is still first in the list so the likeliest choice is one click away.
            // Several rows of the same song by the same performer are one answer, not several, so
            // the chooser shows each distinct song once. Otherwise a tie between two re-uploads of
            // the same recording fills the list with what looks like the same entry repeated.
            IReadOnlyList<LyricCandidate> candidates = match
                .Ranked.DistinctBy(pair =>
                    LyricText.Comparable(pair.Record.TrackName)
                    + "\u0000"
                    + LyricText.Comparable(pair.Record.ArtistName)
                )
                .Take(SelectionCandidateCount)
                .Select(pair => ToCandidate(pair.Record, pair.Score))
                .ToArray();

            if (candidates.Count == 0)
            {
                return LyricLookupResult.NotFound(
                    $"No lyrics found for \"{identity.SongTitle ?? identity.RawTitle}\"."
                );
            }

            string message =
                match.Score >= LyricMatcher.AutoAcceptScore
                    ? "Several lyric records fit this track. Pick the one you want."
                    : "The best lyric record for this track is not a confident match. Pick the right one.";

            return LyricLookupResult.NeedsSelection(candidates, message, match.Explanation);
        }

        return ToResult(record, identity, track, match.Score, match.Explanation);
    }

    /// <summary>Reduces a record to the chooser row.</summary>
    private static LyricCandidate ToCandidate(LrclibRecord record, double score) =>
        new(
            record.Id,
            record.TrackName ?? "Untitled",
            record.ArtistName,
            record.AlbumName,
            record.DurationSpan,
            record.Instrumental,
            record.HasSynced,
            record.HasPlain,
            Math.Round(score, 1)
        );

    /// <summary>
    /// Rebuilds a result from a cached entry, re-parsing the stored body.
    /// </summary>
    /// <remarks>
    /// The raw LRC is cached rather than the parsed lines so that a parser improvement applies to
    /// entries written by an older build.
    /// </remarks>
    private static LyricLookupResult? TryBuildFromCache(
        LyricCacheEntry entry,
        SongIdentity identity,
        CancellationToken cancellationToken
    )
    {
        _ = cancellationToken;
        _ = identity;

        if (entry.Negative)
        {
            return null;
        }

        TimeSpan? duration = entry.MatchedDuration;

        switch (entry.ResultKind)
        {
            case LyricKind.Synced when entry.SyncedLyrics is { Length: > 0 }:
                LrcDocument document = LrcParser.Parse(entry.SyncedLyrics);
                return document.IsEmpty
                    ? null
                    : LyricLookupResult.Synced(
                        document.Lines,
                        entry.ResultSource,
                        entry.RecordId,
                        entry.MatchedTitle,
                        entry.MatchedArtist,
                        entry.MatchedAlbum,
                        duration,
                        entry.MatchScore,
                        entry.Explanation
                    );

            case LyricKind.Plain when entry.PlainText is { Length: > 0 }:
                return LyricLookupResult.Plain(
                    entry.PlainText,
                    entry.ResultSource,
                    entry.RecordId,
                    entry.MatchedTitle,
                    entry.MatchedArtist,
                    entry.MatchedAlbum,
                    duration,
                    entry.MatchScore,
                    entry.Explanation
                );

            case LyricKind.Instrumental:
                return LyricLookupResult.Instrumental(
                    entry.ResultSource,
                    entry.RecordId,
                    entry.MatchedTitle,
                    entry.MatchedArtist,
                    entry.MatchedAlbum,
                    duration,
                    entry.MatchScore,
                    entry.Explanation
                );

            default:
                return null;
        }
    }

    /// <summary>Uses archived video subtitles, when the video has them.</summary>
    private Task<LyricLookupResult?> TryCaptionsAsync(
        Track track,
        CancellationToken cancellationToken
    )
    {
        _ = cancellationToken;

        if (!track.IsStream || track.StreamKey is not { } streamKey)
        {
            return Task.FromResult<LyricLookupResult?>(null);
        }

        IReadOnlyList<LyricLine> archived = _captions.Load(streamKey);
        if (archived.Count == 0)
        {
            return Task.FromResult<LyricLookupResult?>(null);
        }

        return Task.FromResult<LyricLookupResult?>(
            LyricLookupResult.Synced(
                archived,
                LyricSourceKind.VideoCaptions,
                explanation: "Timed by the video's own subtitle track."
            )
        );
    }

    /// <summary>Writes the outcome to the cache, preserving choices and offsets already stored.</summary>
    private async Task RememberAsync(
        Track track,
        string key,
        LyricCacheEntry? previous,
        LyricLookupResult result,
        long? chosenRecordId,
        CancellationToken cancellationToken
    )
    {
        _ = track;

        // A definite "no such song" is worth remembering briefly so that skipping past a track does not
        // re-query it every time. The entry expires on its own, so a record added later is still found.
        if (result.Kind is LyricKind.NotFound)
        {
            await _cache
                .SaveAsync(
                    new LyricCacheEntry
                    {
                        Key = key,
                        Kind = nameof(LyricKind.NotFound),
                        Negative = true,
                        FetchedAt = DateTimeOffset.UtcNow,
                        OffsetMilliseconds = previous?.OffsetMilliseconds ?? 0,
                        ManualRecordId = previous?.ManualRecordId,
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        // Only definite answers are cached. A failure is a property of the network at this moment, and
        // "needs selection" is a property of the matcher, not of the song.
        if (result.Kind is not (LyricKind.Synced or LyricKind.Plain or LyricKind.Instrumental))
        {
            return;
        }

        long? manual =
            chosenRecordId is not null ? chosenRecordId
            : previous?.ManualRecordId is { } kept && kept == result.RecordId ? kept
            : previous?.ManualRecordId;

        LyricCacheEntry entry = new()
        {
            Key = key,
            Kind = result.Kind.ToString(),
            Source = result.Source.ToString(),
            RecordId = result.RecordId,
            MatchedTitle = result.MatchedTitle,
            MatchedArtist = result.MatchedArtist,
            MatchedAlbum = result.MatchedAlbum,
            MatchedDurationSeconds =
                result.MatchedDuration is { } duration && duration > TimeSpan.Zero
                    ? (int)Math.Round(duration.TotalSeconds)
                    : null,
            MatchScore = result.MatchScore,
            Explanation = result.Explanation,
            SyncedLyrics = result.HasTimedLines ? RenderLrc(result.Lines) : null,
            PlainText = result.PlainText,
            FetchedAt = DateTimeOffset.UtcNow,
            Negative = false,
            OffsetMilliseconds = previous?.OffsetMilliseconds ?? 0,
            ManualRecordId = manual,
        };

        await _cache.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-renders parsed lines back into LRC so the cache stores one canonical body.
    /// </summary>
    /// <remarks>
    /// Round-tripping through the parser is deliberate: it guarantees a cached body is exactly what the
    /// parser can read back, so a cache hit can never behave differently from a fresh fetch.
    /// </remarks>
    private static string RenderLrc(IReadOnlyList<LyricLine> lines)
    {
        System.Text.StringBuilder builder = new();
        foreach (LyricLine line in lines)
        {
            int minutes = (int)line.Time.TotalMinutes;
            int seconds = line.Time.Seconds;
            int milliseconds = line.Time.Milliseconds;
            builder
                .Append('[')
                .Append(minutes.ToString("00", System.Globalization.CultureInfo.InvariantCulture))
                .Append(':')
                .Append(seconds.ToString("00", System.Globalization.CultureInfo.InvariantCulture))
                .Append('.')
                .Append(
                    milliseconds.ToString("000", System.Globalization.CultureInfo.InvariantCulture)
                )
                .Append(']')
                .Append(line.Text)
                .Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Builds the database query from what is actually known.</summary>
    private static LrclibQuery BuildQuery(SongIdentity identity) =>
        new(
            Title: identity.HasSongTitle ? identity.SongTitle : null,
            Artist: identity.HasArtist ? identity.Artist : null,
            Album: identity.HasAlbum ? identity.Album : null,
            // Duration is an exact filter on this API, so it is only sent when it is a real value.
            Duration: identity.HasDuration ? identity.Duration : null
        );

    /// <inheritdoc />
    public async Task<int> GetOffsetMillisecondsAsync(
        Track track,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(track);

        try
        {
            LyricCacheEntry? entry = await _cache
                .LoadAsync(LyricCacheStore.KeyFor(track), cancellationToken)
                .ConfigureAwait(false);
            return entry?.OffsetMilliseconds ?? 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <inheritdoc />
    public async Task SetOffsetMillisecondsAsync(
        Track track,
        int milliseconds,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(track);

        try
        {
            string key = LyricCacheStore.KeyFor(track);
            LyricCacheEntry? entry = await _cache
                .LoadAsync(key, cancellationToken)
                .ConfigureAwait(false);

            // The offset belongs to the track, not to a particular lyric body, so it is stored even
            // when nothing else about the track is cached yet.
            entry ??= new LyricCacheEntry
            {
                Key = key,
                Kind = nameof(LyricKind.NotFound),
                Negative = false,
                FetchedAt = DateTimeOffset.UtcNow,
            };

            await _cache
                .SaveAsync(entry with { OffsetMilliseconds = milliseconds }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Failing to store an offset is a nuisance, never a reason to interrupt playback.
        }
    }
}
