using SongPlayer.Models;
using SongPlayer.Services.Lyrics;
using SongPlayer.ViewModels.Shell;

namespace SongPlayer.Tests;

/// <summary>
/// Tests for the overlay's lyric state machine.
/// </summary>
/// <remarks>
/// Three things are easy to get wrong here and expensive to notice in use: a lookup that lands after
/// the user moved on overwriting the new track, a lookup that lands after the user deliberately chose
/// the visualizer stealing the view back, and a highlight that starts at line zero even though the song
/// is already at 1:30 by the time the lyrics arrive. Each of those is pinned below.
/// </remarks>
public sealed class NowPlayingViewModelTests
{
    private static readonly Track First = new(
        "Creep",
        "Radiohead",
        TimeSpan.FromSeconds(239),
        FilePath: @"D:\Music\Creep.flac");

    private static readonly Track Second = new(
        "Wonderwall",
        "Oasis",
        TimeSpan.FromSeconds(258),
        FilePath: @"D:\Music\Wonderwall.flac");

    private static LyricLookupResult Synced(params (double Seconds, string Text)[] lines) =>
        LyricLookupResult.Synced(
            lines.Select(line => new LyricLine(TimeSpan.FromSeconds(line.Seconds), line.Text)).ToArray(),
            LyricSourceKind.Lrclib,
            recordId: 1,
            title: "Creep",
            artist: "Radiohead",
            album: "Pablo Honey",
            duration: TimeSpan.FromSeconds(239),
            score: 92,
            explanation: "\"Creep\" by Radiohead scored 92");

    /// <summary>Polls until the condition holds, so a test never depends on scheduling luck.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Timed out waiting until " + because + ".");
    }

    [Fact]
    public async Task A_result_for_the_previous_track_never_reaches_the_new_one()
    {
        ScriptedLyricProvider provider = new();
        TaskCompletionSource<LyricLookupResult> first = provider.Hold(First);
        TaskCompletionSource<LyricLookupResult> second = provider.Hold(Second);
        NowPlayingViewModel viewModel = new(provider);

        viewModel.LoadFor(First);
        viewModel.LoadFor(Second);

        second.SetResult(Synced((5, "today is gonna be the day")));
        await WaitUntilAsync(() => viewModel.HasLyrics, "the second track's lyrics arrive");

        // The abandoned request finishes late and must be ignored, not applied on top.
        first.SetResult(Synced((5, "when you were here before")));
        await Task.Delay(100);

        Assert.Equal("today is gonna be the day", viewModel.Lyrics[0].Text);
        Assert.DoesNotContain(viewModel.Lyrics, line => line.Text.Contains("here before", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lyrics_that_arrive_after_the_user_picked_the_visualizer_do_not_steal_the_view()
    {
        ScriptedLyricProvider provider = new();
        TaskCompletionSource<LyricLookupResult> pending = provider.Hold(First);
        NowPlayingViewModel viewModel = new(provider);

        viewModel.LoadFor(First);
        viewModel.ShowVisualizerCommand.Execute(null);

        pending.SetResult(Synced((5, "when you were here before")));
        await WaitUntilAsync(() => viewModel.HasLyrics, "the lyrics arrive");

        Assert.Equal(OverlayView.Visualizer, viewModel.ActiveView);
        Assert.True(viewModel.IsVisualizerSelected);
    }

    [Fact]
    public async Task Lyrics_that_arrive_unasked_switch_the_card_to_the_lyric_list()
    {
        ScriptedLyricProvider provider = new();
        TaskCompletionSource<LyricLookupResult> pending = provider.Hold(First);
        NowPlayingViewModel viewModel = new(provider);

        viewModel.LoadFor(First);
        Assert.Equal(OverlayView.Visualizer, viewModel.ActiveView);

        pending.SetResult(Synced((5, "when you were here before")));
        await WaitUntilAsync(() => viewModel.HasLyrics, "the lyrics arrive");

        Assert.Equal(OverlayView.Lyrics, viewModel.ActiveView);
        Assert.Equal("LYRICS · LRCLIB", viewModel.SectionLabel);
    }

    [Fact]
    public async Task A_late_result_highlights_the_line_playing_right_now()
    {
        ScriptedLyricProvider provider = new();
        TaskCompletionSource<LyricLookupResult> pending = provider.Hold(First);
        NowPlayingViewModel viewModel = new(provider);

        viewModel.LoadFor(First);

        // The song is already past the second line when the answer lands.
        viewModel.UpdateActiveLyric(TimeSpan.FromSeconds(15));
        pending.SetResult(Synced((0, "a"), (10, "b"), (20, "c")));

        // The rows and the highlight are published together, so waiting for them separately could
        // observe the rows before the highlight lands and make this test flaky. Wait for the state
        // that is actually asserted.
        await WaitUntilAsync(() => viewModel.ActiveLyricIndex == 1, "the line playing right now is highlighted");

        Assert.Equal(1, viewModel.ActiveLyricIndex);
        Assert.True(viewModel.Lyrics[1].IsActive);
        Assert.False(viewModel.Lyrics[0].IsActive);
    }

    [Fact]
    public async Task The_offset_moves_the_highlight_and_the_click_by_the_same_amount()
    {
        ScriptedLyricProvider provider = new();
        provider.Immediate = Synced((0, "a"), (10, "b"), (20, "c"));
        NowPlayingViewModel viewModel = new(provider);

        viewModel.LoadFor(First);
        await WaitUntilAsync(() => viewModel.HasLyrics, "the lyrics arrive");

        // Offset +0.5 s means a line stamped 10 s is on screen at playback 10.5 s, and clicking that
        // line seeks to the same moment.
        viewModel.OffsetLaterCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.LyricOffsetMilliseconds == 500, "the offset is applied");

        viewModel.UpdateActiveLyric(TimeSpan.FromSeconds(10.5));
        Assert.Equal(1, viewModel.ActiveLyricIndex);

        // Clicking that line has to seek to the same place the highlight was computed for.
        Assert.Equal(TimeSpan.FromSeconds(10.5), viewModel.PlaybackPositionFor(viewModel.Lyrics[1]));

        viewModel.UpdateActiveLyric(TimeSpan.FromSeconds(10));
        Assert.Equal(0, viewModel.ActiveLyricIndex);
    }

    [Fact]
    public async Task A_negative_offset_moves_the_highlight_earlier()
    {
        ScriptedLyricProvider provider = new();
        provider.Immediate = Synced((0, "a"), (10, "b"), (20, "c"));
        NowPlayingViewModel viewModel = new(provider);

        viewModel.LoadFor(First);
        await WaitUntilAsync(() => viewModel.HasLyrics, "the lyrics arrive");

        viewModel.OffsetEarlierCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.LyricOffsetMilliseconds == -500, "the offset is applied");

        // Offset -0.5 s means the line stamped 10 s is on screen at playback 9.5 s.
        viewModel.UpdateActiveLyric(TimeSpan.FromSeconds(9.5));
        Assert.Equal(1, viewModel.ActiveLyricIndex);
        Assert.Equal(TimeSpan.FromSeconds(9.5), viewModel.PlaybackPositionFor(viewModel.Lyrics[1]));
    }

    [Fact]
    public async Task Resetting_the_offset_returns_to_the_stored_timeline()
    {
        ScriptedLyricProvider provider = new();
        provider.Immediate = Synced((0, "a"), (10, "b"));
        NowPlayingViewModel viewModel = new(provider);

        viewModel.LoadFor(First);
        await WaitUntilAsync(() => viewModel.HasLyrics, "the lyrics arrive");

        viewModel.OffsetLaterCommand.Execute(null);
        viewModel.OffsetLaterCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.LyricOffsetMilliseconds == 1000, "both nudges are applied");

        viewModel.ResetOffsetCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.LyricOffsetMilliseconds == 0, "the offset is cleared");

        Assert.False(viewModel.HasLyricOffset);
    }

    [Fact]
    public async Task A_stored_offset_is_loaded_with_the_track()
    {
        ScriptedLyricProvider provider = new();
        provider.Immediate = Synced((10, "b"));
        provider.Offsets["D:\\Music\\Creep.flac"] = -1500;
        NowPlayingViewModel viewModel = new(provider);

        viewModel.LoadFor(First);

        await WaitUntilAsync(() => viewModel.LyricOffsetMilliseconds == -1500, "the stored offset is applied");
        Assert.Equal("-1.5 s", viewModel.LyricOffsetText);
    }

    [Fact]
    public async Task Plain_lyrics_are_shown_without_a_line_list()
    {
        ScriptedLyricProvider provider = new();
        provider.Immediate = LyricLookupResult.Plain(
            "line one\nline two",
            LyricSourceKind.Lrclib,
            recordId: 3,
            title: "Creep",
            artist: "Radiohead",
            album: "Pablo Honey",
            duration: TimeSpan.FromSeconds(239),
            score: 90,
            explanation: null);
        NowPlayingViewModel viewModel = new(provider);

        viewModel.LoadFor(First);
        await WaitUntilAsync(() => viewModel.HasPlainText, "the plain lyrics arrive");

        Assert.Equal(OverlayView.Lyrics, viewModel.ActiveView);
        Assert.False(viewModel.HasLyrics);
        Assert.True(viewModel.ShowsPlainLyrics);
        Assert.Contains("line two", viewModel.PlainText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LyricKind.NotFound, "No lyrics found")]
    [InlineData(LyricKind.Instrumental, "instrumental")]
    [InlineData(LyricKind.Failed, "Could not reach")]
    public async Task A_lookup_that_has_nothing_to_show_keeps_the_visualizer_and_says_why(
        LyricKind kind,
        string expected)
    {
        ScriptedLyricProvider provider = new();
        provider.Immediate = kind switch
        {
            LyricKind.Instrumental => LyricLookupResult.Instrumental(
                LyricSourceKind.Lrclib, 5, "Photograph", "Ed Sheeran", null, TimeSpan.FromSeconds(258), 90, null),
            LyricKind.Failed => LyricLookupResult.Failed("The lyric database is busy."),
            _ => LyricLookupResult.NotFound("No lyrics found for \"Creep\"."),
        };
        NowPlayingViewModel viewModel = new(provider);

        viewModel.LoadFor(First);
        await WaitUntilAsync(() => !viewModel.IsLoading, "the lookup finishes");

        Assert.Equal(OverlayView.Visualizer, viewModel.ActiveView);
        Assert.False(viewModel.HasPresentableContent);
        Assert.Contains(expected, viewModel.EmptyTitle + viewModel.EmptyHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_lyric_entry_stays_usable_even_when_there_are_no_lyrics()
    {
        ScriptedLyricProvider provider = new() { Immediate = LyricLookupResult.NotFound("none") };
        NowPlayingViewModel viewModel = new(provider);

        viewModel.LoadFor(First);
        await WaitUntilAsync(() => !viewModel.IsLoading, "the lookup finishes");

        Assert.True(viewModel.CanShowLyrics);
        viewModel.ShowLyricsCommand.Execute(null);
        Assert.True(viewModel.IsLyricsSelected);
        Assert.True(viewModel.ShowsEmptyLyricState);
    }

    [Fact]
    public async Task Ambiguous_results_offer_a_chooser_and_picking_one_reloads_by_record()
    {
        ScriptedLyricProvider provider = new();
        LyricCandidate candidate = new(7, "Creep", "Radiohead", "Pablo Honey", TimeSpan.FromSeconds(239), false, true, false, 70);
        provider.Immediate = LyricLookupResult.NeedsSelection([candidate], "Pick the right one.", "close call");
        NowPlayingViewModel viewModel = new(provider);

        viewModel.LoadFor(First);
        await WaitUntilAsync(() => viewModel.HasCandidates, "the candidates arrive");

        Assert.True(viewModel.ShowsCandidateList);
        Assert.Single(viewModel.Candidates);
        Assert.Equal("Synced", viewModel.Candidates[0].TypeLabel);

        viewModel.Candidates[0].SelectCommand.Execute(null);
        await WaitUntilAsync(() => provider.Requests.Count == 2, "the chosen record is requested");

        Assert.Equal(7, provider.Requests[1].Options.RecordId);
    }

    [Fact]
    public async Task A_new_track_clears_the_previous_lines_before_its_own_arrive()
    {
        ScriptedLyricProvider provider = new();
        provider.Immediate = Synced((0, "when you were here before"));
        NowPlayingViewModel viewModel = new(provider);

        viewModel.LoadFor(First);
        await WaitUntilAsync(() => viewModel.HasLyrics, "the first track's lyrics arrive");

        TaskCompletionSource<LyricLookupResult> pending = provider.Hold(Second);
        viewModel.LoadFor(Second);

        Assert.Empty(viewModel.Lyrics);
        Assert.True(viewModel.IsLoading);

        pending.SetResult(Synced((0, "today is gonna be the day")));
        await WaitUntilAsync(() => !viewModel.IsLoading, "the second track's lyrics arrive");
        Assert.Equal("today is gonna be the day", viewModel.Lyrics[0].Text);
    }

    [Fact]
    public async Task A_failed_lookup_can_be_retried_and_the_retry_forces_a_refresh()
    {
        ScriptedLyricProvider provider = new() { Immediate = LyricLookupResult.Failed("busy") };
        NowPlayingViewModel viewModel = new(provider);

        viewModel.LoadFor(First);
        await WaitUntilAsync(() => viewModel.CanRetry, "the failure is visible");

        Assert.True(viewModel.CanRetry);
        viewModel.RetryCommand.Execute(null);
        await WaitUntilAsync(() => provider.Requests.Count == 2, "the retry is requested");

        Assert.True(provider.Requests[1].Options.ForceRefresh);
        Assert.True(provider.Requests[1].Options.ClearManualChoice);
    }

    [Fact]
    public async Task Cancelling_a_pending_lookup_drops_its_result()
    {
        ScriptedLyricProvider provider = new();
        TaskCompletionSource<LyricLookupResult> pending = provider.Hold(First);
        NowPlayingViewModel viewModel = new(provider);

        viewModel.LoadFor(First);
        viewModel.CancelPendingLookup();

        pending.SetResult(Synced((0, "when you were here before")));
        await Task.Delay(100);

        Assert.Empty(viewModel.Lyrics);
    }
}
