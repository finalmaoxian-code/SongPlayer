using SongPlayer.Models;
using SongPlayer.Services.Lyrics;

namespace SongPlayer.Tests;

/// <summary>
/// Tests for the lookup orchestration: what is queried, what is cached, and what the user is shown.
/// </summary>
/// <remarks>
/// The behaviour that matters is not the happy path but the four ways a lookup can end badly. A miss
/// may be remembered for a while so skipping through tracks does not re-query the same absence; a
/// network failure may never be remembered, or a temporary outage would be frozen into "this song has
/// no lyrics"; an ambiguous answer must ask rather than guess; and none of it may cost the song its
/// playback or its earlier corrections.
/// </remarks>
public sealed class TrackLyricProviderTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "SongPlayer.Tests",
        Guid.NewGuid().ToString("N"));

    private readonly FakeLrclibClient _client = new();

    private readonly FakeCaptionArchive _captions = new();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }

    private TrackLyricProvider Provider() => new(
        _client,
        new LyricMatcher(),
        new LyricCacheStore(_folder),
        new SongIdentityResolver(),
        _captions);

    private static Track Local(string title = "Creep", string artist = "Radiohead", string file = "Creep.flac") =>
        new(
            Title: title,
            Artist: artist,
            Duration: TimeSpan.FromSeconds(239),
            FilePath: Path.Combine(Path.GetTempPath(), "Music", file),
            Album: "Pablo Honey");

    private static Track Online(string title = "Creep", string uploader = "Some Channel", string id = "BV1xx411c7mD") =>
        new(
            Title: title,
            Artist: uploader,
            Duration: TimeSpan.FromSeconds(239),
            StreamKey: "bilibili:" + id,
            StreamUri: "https://example.invalid/audio.m4a");

    private static LrclibRecord Synced(long id = 496, string track = "Creep", string? artist = "Radiohead") =>
        new(
            Id: id,
            TrackName: track,
            ArtistName: artist,
            AlbumName: "Pablo Honey",
            Duration: 239,
            Instrumental: false,
            PlainLyrics: "When you were here before",
            SyncedLyrics: "[00:19.160]When you were here before\n[00:24.500]Couldn't look you in the eye");

    [Fact]
    public async Task A_trusted_title_is_queried_exactly_and_comes_back_synced()
    {
        _client.ExactResult = Synced();

        LyricLookupResult result = await Provider().GetLyricsAsync(Local());

        Assert.Equal(LyricKind.Synced, result.Kind);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(LyricSourceKind.Lrclib, result.Source);
        Assert.Equal(496, result.RecordId);
        Assert.Single(_client.ExactQueries);
        Assert.Empty(_client.SearchQueries);
    }

    [Fact]
    public async Task A_record_without_timing_comes_back_as_plain_text()
    {
        _client.ExactResult = Synced() with
        {
            SyncedLyrics = null,
            PlainLyrics = "When you were here before\nCouldn't look you in the eye",
        };

        LyricLookupResult result = await Provider().GetLyricsAsync(Local());

        Assert.Equal(LyricKind.Plain, result.Kind);
        Assert.Contains("When you were here", result.PlainText, StringComparison.Ordinal);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public async Task A_synced_body_that_parses_to_nothing_falls_back_to_the_plain_body()
    {
        _client.ExactResult = Synced() with
        {
            SyncedLyrics = "not a timestamp in sight",
            PlainLyrics = "plain body",
        };

        LyricLookupResult result = await Provider().GetLyricsAsync(Local());

        Assert.Equal(LyricKind.Plain, result.Kind);
        Assert.Equal("plain body", result.PlainText);
    }

    [Fact]
    public async Task An_instrumental_record_is_reported_as_instrumental()
    {
        _client.ExactResult = Synced() with
        {
            Instrumental = true,
            SyncedLyrics = null,
            PlainLyrics = null,
        };

        LyricLookupResult result = await Provider().GetLyricsAsync(Local());

        Assert.Equal(LyricKind.Instrumental, result.Kind);
        Assert.False(result.HasContent);
    }

    [Fact]
    public async Task A_missing_record_is_a_miss_not_a_failure()
    {
        _client.ExactResult = null;
        _client.SearchResults = [];

        LyricLookupResult result = await Provider().GetLyricsAsync(Local());

        Assert.Equal(LyricKind.NotFound, result.Kind);
        Assert.True(result.CanRetry);
    }

    [Fact]
    public async Task A_transport_failure_is_reported_and_never_cached()
    {
        _client.Failure = new LrclibException(LrclibFailureKind.Unavailable, "The lyric database is busy.");

        TrackLyricProvider provider = Provider();
        LyricLookupResult first = await provider.GetLyricsAsync(Local());
        LyricLookupResult second = await provider.GetLyricsAsync(Local());

        Assert.Equal(LyricKind.Failed, first.Kind);
        Assert.Equal(LyricKind.Failed, second.Kind);

        // The second lookup must have gone back to the network instead of replaying a cached failure.
        Assert.Equal(2, _client.ExactQueries.Count);
    }

    [Fact]
    public async Task A_miss_is_remembered_so_the_same_song_is_not_queried_twice()
    {
        _client.ExactResult = null;
        _client.SearchResults = [];

        TrackLyricProvider provider = Provider();
        LyricLookupResult first = await provider.GetLyricsAsync(Local());
        LyricLookupResult second = await provider.GetLyricsAsync(Local());

        Assert.Equal(LyricKind.NotFound, first.Kind);
        Assert.Equal(LyricKind.NotFound, second.Kind);
        Assert.Single(_client.ExactQueries);
    }

    [Fact]
    public async Task A_forced_re_match_queries_again()
    {
        _client.ExactResult = null;
        _client.SearchResults = [];

        TrackLyricProvider provider = Provider();
        await provider.GetLyricsAsync(Local());
        await provider.GetLyricsAsync(Local(), new LyricLookupOptions(ForceRefresh: true));

        Assert.Equal(2, _client.ExactQueries.Count);
    }

    [Fact]
    public async Task A_cached_result_is_reused_without_touching_the_network()
    {
        _client.ExactResult = Synced();

        TrackLyricProvider provider = Provider();
        await provider.GetLyricsAsync(Local());
        LyricLookupResult cached = await provider.GetLyricsAsync(Local());

        Assert.Equal(LyricKind.Synced, cached.Kind);
        Assert.Equal(2, cached.Lines.Count);
        Assert.Single(_client.ExactQueries);
    }

    [Fact]
    public async Task Two_tracks_with_the_same_name_do_not_share_a_cache_entry()
    {
        _client.ExactResult = Synced();

        TrackLyricProvider provider = Provider();
        await provider.GetLyricsAsync(Local(artist: "Radiohead", file: "Creep.flac"));
        await provider.GetLyricsAsync(Local(artist: "Postmodern Jukebox", file: "Creep (cover).flac"));

        Assert.Equal(2, _client.ExactQueries.Count);
        Assert.NotEqual(
            LyricCacheStore.KeyFor(Local(artist: "Radiohead", file: "Creep.flac")),
            LyricCacheStore.KeyFor(Local(artist: "Postmodern Jukebox", file: "Creep (cover).flac")));
    }

    [Fact]
    public async Task A_corrupt_cache_entry_is_ignored_rather_than_fatal()
    {
        _client.ExactResult = Synced();
        string key = LyricCacheStore.KeyFor(Local());

        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, key + ".json"), "{ this is not json");

        LyricLookupResult result = await Provider().GetLyricsAsync(Local());

        Assert.Equal(LyricKind.Synced, result.Kind);
    }

    [Fact]
    public async Task An_ambiguous_answer_offers_candidates_instead_of_guessing()
    {
        _client.ExactResult = null;
        _client.SearchResults =
        [
            Synced(1, "Creep", "Radiohead"),
            new LrclibRecord(2, "Creep", "Radiohead Tribute Band", "Tributes", 239, false, "plain", null),
        ];

        // A video title is a clue rather than a fact, so no exact query is attempted and the search
        // path decides between two plausible records.
        LyricLookupResult result = await Provider().GetLyricsAsync(Online(title: "Creep"));

        Assert.Equal(LyricKind.NeedsSelection, result.Kind);
        Assert.Equal(2, result.Candidates.Count);
        Assert.True(result.CanRetry);
        Assert.Equal("Synced", result.Candidates[0].TypeLabel);
        Assert.Equal("Plain text", result.Candidates[1].TypeLabel);
    }

    [Fact]
    public async Task Picking_a_candidate_uses_that_record_and_remembers_the_choice()
    {
        _client.ExactResult = null;
        _client.SearchResults =
        [
            Synced(1, "Creep", "Radiohead"),
            new LrclibRecord(2, "Creep", "Radiohead Tribute Band", "Tributes", 239, false, "plain", null),
        ];

        Track track = Online();
        TrackLyricProvider provider = Provider();
        LyricLookupResult ambiguous = await provider.GetLyricsAsync(track);
        _client.ByIdResult = new LrclibRecord(2, "Creep", "Radiohead Tribute Band", "Tributes", 239, false, "plain", null);

        LyricLookupResult chosen = await provider.GetLyricsAsync(
            track,
            new LyricLookupOptions(RecordId: ambiguous.Candidates[1].RecordId));

        Assert.Equal(LyricKind.Plain, chosen.Kind);
        Assert.Equal(2, chosen.RecordId);
        Assert.Contains("you chose", chosen.Explanation, StringComparison.OrdinalIgnoreCase);

        // The correction must outrank matching on the next load, with no further search.
        int searchesBefore = _client.SearchQueries.Count;
        LyricLookupResult remembered = await provider.GetLyricsAsync(track);
        Assert.Equal(2, remembered.RecordId);
        Assert.Equal(searchesBefore, _client.SearchQueries.Count);
    }

    [Fact]
    public async Task Clearing_the_manual_choice_goes_back_to_automatic_matching()
    {
        _client.ExactResult = null;
        _client.SearchResults =
        [
            Synced(1, "Creep", "Radiohead"),
            new LrclibRecord(2, "Creep", "Radiohead Tribute Band", "Tributes", 239, false, "plain", null),
        ];

        Track track = Online();
        TrackLyricProvider provider = Provider();
        await provider.GetLyricsAsync(track);
        _client.ByIdResult = Synced(1, "Creep", "Radiohead");
        await provider.GetLyricsAsync(track, new LyricLookupOptions(RecordId: 1));

        LyricLookupResult afterClear = await provider.GetLyricsAsync(
            track,
            new LyricLookupOptions(ForceRefresh: true, ClearManualChoice: true));

        Assert.Equal(LyricKind.NeedsSelection, afterClear.Kind);
        Assert.DoesNotContain(_client.RequestedIds, id => id == 1 && _client.RequestedIds.Count(id2 => id2 == 1) > 1);
    }

    [Fact]
    public async Task An_offset_is_stored_per_track_and_survives_a_new_provider()
    {
        Track track = Local();

        await Provider().SetOffsetMillisecondsAsync(track, 1500);

        Assert.Equal(1500, await Provider().GetOffsetMillisecondsAsync(track));
        Assert.Equal(0, await Provider().GetOffsetMillisecondsAsync(Local(file: "Other.flac")));
    }

    [Fact]
    public async Task Video_subtitles_are_used_when_the_video_has_them_and_labelled_as_such()
    {
        _captions.Archived["bilibili:BV1xx411c7mD"] =
        [
            new LyricLine(TimeSpan.FromSeconds(1), "from the video"),
        ];

        LyricLookupResult result = await Provider().GetLyricsAsync(Online());

        Assert.Equal(LyricKind.Synced, result.Kind);
        Assert.Equal(LyricSourceKind.VideoCaptions, result.Source);
        Assert.Empty(_client.ExactQueries);
        Assert.Empty(_client.SearchQueries);
    }

    [Fact]
    public async Task A_real_song_never_falls_back_to_the_demo_lyrics()
    {
        _client.ExactResult = null;
        _client.SearchResults = [];

        LyricLookupResult result = await Provider().GetLyricsAsync(Local());

        Assert.Equal(LyricKind.NotFound, result.Kind);
        Assert.Empty(result.Lines);
        Assert.NotEqual(LyricSourceKind.Sample, result.Source);
    }

    [Fact]
    public async Task The_demo_track_still_answers_with_the_built_in_sample()
    {
        LyricLookupResult result = await Provider().GetLyricsAsync(Track.Sample);

        Assert.Equal(LyricSourceKind.Sample, result.Source);
        Assert.NotEmpty(result.Lines);
        Assert.Empty(_client.ExactQueries);
    }

    [Fact]
    public async Task An_uploader_name_is_not_sent_as_the_artist()
    {
        _client.ExactResult = null;
        _client.SearchResults = [Synced(1, "Creep", "Radiohead")];

        await Provider().GetLyricsAsync(Online("Creep", uploader: "Some Upload Channel"));

        Assert.All(_client.SearchQueries, query => Assert.Null(query.Artist));
    }

    [Fact]
    public async Task An_unknown_duration_is_not_sent_to_the_exact_endpoint()
    {
        _client.ExactResult = Synced();
        Track untimed = new(
            Title: "Creep",
            Artist: "Radiohead",
            Duration: TimeSpan.Zero,
            FilePath: Path.Combine(Path.GetTempPath(), "Music", "untimed.flac"));

        await Provider().GetLyricsAsync(untimed);

        Assert.Null(Assert.Single(_client.ExactQueries).Duration);
    }

    [Fact]
    public async Task A_quoted_video_title_is_searched_under_the_song_name_it_quotes()
    {
        // The reported upload: searching the whole title matches nothing, the quoted name matches.
        _client.SearchResults = [Synced(18453229, "月亮代表我的心", "邓丽君")];
        Track upload = new(
            Title: "邓丽君《月亮代表我的心》百万豪装录音棚大声听",
            Artist: "JLRS-LeoFM",
            Duration: TimeSpan.FromSeconds(209),
            StreamKey: "bilibili:BV1Hb411v7Hm",
            StreamUri: "https://example.invalid/audio.m4a");

        LyricLookupResult result = await Provider().GetLyricsAsync(upload);

        Assert.Equal(LyricKind.Synced, result.Kind);
        Assert.Equal("月亮代表我的心", result.MatchedTitle);
        Assert.Contains(_client.SearchQueries, query => query.Keyword!.Contains("月亮代表我的心", StringComparison.Ordinal));
        Assert.DoesNotContain(_client.SearchQueries, query => query.Keyword!.Contains("百万豪装", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_row_of_the_best_song_does_not_turn_the_answer_into_a_chooser()
    {
        // Two lengths of the same recording: one answer stored twice, so no choice needs asking.
        _client.SearchResults =
        [
            Synced(18453229, "月亮代表我的心", "邓丽君"),
            Synced(27214605, "月亮代表我的心", "邓丽君"),
        ];

        LyricLookupResult result = await Provider().GetLyricsAsync(Local("月亮代表我的心", "邓丽君"));

        Assert.Equal(LyricKind.Synced, result.Kind);
        Assert.Empty(result.Candidates);
    }
}
