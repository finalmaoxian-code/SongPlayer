using System.Windows.Threading;
using SongPlayer.Models;
using SongPlayer.ViewModels.Shell;

namespace SongPlayer.Tests;

/// <summary>
/// Proves that a lyric lookup publishes on the thread that owns the view model, not on whichever
/// thread pool thread happened to complete the request.
/// </summary>
/// <remarks>
/// <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/> refuses changes from a
/// thread other than the one that owns its collection view. Before this was pinned down, publishing
/// relied on the awaited provider happening to resume on the caller's context: correct in the app,
/// but silently broken the moment a lookup was started from anywhere else, and it made any view
/// bound to a lookup fail on a background completion. These tests drive a real
/// <see cref="Dispatcher"/> so the rule is asserted, not assumed.
/// </remarks>
public sealed class NowPlayingViewModelThreadingTests
{
    private static readonly Track First =
        new("Creep", "Radiohead", TimeSpan.FromSeconds(239), FilePath: @"D:\Music\Creep.flac");

    [Fact]
    public void A_result_completed_on_a_pool_thread_still_publishes_on_the_dispatcher()
    {
        Exception? failure = null;
        string? publishThreadKind = null;
        int lineCount = 0;

        Thread thread = new(() =>
        {
            try
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                ScriptedLyricProvider provider = new();
                TaskCompletionSource<LyricLookupResult> pending = provider.Hold(First);
                NowPlayingViewModel viewModel = new(provider);

                // The rule under test is about the thread that mutates the bound collection, so the
                // observation has to happen at that moment, not on the test thread afterwards.
                bool changedOnDispatcher = true;
                viewModel.Lyrics.CollectionChanged += (_, _) =>
                {
                    changedOnDispatcher &= dispatcher.CheckAccess();
                };

                viewModel.LoadFor(First);

                // Complete the request from a pool thread while the dispatcher keeps pumping, which
                // is what a real HTTP continuation looks like.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(25);
                    await dispatcher.InvokeAsync(() => viewModel.UpdateActiveLyric(TimeSpan.FromSeconds(15)));
                    pending.SetResult(LyricLookupResult.Synced(
                        [new LyricLine(TimeSpan.Zero, "a"), new LyricLine(TimeSpan.FromSeconds(10), "b")],
                        LyricSourceKind.Lrclib,
                        recordId: 496,
                        title: "Creep",
                        artist: "Radiohead",
                        album: null,
                        duration: TimeSpan.FromSeconds(239),
                        score: 88,
                        explanation: "matched"));
                });

                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    PumpOnce(dispatcher);
                    if (!viewModel.IsLoading && viewModel.Lyrics.Count > 0)
                    {
                        break;
                    }
                }

                lineCount = viewModel.Lyrics.Count;
                publishThreadKind = changedOnDispatcher ? "dispatcher" : "other";
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The UI thread did not finish.");

        Assert.Null(failure);
        Assert.Equal("dispatcher", publishThreadKind);
        Assert.Equal(2, lineCount);
    }

    [Fact]
    public void A_result_that_arrives_after_a_track_change_is_dropped()
    {
        Exception? failure = null;
        int published = -1;
        string? label = null;

        Thread thread = new(() =>
        {
            try
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                ScriptedLyricProvider provider = new();
                TaskCompletionSource<LyricLookupResult> firstPending = provider.Hold(First);
                NowPlayingViewModel viewModel = new(provider);

                viewModel.LoadFor(First);

                Track second = new("Wonderwall", "Oasis", TimeSpan.FromSeconds(258), FilePath: @"D:\Music\Wonderwall.flac");
                provider.Immediate = LyricLookupResult.Synced(
                    [new LyricLine(TimeSpan.Zero, "today is gonna be the day")],
                    LyricSourceKind.Lrclib,
                    recordId: 12,
                    title: "Wonderwall",
                    artist: "Oasis",
                    album: null,
                    duration: TimeSpan.FromSeconds(258),
                    score: 90,
                    explanation: "matched");

                viewModel.LoadFor(second);

                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    PumpOnce(dispatcher);
                    if (!viewModel.IsLoading && viewModel.Lyrics.Count > 0)
                    {
                        break;
                    }
                }

                // The stale track's answer lands only now; it must not overwrite the new song.
                firstPending.SetResult(LyricLookupResult.Synced(
                    [new LyricLine(TimeSpan.Zero, "stale line one"), new LyricLine(TimeSpan.FromSeconds(5), "stale line two")],
                    LyricSourceKind.Lrclib,
                    recordId: 496,
                    title: "Creep",
                    artist: "Radiohead",
                    album: null,
                    duration: TimeSpan.FromSeconds(239),
                    score: 88,
                    explanation: "matched"));

                for (int i = 0; i < 40; i++)
                {
                    PumpOnce(dispatcher);
                }

                published = viewModel.Lyrics.Count;
                label = viewModel.Lyrics.Count > 0 ? viewModel.Lyrics[0].Text : null;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The UI thread did not finish.");

        Assert.Null(failure);
        Assert.Equal(1, published);
        Assert.Equal("today is gonna be the day", label);
    }

    private static void PumpOnce(Dispatcher dispatcher)
    {
        DispatcherFrame frame = new();
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Thread.Sleep(5);
    }
}
