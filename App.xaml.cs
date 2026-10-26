using System.Windows;
using SongPlayer.Services.Audio;
using SongPlayer.Services.Bilibili;
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
/// is explicit about which implementation it gets, which is what makes the audio engine a
/// one-line swap.
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
        IBilibiliSource bilibiliSource = new BilibiliBridgeClient();
        LibVlcAudioPlayer audioPlayer = new();
        ILyricProvider lyricProvider = new SampleLyricProvider();

        _audioPlayer = audioPlayer;

        MainWindowViewModel viewModel = new(lyricProvider, library, folderPicker, bilibiliSource, audioPlayer);

        MainWindow = new MainWindow { DataContext = viewModel };
        MainWindow.Show();

        // Restored after the window is up so a slow or empty library never delays start-up and
        // the list fills in visibly instead of the window appearing late.
        await viewModel.Library.RestoreAsync();
    }

    /// <summary>
    /// Releases the audio device on shutdown.
    /// </summary>
    /// <remarks>
    /// LibVLC holds an audio output device open for as long as the engine exists, which would
    /// outlive the window if nothing disposed it.
    /// </remarks>
    /// <param name="e">Exit arguments.</param>
    protected override void OnExit(ExitEventArgs e)
    {
        _audioPlayer?.Dispose();
        _audioPlayer = null;

        base.OnExit(e);
    }

    /// <summary>
    /// Engine instance kept for shutdown; the view models only ever see the interface.
    /// </summary>
    private LibVlcAudioPlayer? _audioPlayer;
}
