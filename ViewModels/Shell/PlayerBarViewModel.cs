using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SongPlayer.Models;
using SongPlayer.Services.Audio;

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
    /// Engine that executes the transport commands. Null while no engine is wired up, which is
    /// why every call is null-conditional: the bar stays usable as a pure UI shell.
    /// </summary>
    private readonly IAudioPlayer? _audioPlayer;

    /// <summary>
    /// Creates the player bar without an audio engine, so it only tracks visual state.
    /// </summary>
    public PlayerBarViewModel()
    {
    }

    /// <summary>
    /// Creates the player bar that forwards transport commands to <paramref name="audioPlayer"/>.
    /// </summary>
    /// <param name="audioPlayer">Engine that turns the commands into actual playback.</param>
    public PlayerBarViewModel(IAudioPlayer audioPlayer) => _audioPlayer = audioPlayer;

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
    /// Album tag of the loaded track. Falls back to a neutral label for loose files that carry
    /// no album tag.
    /// </summary>
    public string TrackAlbum =>
        !string.IsNullOrWhiteSpace(CurrentTrack.Album) ? CurrentTrack.Album! : "No album";

    /// <summary>
    /// Track position inside its album, formatted the way music players show it.
    /// </summary>
    public string TrackAlbumPosition =>
        CurrentTrack.TrackNumber is { } number ? $"Track {number:D2}" : "—";

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
    /// Loads a track without starting it.
    /// </summary>
    /// <param name="track">Track to load.</param>
    /// <remarks>
    /// The position is reset before the track changes, so the seek slider never spans the old and
    /// the new duration for one frame. Lyrics follow through the mediator, which reacts to
    /// <see cref="CurrentTrack"/>.
    /// </remarks>
    public void LoadTrack(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);

        IsPlaying = false;
        PositionSeconds = 0;
        CurrentTrack = track;

        _audioPlayer?.Stop();
    }

    /// <summary>
    /// Loads a track and starts it from the beginning.
    /// </summary>
    /// <param name="track">Track to play.</param>
    public void PlayTrack(Track track)
    {
        LoadTrack(track);

        IsPlaying = true;

        _audioPlayer?.Play(track, TimeSpan.Zero);
    }

    /// <summary>
    /// Toggles the playback state and forwards the resulting instruction to the audio engine.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasTrack))]
    private void TogglePlayback()
    {
        IsPlaying = !IsPlaying;

        if (IsPlaying)
        {
            _audioPlayer?.Play(CurrentTrack, TimeSpan.FromSeconds(PositionSeconds));
        }
        else
        {
            _audioPlayer?.Pause();
        }
    }

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
    /// Keeps the position label in sync while seeking or playing, and moves the playhead.
    /// </summary>
    /// <remarks>
    /// A slider drag raises this for every pixel, so the engine is asked to seek continuously
    /// rather than once on release; a real engine is expected to coalesce those seeks.
    /// </remarks>
    partial void OnPositionSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(PositionText));
        _audioPlayer?.Seek(TimeSpan.FromSeconds(value));
    }

    /// <summary>
    /// Formats a duration as <c>m:ss</c> or <c>h:mm:ss</c> for long tracks.
    /// </summary>
    private static string FormatTime(TimeSpan value) => TimeSpanFormatting.ToClock(value);
}
