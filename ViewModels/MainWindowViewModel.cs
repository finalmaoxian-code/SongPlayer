using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SongPlayer.Models;
using SongPlayer.Services.Lyrics;
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
    /// The parameterless overload is what XAML instantiates; it uses the sample lyric provider so
    /// the shell is self-contained until the composition root constructs it with real services.
    /// </remarks>
    public MainWindowViewModel()
        : this(new SampleLyricProvider())
    {
    }

    /// <summary>
    /// Creates the shell with the child view models wired to the given lyric provider.
    /// </summary>
    /// <param name="lyricProvider">Source of the timed lyrics shown on the Now Playing overlay.</param>
    public MainWindowViewModel(ILyricProvider lyricProvider)
    {
        PlayerBar = new PlayerBarViewModel();
        NowPlaying = new NowPlayingViewModel(lyricProvider);
        NowPlaying.LoadFor(PlayerBar.CurrentTrack);

        PlayerBar.PropertyChanged += OnPlayerBarPropertyChanged;
    }

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
