using System.Diagnostics;
using System.Windows;
using SongPlayer.Converters;
using SongPlayer.Models;
using SongPlayer.Services.Lyrics;
using SongPlayer.ViewModels;
using SongPlayer.Views.Overlays;

namespace SongPlayer.Tests;

/// <summary>
/// Hosts the real <see cref="NowPlayingView"/> against the real shell view model and fails on any
/// WPF data-binding error.
/// </summary>
/// <remarks>
/// XAML binding mistakes are silent at runtime: a typo in a path renders an empty chip and nothing
/// throws. This test is the only automated gate that reads the lyric card the way WPF does — it
/// subscribes to <see cref="PresentationTraceSources.DataBindingSource"/>, walks the same states the
/// user can reach (loading, lyrics, plain text, chooser, empty, offset), and asserts the trace stayed
/// quiet. It needs a real WPF <see cref="Application"/> with the real theme dictionaries, so it runs
/// on a dedicated STA thread and builds the resources from the app's own `pack://` URIs rather than
/// duplicating them.
/// </remarks>
public sealed class NowPlayingViewBindingTests
{
    [Fact]
    public void The_now_playing_view_binds_every_state_without_errors()
    {
        List<string> bindingErrors = [];
        Exception? failure = null;

        Thread thread = new(() =>
        {
            try
            {
                SourceLevels level = PresentationTraceSources.DataBindingSource.Switch.Level;
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
                CollectingListener listener = new(bindingErrors);
                PresentationTraceSources.DataBindingSource.Listeners.Add(listener);

                Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources.MergedDictionaries.Add(Theme("Colors"));
                foreach (string dictionary in new[] { "Typography", "Surfaces", "Buttons", "Inputs", "Navigation", "NowPlaying", "Library" })
                {
                    application.Resources.MergedDictionaries.Add(Theme(dictionary));
                }

                application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
                application.Resources["InverseBoolToVisibility"] = new BoolToVisibilityConverter { Invert = true };

                MainWindowViewModel shell = new();
                NowPlayingView view = new() { DataContext = shell };
                Window window = new() { Content = view, Width = 1100, Height = 700 };
                window.Show();
                window.UpdateLayout();

                // The demo track answers from the design-time provider, so the lyric card has rows
                // to lay out the moment it is selected.
                shell.NowPlaying.LoadFor(Track.Sample);
                shell.NowPlaying.ShowLyricsCommand.Execute(null);
                window.UpdateLayout();
                Assert.True(shell.NowPlaying.Lyrics.Count > 0, "The design-time lyric sheet should have rows.");

                // Every gate the card binds to must be reachable and self-consistent.
                Assert.True(shell.NowPlaying.CanShowLyrics, "The lyrics chip must never be disabled.");
                Assert.True(shell.NowPlaying.ShowsLyricList || shell.NowPlaying.ShowsPlainLyrics
                    || shell.NowPlaying.ShowsCandidateList || shell.NowPlaying.ShowsEmptyLyricState,
                    "Selecting lyrics must show exactly one body.");

                // The offset controls address a property chain that only exists in XAML.
                shell.NowPlaying.OffsetLaterCommand.Execute(null);
                Assert.Equal("+0.5 s", shell.NowPlaying.LyricOffsetText);
                shell.NowPlaying.ResetOffsetCommand.Execute(null);
                Assert.Equal("0.0 s", shell.NowPlaying.LyricOffsetText);

                shell.NowPlaying.ShowVisualizerCommand.Execute(null);
                window.UpdateLayout();
                Assert.True(shell.NowPlaying.IsVisualizerSelected, "Switching back to the visualiser must take.");

                window.Close();

                // The chooser is a second item template, and realising it is the only way to reach
                // the failure it once had: `Run.Text` binds two-way by default, so binding it to a
                // read-only property throws XamlParseException from inside a layout pass — an
                // unhandled exception that terminated the process, not a binding warning. The trace
                // listener above would stay silent for it, so this state is laid out for real.
                MainWindowViewModel chooserShell = new(
                    new ChooserLyricProvider(),
                    new EmptyLibrary(),
                    new NullFolderPicker(),
                    new NullConfirmationPrompt(),
                    new NullBilibiliSource(),
                    new CaptionStore());
                NowPlayingView chooserView = new() { DataContext = chooserShell };
                Window chooserWindow = new() { Content = chooserView, Width = 1100, Height = 700 };
                chooserWindow.Show();
                chooserWindow.UpdateLayout();

                chooserShell.NowPlaying.LoadFor(new Track("Creep", "Radiohead", TimeSpan.FromSeconds(239)));
                chooserWindow.UpdateLayout();

                Assert.Equal(ChooserLyricProvider.Rows.Count, chooserShell.NowPlaying.Candidates.Count);
                Assert.True(chooserShell.NowPlaying.ShowsCandidateList, "The chooser should be the visible body.");
                Assert.True(chooserShell.NowPlaying.IsLyricsSelected, "A chooser is a lyric answer, so it opens the card.");
                Assert.Contains("Pablo Honey", chooserShell.NowPlaying.Candidates[0].Detail, StringComparison.Ordinal);
                chooserWindow.Close();
                PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
                PresentationTraceSources.DataBindingSource.Switch.Level = level;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "The UI thread did not finish.");

        Assert.Null(failure);
        Assert.True(bindingErrors.Count == 0, "Binding errors: " + string.Join(" | ", bindingErrors));
    }

    private static ResourceDictionary Theme(string name) => new()
    {
        Source = new Uri($"pack://application:,,,/SongPlayer;component/Themes/{name}.xaml", UriKind.Absolute),
    };

    /// <summary>Collects the WPF binding trace instead of letting it fall through to stderr.</summary>
    private sealed class CollectingListener(List<string> sink) : TraceListener
    {
        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                sink.Add(message);
            }
        }
    }
}
