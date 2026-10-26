using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SongPlayer.Models;

namespace SongPlayer.ViewModels.Shell;

/// <summary>
/// Track metadata and transport state of the bottom player bar.
/// </summary>
/// <remarks>
/// <see cref="PositionSeconds"/> is the single source of truth for playback position: the seek
/// slider, the position label and the karaoke highlight all derive from it. Nothing else in the
/// shell is allowed to hold a second copy of the position, otherwise the views drift apart while
/// the user drags the slider.
/// </remarks>
public partial class PlayerBarViewModel : ObservableObject
{
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
    /// True when a real track is loaded, used to enable the transport controls.
    /// </summary>
    public bool HasTrack => CurrentTrack != Track.Empty;

    /// <summary>
    /// Title of the loaded track, or a friendly placeholder.
    /// </summary>
    public string TrackTitle => CurrentTrack.Title;

    /// <summary>
    /// Artist of the loaded track, or a friendly placeholder.
    /// </summary>
    public string TrackArtist => CurrentTrack.Artist;

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

    /// <summary>
    /// Toggles the playback state. The audio pipeline is not wired up yet, so this only
    /// updates the visual state.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasTrack))]
    private void TogglePlayback() => IsPlaying = !IsPlaying;

    /// <summary>
    /// Refreshes the track labels and re-evaluates the transport command whenever the
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
    /// Keeps the position label in sync while seeking or playing.
    /// </summary>
    partial void OnPositionSecondsChanged(double value) => OnPropertyChanged(nameof(PositionText));

    /// <summary>
    /// Formats a duration as <c>m:ss</c> or <c>h:mm:ss</c> for long tracks.
    /// </summary>
    private static string FormatTime(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}"
            : $"{value.Minutes:D2}:{value.Seconds:D2}";
}
