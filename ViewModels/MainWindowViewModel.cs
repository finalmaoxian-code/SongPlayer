using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SongPlayer.Models;

namespace SongPlayer.ViewModels;

/// <summary>
/// View model for the main window shell.
/// It owns the navigation state and the state of the bottom player bar.
/// All values exposed here are plain data, the view only binds to them, which keeps
/// <c>MainWindow.xaml.cs</c> free of UI state manipulation.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>
    /// Creates the shell view model with the sample track and its lyrics already loaded.
    /// </summary>
    public MainWindowViewModel()
    {
        LoadLyrics();
    }

    /// <summary>
    /// Currently displayed page. Changing it refreshes the derived navigation flags.
    /// </summary>
    [ObservableProperty]
    private PlayerPage currentPage = PlayerPage.Home;

    /// <summary>
    /// Track shown in the player bar. <see cref="Track.Empty"/> means nothing is loaded.
    /// The sample track is preloaded so the player bar and the Now Playing page can be
    /// reviewed before the audio engine exists.
    /// </summary>
    [ObservableProperty]
    private Track currentTrack = Track.Sample;

    /// <summary>
    /// Whether playback is currently running. Purely visual until the audio engine lands.
    /// </summary>
    [ObservableProperty]
    private bool isPlaying;

    /// <summary>
    /// Current playback position in seconds (bound to the seek slider).
    /// </summary>
    [ObservableProperty]
    private double positionSeconds;

    /// <summary>
    /// Player volume in the range 0-100.
    /// </summary>
    [ObservableProperty]
    private double volume = 70;

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
    /// True when a real track is loaded, used to enable the transport controls.
    /// </summary>
    public bool HasTrack => CurrentTrack != Track.Empty;

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
    /// Title of the loaded track, or a friendly placeholder.
    /// </summary>
    public string TrackTitle => CurrentTrack.Title;

    /// <summary>
    /// Artist of the loaded track, or a friendly placeholder.
    /// </summary>
    public string TrackArtist => CurrentTrack.Artist;

    /// <summary>
    /// Total length of the loaded track in seconds (seek slider maximum).
    /// </summary>
    public double DurationSeconds => CurrentTrack.Duration.TotalSeconds;

    /// <summary>
    /// Formatted playback position, e.g. <c>01:24</c>.
    /// </summary>
    public string PositionText => FormatTime(TimeSpan.FromSeconds(PositionSeconds));

    /// <summary>
    /// Formatted track length, e.g. <c>03:52</c>.
    /// </summary>
    public string DurationText => FormatTime(CurrentTrack.Duration);

    /// <summary>
    /// Glyph of the play/pause button (Segoe MDL2 Assets codes).
    /// </summary>
    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE768";

    /// <summary>
    /// Tooltip text of the play/pause button.
    /// </summary>
    public string PlayPauseTooltip => IsPlaying ? "Pause" : "Play";

    // ------------------------------------------------------------------ now playing

    /// <summary>
    /// Timed lyrics of the loaded track, projected into rows the view can bind to.
    /// </summary>
    public ObservableCollection<LyricLineViewModel> Lyrics { get; } = new();

    /// <summary>
    /// Index of the highlighted lyric line, or -1 while the track has not reached the first
    /// line yet. The view listens to this to scroll the active line into view.
    /// </summary>
    [ObservableProperty]
    private int activeLyricIndex = -1;

    /// <summary>
    /// True when the loaded track has at least one lyric line, which decides whether the
    /// list or the empty hint is shown.
    /// </summary>
    public bool HasLyrics => Lyrics.Count > 0;

    /// <summary>
    /// Album name shown under the artwork. Falls back to a neutral label for loose files
    /// that carry no album tag.
    /// </summary>
    public string TrackAlbum => HasTrack ? "Night Roads" : "No album";

    /// <summary>
    /// Track position inside its album, formatted the way music players show it.
    /// </summary>
    public string TrackAlbumPosition => HasTrack ? "Track 04" : "—";

    /// <summary>
    /// Glyph of the procedural artwork placeholder. Real cover art will replace this once
    /// tag reading lands, so the view already binds it instead of hard-coding the glyph.
    /// </summary>
    public string ArtworkGlyph => "\uE8D6";

    // ------------------------------------------------------------------ navigation

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
    [RelayCommand(CanExecute = nameof(HasTrack))]
    private void OpenNowPlaying() => IsNowPlayingOpen = true;

    /// <summary>Dismisses the Now Playing overlay and reveals the shell again.</summary>
    [RelayCommand]
    private void CloseNowPlaying() => IsNowPlayingOpen = false;

    /// <summary>
    /// Jumps to the moment a lyric line starts, so clicking a line seeks the track.
    /// </summary>
    /// <param name="line">Row that was clicked.</param>
    [RelayCommand]
    private void SeekToLyric(LyricLineViewModel? line)
    {
        if (line is null || !HasTrack)
        {
            return;
        }

        PositionSeconds = line.Time.TotalSeconds;
    }

    // ------------------------------------------------------------------ transport

    /// <summary>
    /// Toggles the playback state. The audio pipeline is not wired up yet, so this only
    /// updates the visual state.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasTrack))]
    private void TogglePlayback() => IsPlaying = !IsPlaying;

    // ------------------------------------------------------------------ notifications

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

    /// <summary>
    /// Refreshes the track labels and re-evaluates the transport commands whenever the
    /// loaded track changes.
    /// </summary>
    partial void OnCurrentTrackChanged(Track value)
    {
        OnPropertyChanged(nameof(HasTrack));
        OnPropertyChanged(nameof(TrackTitle));
        OnPropertyChanged(nameof(TrackArtist));
        OnPropertyChanged(nameof(TrackAlbum));
        OnPropertyChanged(nameof(TrackAlbumPosition));
        OnPropertyChanged(nameof(DurationSeconds));
        OnPropertyChanged(nameof(DurationText));
        TogglePlaybackCommand.NotifyCanExecuteChanged();
        OpenNowPlayingCommand.NotifyCanExecuteChanged();
        LoadLyrics();
    }

    /// <summary>
    /// Keeps the play/pause glyph and tooltip in sync with the playback state.
    /// </summary>
    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayPauseGlyph));
        OnPropertyChanged(nameof(PlayPauseTooltip));
    }

    /// <summary>
    /// Keeps the position label in sync while seeking or playing, and moves the karaoke
    /// highlight to the lyric line that matches the new position.
    /// </summary>
    partial void OnPositionSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(PositionText));
        UpdateActiveLyric(TimeSpan.FromSeconds(value));
    }

    // ------------------------------------------------------------------ lyric helpers

    /// <summary>
    /// Rebuilds the lyric rows for the loaded track and resets the highlight.
    /// </summary>
    /// <remarks>
    /// Until the <c>.lrc</c> parser exists the rows come from <see cref="SampleLyrics"/>, so
    /// every track shares the same placeholder lyrics.
    /// </remarks>
    private void LoadLyrics()
    {
        Lyrics.Clear();

        if (HasTrack)
        {
            foreach (var line in SampleLyrics.ForSampleTrack)
            {
                Lyrics.Add(new LyricLineViewModel(line));
            }
        }

        OnPropertyChanged(nameof(HasLyrics));
        UpdateActiveLyric(TimeSpan.FromSeconds(PositionSeconds));
    }

    /// <summary>
    /// Marks the last line whose start time is not after <paramref name="position"/> as the
    /// active one and clears the flag on every other row.
    /// </summary>
    /// <param name="position">Current playback position.</param>
    private void UpdateActiveLyric(TimeSpan position)
    {
        var activeIndex = -1;

        for (var i = 0; i < Lyrics.Count; i++)
        {
            if (Lyrics[i].Time > position)
            {
                break;
            }

            activeIndex = i;
        }

        // Only the two rows that actually change need a notification.
        for (var i = 0; i < Lyrics.Count; i++)
        {
            Lyrics[i].IsActive = i == activeIndex;
        }

        if (ActiveLyricIndex != activeIndex)
        {
            ActiveLyricIndex = activeIndex;
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Formats a duration as <c>m:ss</c> or <c>h:mm:ss</c> for long tracks.
    /// </summary>
    private static string FormatTime(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}"
            : $"{value.Minutes:D2}:{value.Seconds:D2}";
}
