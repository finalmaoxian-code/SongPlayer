using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SongPlayer.Models;
using SongPlayer.Services.Audio;
using SongPlayer.Services.Bilibili;
using SongPlayer.Services.Library;
using SongPlayer.Services.Lyrics;
using SongPlayer.Services.Storage;
using SongPlayer.ViewModels.Pages;
using SongPlayer.ViewModels.Shell;

namespace SongPlayer.ViewModels;

/// <summary>
/// View model of the shell window. It owns navigation state and composes the child view models
/// of the player bar and the Now Playing overlay.
/// </summary>
/// <remarks>
/// The children never reference each other. This type is the mediator: the player bar stays the
/// single source of truth for the playback position, and every change to it is pushed into the
/// overlay view model from here. Keeping the wiring in one place means there is exactly one edge
/// between the two, so no notification can be forgotten in a partial method of the wrong class.
/// Children are exposed as properties so the views can bind through them
/// (<c>{Binding PlayerBar.TrackTitle}</c>) while the window keeps a single <c>DataContext</c>.
/// </remarks>
public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>
    /// Creates the shell for the sample track and its lyrics.
    /// </summary>
    /// <remarks>
    /// This overload is the design-time fallback: it builds the whole shell from the default
    /// providers so the XAML designer and a bare <c>new MainWindow()</c> both show something
    /// sensible. The composition root in <c>App</c> passes the real services instead.
    /// </remarks>
    public MainWindowViewModel()
        : this(
            new SampleLyricProvider(),
            CreateDesignTimeLibrary(),
            new FolderPicker(),
            new BilibiliBridgeClient(),
            audioPlayer: null)
    {
    }

    /// <summary>
    /// Builds a library over the real services for the designer / bare-window path.
    /// </summary>
    private static IMusicLibraryService CreateDesignTimeLibrary()
    {
        var scanner = new AudioFileScanner();

        return new MusicLibraryService(scanner, new TrackMetadataReader(scanner), new LibraryStore());
    }

    /// <summary>
    /// Creates the shell with the child view models wired to the given services.
    /// </summary>
    /// <param name="lyricProvider">Source of the timed lyrics shown on the Now Playing overlay.</param>
    /// <param name="library">Library facade behind the Local Music page.</param>
    /// <param name="folderPicker">Picker used by the import command.</param>
    /// <param name="bilibiliSource">Bridge client behind the Bilibili input.</param>
    /// <param name="audioPlayer">
    /// Engine that executes the transport commands, or <c>null</c> to keep the transport purely
    /// visual.
    /// </param>
    public MainWindowViewModel(
        ILyricProvider lyricProvider,
        IMusicLibraryService library,
        IFolderPicker folderPicker,
        IBilibiliSource bilibiliSource,
        IAudioPlayer? audioPlayer = null)
    {
        PlayerBar = audioPlayer is null
            ? new PlayerBarViewModel()
            : new PlayerBarViewModel(audioPlayer);

        NowPlaying = new NowPlayingViewModel(lyricProvider);
        NowPlaying.LoadFor(PlayerBar.CurrentTrack);

        Library = new LibraryPageViewModel(library, folderPicker);
        Library.TrackActivated += OnLibraryTrackActivated;

        Bilibili = new BilibiliViewModel(bilibiliSource);
        Bilibili.TrackResolved += OnBilibiliTrackResolved;

        PlayerBar.PropertyChanged += OnPlayerBarPropertyChanged;
        PlayerBar.PlaybackFailed += OnPlayerBarPlaybackFailed;
    }

    /// <summary>
    /// Bilibili input shown in the Local Music toolbar.
    /// </summary>
    public BilibiliViewModel Bilibili { get; }

    /// <summary>
    /// Tracks imported into the local library, shown by the Local Music page.
    /// </summary>
    public LibraryPageViewModel Library { get; }

    /// <summary>
    /// Transport state of the bottom bar. Also the source of truth for the overlay, which shows
    /// the very same track and position.
    /// </summary>
    public PlayerBarViewModel PlayerBar { get; }

    /// <summary>
    /// Karaoke state of the immersive overlay.
    /// </summary>
    public NowPlayingViewModel NowPlaying { get; }

    /// <summary>
    /// Currently displayed page. Changing it refreshes the derived navigation flags.
    /// </summary>
    [ObservableProperty]
    private PlayerPage currentPage = PlayerPage.Home;

    /// <summary>
    /// Text typed into the search box. Reserved for the library filter.
    /// </summary>
    [ObservableProperty]
    private string searchText = string.Empty;

    /// <summary>
    /// True when the full-window Now Playing overlay is open. The overlay replaces the whole
    /// shell (sidebar, page header and player bar), so it is tracked separately from
    /// <see cref="CurrentPage"/> and closing it always returns to the page underneath.
    /// </summary>
    [ObservableProperty]
    private bool isNowPlayingOpen;

    /// <summary>
    /// True when the Home page is the active page. This is a two-way binding target for the
    /// sidebar radio button: assigning <c>true</c> navigates, so the checked state of the nav
    /// item and <see cref="CurrentPage"/> can never drift apart, no matter how the item was
    /// activated (mouse, keyboard or UI Automation).
    /// </summary>
    public bool IsHomeSelected
    {
        get => CurrentPage == PlayerPage.Home;
        set
        {
            if (value)
            {
                CurrentPage = PlayerPage.Home;
            }
        }
    }

    /// <summary>
    /// True when the Library page is the active page. See <see cref="IsHomeSelected"/> for why
    /// the setter exists.
    /// </summary>
    public bool IsLibrarySelected
    {
        get => CurrentPage == PlayerPage.Library;
        set
        {
            if (value)
            {
                CurrentPage = PlayerPage.Library;
            }
        }
    }

    /// <summary>
    /// Large heading of the active page.
    /// </summary>
    public string PageTitle =>
        CurrentPage switch
        {
            PlayerPage.Library => "Local Music",
            _ => "Home",
        };

    /// <summary>
    /// Supporting line under the page heading.
    /// </summary>
    public string PageSubtitle =>
        CurrentPage switch
        {
            PlayerPage.Library => "Everything you have imported into SongPlayer lives here.",
            _ => "Welcome back — pick up where you left off.",
        };

    /// <summary>
    /// Switches the content area to the Library page. The sidebar items bind to the selection
    /// properties instead, but plain buttons (such as the hero call to action) need a command.
    /// </summary>
    [RelayCommand]
    private void ShowLibrary() => CurrentPage = PlayerPage.Library;

    /// <summary>
    /// Runs the folder import and then shows the result.
    /// </summary>
    /// <remarks>
    /// The import belongs to the library page, but the Home page and the sidebar offer the same
    /// action. Rather than duplicating the scan logic, they call this command, which delegates
    /// and navigates to where the outcome is visible.
    /// </remarks>
    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        await Library.ImportFolderCommand.ExecuteAsync(null);

        CurrentPage = PlayerPage.Library;
    }

    /// <summary>
    /// Opens the immersive Now Playing overlay. The current page is left untouched, so
    /// closing the overlay lands back on exactly the page the user came from.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenNowPlaying))]
    private void OpenNowPlaying() => IsNowPlayingOpen = true;

    /// <summary>
    /// Dismisses the Now Playing overlay and reveals the shell again.
    /// </summary>
    [RelayCommand]
    private void CloseNowPlaying() => IsNowPlayingOpen = false;

    /// <summary>
    /// Jumps to the moment a lyric line starts, so clicking a line seeks the track.
    /// </summary>
    /// <param name="line">Row that was clicked.</param>
    [RelayCommand]
    private void SeekToLyric(LyricLineViewModel? line)
    {
        if (line is null || !PlayerBar.HasTrack)
        {
            return;
        }

        PlayerBar.PositionSeconds = line.Time.TotalSeconds;
    }

    /// <summary>
    /// The overlay can only be opened once a track is loaded.
    /// </summary>
    private bool CanOpenNowPlaying() => PlayerBar.HasTrack;

    /// <summary>
    /// Fans a player bar change out to whatever else depends on it.
    /// </summary>
    /// <remarks>
    /// This is the mediator edge: position drives the karaoke highlight, and loading a track
    /// rebuilds the lyric rows and re-evaluates the overlay command.
    /// </remarks>
    private void OnPlayerBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerBarViewModel.PositionSeconds):
                NowPlaying.UpdateActiveLyric(TimeSpan.FromSeconds(PlayerBar.PositionSeconds));
                break;

            case nameof(PlayerBarViewModel.CurrentTrack):
                NowPlaying.LoadFor(PlayerBar.CurrentTrack);
                OpenNowPlayingCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    /// <summary>
    /// Loads a track picked on the Local Music page into the player bar.
    /// </summary>
    /// <remarks>
    /// The library page does not know the player bar exists; it raises an event and the mediator
    /// decides what it means. Selecting a row only loads the track (position reset, lyrics rebuilt,
    /// transport enabled) — starting playback stays an explicit action on the transport button.
    /// </remarks>
    private void OnLibraryTrackActivated(object? sender, Track track)
    {
        PlayerBar.LoadTrack(track);
        CurrentPage = PlayerPage.Library;
    }

    /// <summary>
    /// Plays a stream that the Bilibili input just resolved.
    /// </summary>
    /// <remarks>
    /// Starting playback immediately is the right default here: the user pressed a button labelled
    /// "Play", unlike a library row selection, which only loads the track.
    /// </remarks>
    private void OnBilibiliTrackResolved(object? sender, Track track)
    {
        _refreshedStreamKey = null;
        PlayerBar.PlayTrack(track);
        CurrentPage = PlayerPage.Library;
    }

    /// <summary>
    /// Retries an online track once when its stream could not be played.
    /// </summary>
    /// <param name="sender">Player bar that failed.</param>
    /// <param name="track">Track the engine gave up on.</param>
    /// <remarks>
    /// Bilibili signs CDN URLs with a deadline, so a link saved from an earlier session — or one
    /// that simply aged out while the user was listening — is dead by design. Re-resolving is the
    /// only honest fix, and it is attempted once per track so a genuinely broken video does not
    /// spin in a retry loop.
    /// </remarks>
    private async void OnPlayerBarPlaybackFailed(object? sender, Track track)
    {
        if (!track.IsStream || track.StreamKey == _refreshedStreamKey)
        {
            return;
        }

        _refreshedStreamKey = track.StreamKey;

        Track? refreshed = await Bilibili.TryRefreshAsync(track);

        if (refreshed is not null)
        {
            PlayerBar.PlayTrack(refreshed);
        }
    }

    /// <summary>
    /// Stream key whose refresh was already attempted, so a failure cannot repeat forever.
    /// </summary>
    private string? _refreshedStreamKey;

    /// <summary>
    /// Refreshes every property derived from <see cref="CurrentPage"/>.
    /// </summary>
    partial void OnCurrentPageChanged(PlayerPage value)
    {
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsLibrarySelected));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
    }
}
