using System.Windows;
using SongPlayer.Services.Audio;
using SongPlayer.Services.Library;
using SongPlayer.Services.Lyrics;
using SongPlayer.Services.Storage;
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
    /// Builds the object graph, shows the shell window and restores the saved library.
    /// </summary>
    /// <param name="e">Start-up arguments.</param>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The shared scanner is passed to both the metadata reader and the library service so the
        // supported-format list has exactly one instance behind it.
        var scanner = new AudioFileScanner();
        var metadataReader = new TrackMetadataReader(scanner);
        IMusicLibraryService library = new MusicLibraryService(scanner, metadataReader, new LibraryStore());
        IFolderPicker folderPicker = new FolderPicker();
        IAudioPlayer audioPlayer = new SilentAudioPlayer();
        ILyricProvider lyricProvider = new SampleLyricProvider();

        MainWindowViewModel viewModel = new(lyricProvider, library, folderPicker, audioPlayer);

        MainWindow = new MainWindow { DataContext = viewModel };
        MainWindow.Show();

        // Restored after the window is up so a slow or empty library never delays start-up and
        // the list fills in visibly instead of the window appearing late.
        await viewModel.Library.RestoreAsync();
    }
}
