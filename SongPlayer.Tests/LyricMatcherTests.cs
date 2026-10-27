using SongPlayer.Models;
using SongPlayer.Services.Lyrics;

namespace SongPlayer.Tests;

/// <summary>
/// Tests for the candidate-scoring policy.
/// </summary>
/// <remarks>
/// Every case here is one that a live query actually produces: cover versions rated as synced
/// originals, uploader names mistaken for artists, "radiohead  creep" junk records whose name, artist
/// and album are all the same string, and two equally plausible records. The policy is engineering
/// judgement, so the tests pin the judgement rather than the numbers of a service contract.
/// </remarks>
public sealed class LyricMatcherTests
{
    private static SongIdentity Identity(
        string title,
        string? artist = null,
        TimeSpan? duration = null,
        SongVersion version = SongVersion.Studio,
        IdentitySource source = IdentitySource.TagMetadata) =>
        new(
            RawTitle: title,
            SongTitle: title,
            Artist: artist,
            Album: null,
            Duration: duration,
            Version: version,
            Source: source,
            SearchClue: null);

    private static LrclibRecord Record(
        long id,
        string track,
        string? artist,
        double? duration,
        bool synced = true,
        bool instrumental = false,
        string? album = null) =>
        new(
            Id: id,
            TrackName: track,
            ArtistName: artist,
            AlbumName: album,
            Duration: duration,
            Instrumental: instrumental,
            PlainLyrics: synced ? null : "plain",
            SyncedLyrics: synced ? "[00:01.00]line" : null);

    [Fact]
    public void A_matching_title_and_artist_is_accepted_without_asking()
    {
        LyricMatch match = new LyricMatcher().Match(
            Identity("Creep", "Radiohead", TimeSpan.FromSeconds(239)),
            [Record(496, "Creep", "Radiohead", 239)]);

        Assert.True(match.IsConfident);
        Assert.Equal(496, match.Record!.Id);
        Assert.True(match.Score >= LyricMatcher.AutoAcceptScore);
    }

    [Fact]
    public void A_record_for_a_different_artist_is_not_confident()
    {
        LyricMatch match = new LyricMatcher().Match(
            Identity("Creep", "Radiohead"),
            [Record(1, "Creep", "Postmodern Jukebox", 240)]);

        Assert.NotNull(match.Record);
        Assert.False(match.IsConfident);
        Assert.True(match.Score < LyricMatcher.AutoAcceptScore);
    }

    [Fact]
    public void A_record_for_a_different_song_is_rejected_outright()
    {
        LyricMatch match = new LyricMatcher().Match(
            Identity("Creep", "Radiohead"),
            [Record(1, "Wonderwall", "Oasis", 258)]);

        Assert.Null(match.Record);
        Assert.NotEmpty(match.Rejections);
    }

    [Fact]
    public void A_live_take_is_not_used_for_a_studio_track()
    {
        LyricMatch match = new LyricMatcher().Match(
            Identity("Creep", "Radiohead", TimeSpan.FromSeconds(239), SongVersion.Studio),
            [Record(2, "Creep (Live in Paris)", "Radiohead", 251)]);

        // A live performance has its own timing, so its timestamps must not be laid over the studio
        // release even though the name and artist agree.
        Assert.False(match.IsConfident);
        Assert.Contains(match.Rejections, note => note.Contains("version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_similar_score_with_a_close_runner_up_asks_the_user()
    {
        LyricMatch match = new LyricMatcher().Match(
            Identity("Creep", "Radiohead"),
            [
                Record(1, "Creep", "Radiohead", 239),
                Record(2, "Creep", "Radiohead Tribute Band", 240),
            ]);

        Assert.False(match.IsConfident);
        Assert.Equal(2, match.Ranked.Count);
        Assert.True(match.Score - match.RunnerUpScore < LyricMatcher.AutoAcceptMargin);
    }

    [Fact]
    public void An_unknown_artist_does_not_rule_out_a_title_match()
    {
        LyricMatch match = new LyricMatcher().Match(
            Identity("月亮代表我的心", artist: null, source: IdentitySource.VideoTitle),
            [Record(3, "月亮代表我的心", "邓丽君", 208)]);

        Assert.NotNull(match.Record);
        Assert.False(match.IsConfident);
        Assert.Contains("scored", match.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_uploader_shaped_junk_record_is_not_confident()
    {
        // The live search endpoint returns records whose name, artist and album are the raw query.
        LyricMatch match = new LyricMatcher().Match(
            Identity("radiohead creep", artist: null, source: IdentitySource.VideoTitle),
            [Record(30020794, "radiohead  creep", "radiohead  creep", 237)]);

        Assert.False(match.IsConfident);
    }

    [Fact]
    public void A_synced_record_is_never_preferred_over_a_name_mismatch()
    {
        LyricMatch match = new LyricMatcher().Match(
            Identity("Creep", "Radiohead"),
            [
                Record(1, "Wonderwall", "Oasis", 258, synced: true),
                Record(2, "Creep", "Radiohead", 239, synced: false),
            ]);

        Assert.Equal(2, match.Record!.Id);
    }

    [Fact]
    public void Duration_far_from_the_track_lowers_the_score_but_does_not_veto_it()
    {
        LyricMatch withDuration = new LyricMatcher().Match(
            Identity("Creep", "Radiohead", TimeSpan.FromSeconds(239)),
            [Record(1, "Creep", "Radiohead", 239)]);

        LyricMatch farOff = new LyricMatcher().Match(
            Identity("Creep", "Radiohead", TimeSpan.FromSeconds(239)),
            [Record(1, "Creep", "Radiohead", 420)]);

        Assert.True(withDuration.Score > farOff.Score);
        Assert.NotNull(farOff.Record);
    }

    [Fact]
    public void An_instrumental_record_does_not_answer_a_sung_track()
    {
        LyricMatch match = new LyricMatcher().Match(
            Identity("Photograph", "Ed Sheeran"),
            [Record(11490576, "Photograph (Instrumental Piano)", null, 258, synced: false, instrumental: true)]);

        Assert.False(match.IsConfident);
    }

    [Fact]
    public void Two_rows_of_the_same_song_are_one_candidate_not_an_ambiguity()
    {
        // The database holds the same recording several times at slightly different lengths. That is
        // one answer stored twice, so asking which one is meant would be a question with no wrong reply.
        LyricMatch match = new LyricMatcher().Match(
            Identity("月亮代表我的心", "邓丽君", TimeSpan.FromSeconds(209)),
            [
                Record(18453229, "月亮代表我的心", "邓丽君", 206.16),
                Record(27214605, "月亮代表我的心", "邓丽君", 211.11),
            ]);

        Assert.True(match.IsConfident);
        Assert.Null(match.RunnerUp);
        Assert.True(match.Score >= LyricMatcher.AutoAcceptScore);

        // Which of the two rows is chosen is decided by length alone, which the next test pins.
        Assert.Contains(match.Record!.Id, new long[] { 18453229, 27214605 });
    }

    [Fact]
    public void A_tie_between_the_same_song_by_the_same_performer_prefers_the_closer_length()
    {
        LyricMatcher match = new();
        LrclibRecord near = Record(2, "Yesterday", "The Beatles", 125);
        LrclibRecord far = Record(1, "Yesterday", "The Beatles", 260);

        LyricMatch result = match.Match(Identity("Yesterday", "The Beatles", TimeSpan.FromSeconds(124)), [far, near]);

        Assert.True(result.IsConfident);
        Assert.Equal(2, result.Record!.Id);
    }
}
