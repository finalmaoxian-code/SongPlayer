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
        IConfirmationPrompt confirmation = new ConfirmationPrompt();
        // Bilibili is called directly: its public web API answers from this process, so there is no
        // helper to start and no second executable to ship.
        IBilibiliSource bilibiliSource = new BilibiliClient();
        LibVlcAudioPlayer audioPlayer = new();

        // Lyrics are fetched from LRCLIB directly rather than from Bilibili: a lyric database has
        // nothing to do with a video source. The caption store is still passed along so archived
        // online subtitles remain a separate, clearly labelled fallback when LRCLIB has nothing.
        var captionStore = new CaptionStore();
        var lyricCache = new LyricCacheStore();
        ILrclibClient lrclibClient = new LrclibClient();
        ILyricProvider lyricProvider = new TrackLyricProvider(
            lrclibClient,
            new LyricMatcher(),
            lyricCache,
            new SongIdentityResolver(),
            captionStore);

        _audioPlayer = audioPlayer;

        MainWindowViewModel viewModel = new(
            lyricProvider,
            library,
            folderPicker,
            confirmation,
            bilibiliSource,
            captionStore,
            audioPlayer);

        _viewModel = viewModel;

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
    /// outlive the window if nothing disposed it. A lyric lookup still waiting on the network is
    /// cancelled first so its continuation cannot touch a shell that is already gone.
    /// </remarks>
    /// <param name="e">Exit arguments.</param>
    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Shutdown();
        _viewModel = null;

        _audioPlayer?.Dispose();
        _audioPlayer = null;

        base.OnExit(e);
    }

    /// <summary>
    /// Shell kept for shutdown so pending work can be cancelled.
    /// </summary>
    private MainWindowViewModel? _viewModel;

    /// <summary>
    /// Engine instance kept for shutdown; the view models only ever see the interface.
    /// </summary>
    private LibVlcAudioPlayer? _audioPlayer;
}
