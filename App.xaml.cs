using System.Windows;
using SongPlayer.Services.Audio;
using SongPlayer.Services.Lyrics;
using SongPlayer.ViewModels;

namespace SongPlayer;

/// <summary>
/// Application entry point and composition root.
/// </summary>
/// <remarks>
/// The window is created here instead of through <c>StartupUri</c> so the services can be built
/// first and injected into the shell view model. Dependencies are wired by hand — the object
/// graph is small enough that a container would only add indirection — and each construction site
/// is explicit about which implementation it gets, which is what makes swapping the silent
/// player for a real engine a one-line change.
/// </remarks>
public partial class App : Application
{
    /// <summary>
    /// Builds the object graph and shows the shell window.
    /// </summary>
    /// <param name="e">Start-up arguments.</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        IAudioPlayer audioPlayer = new SilentAudioPlayer();
        ILyricProvider lyricProvider = new SampleLyricProvider();

        // The library services (IMusicLibraryService over AudioFileScanner,
        // TrackMetadataReader and LibraryStore) are constructed here as well once the Library
        // page owns a collection to fill; today the shell shows a single track, so instantiating
        // them would only create a graph nobody consumes.
        MainWindowViewModel viewModel = new(lyricProvider, audioPlayer);

        MainWindow = new MainWindow { DataContext = viewModel };
        MainWindow.Show();
    }
}
