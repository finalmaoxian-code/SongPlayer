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
/// <para>
/// Once an engine is attached it owns the playhead: the engine's position reports are copied into
/// <see cref="PositionSeconds"/>, and a position change that came from the engine is not handed
/// back to it as a seek. Without that distinction the timer's own reports would seek the engine
/// four times a second and playback would stutter in place.
/// </para>
/// </remarks>
public partial class PlayerBarViewModel : ObservableObject
{
    /// <summary>
    /// Engine that executes the transport commands. Null while no engine is wired up, which is
    /// why every call is null-conditional: the bar stays usable as a pure UI shell.
    /// </summary>
    private readonly IAudioPlayer? _audioPlayer;

    /// <summary>
    /// True while <see cref="PositionSeconds"/> is being assigned from an engine report, which
    /// marks the assignment as "not a user seek".
    /// </summary>
    private bool _isApplyingEnginePosition;

    /// <summary>Volume to restore when the user unmutes.</summary>
    private double _volumeBeforeMute = 70;

    /// <summary>
    /// True while the user is holding the seek bar.
    /// </summary>
    /// <remarks>
    /// While true the engine's position reports are ignored: the pointer, not the playhead, owns
    /// the thumb during a drag, and letting both write the value makes it jitter under the cursor.
    /// </remarks>
    private bool _isSeeking;

    /// <summary>
    /// Creates the player bar without an audio engine, so it only tracks visual state.
    /// </summary>
    public PlayerBarViewModel()
    {
    }

    /// <summary>
    /// Creates the player bar that forwards transport commands to <paramref name="audioPlayer"/>
    /// and mirrors the playhead it reports.
    /// </summary>
    /// <param name="audioPlayer">Engine that turns the commands into actual playback.</param>
    public PlayerBarViewModel(IAudioPlayer audioPlayer)
    {
        ArgumentNullException.ThrowIfNull(audioPlayer);

        _audioPlayer = audioPlayer;
        _audioPlayer.Volume = Volume;
        _audioPlayer.PositionChanged += OnEnginePositionChanged;
        _audioPlayer.PlaybackEnded += OnEnginePlaybackEnded;
        _audioPlayer.PlaybackFailed += OnEnginePlaybackFailed;
    }

    /// <summary>
    /// Message shown to the user when the engine could not play the loaded track, or <c>null</c>
    /// when the last transport action succeeded.
    /// </summary>
    [ObservableProperty]
    private string? statusMessage;

    /// <summary>
    /// True when there is a failure message to show; bound to the status line visibility.
    /// </summary>
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>
    /// Raised when the engine could not play the loaded track, carrying the track that failed.
    /// </summary>
    /// <remarks>
    /// The message itself travels through <see cref="StatusMessage"/>, so the bar can show it
    /// without anyone listening. The event exists for recovery: an online track whose signed URL
    /// expired is worth re-resolving once, and only the shell knows how to do that.
    /// </remarks>
    public event EventHandler<Track>? PlaybackFailed;

    /// <summary>
    /// Track shown in the player bar. <see cref="Track.Empty"/> means nothing is loaded.
    /// The sample track is preloaded so the shell has something to show before the library is
    /// imported; it carries no file path, so pressing play on it reports that it cannot be played.
    /// </summary>
    [ObservableProperty]
    private Track currentTrack = Track.Sample;

    /// <summary>
    /// Whether playback is currently running.
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
    /// True when the volume slider is at zero.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="Volume"/> instead of stored: a separate flag could disagree with
    /// the slider after the user drags it, and one source of truth is cheaper than reconciling two.
    /// </remarks>
    public bool IsMuted => Volume <= 0;

    /// <summary>
    /// Glyph of the volume button, matching the current loudness.
    /// </summary>
    public string VolumeGlyph => IsMuted ? "\uE74F" : Volume < 50 ? "\uE993" : "\uE995";

    /// <summary>
    /// Tooltip of the volume button.
    /// </summary>
    public string MuteTooltip => IsMuted ? "Unmute" : "Mute";

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
        StatusMessage = null;
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
        StatusMessage = null;

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
        RestartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Restarts the loaded track from the beginning.
    /// </summary>
    /// <remarks>
    /// The player has no queue yet, so the previous/next buttons have nothing to step to. Restart
    /// is what the transport can honestly promise today.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasTrack))]
    private void Restart()
    {
        PositionSeconds = 0;
        IsPlaying = true;
        StatusMessage = null;
        _audioPlayer?.Play(CurrentTrack, TimeSpan.Zero);
    }

    /// <summary>
    /// Mutes the output, or restores the last audible volume.
    /// </summary>
    [RelayCommand]
    private void ToggleMute()
    {
        if (Volume > 0)
        {
            _volumeBeforeMute = Volume;
            Volume = 0;
            return;
        }

        Volume = _volumeBeforeMute > 0 ? _volumeBeforeMute : 70;
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
    /// rather than once on release; the engine coalesces those seeks.
    /// <para>
    /// Assignments that came from the engine are skipped, otherwise the engine would be told to
    /// seek to the position it just reported.
    /// </para>
    /// </remarks>
    partial void OnPositionSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(PositionText));

        if (_isApplyingEnginePosition)
        {
            return;
        }

        _audioPlayer?.Seek(TimeSpan.FromSeconds(value));
    }

    /// <summary>
    /// Forwards the volume to the engine so the slider actually changes the loudness.
    /// </summary>
    partial void OnVolumeChanged(double value)
    {
        _audioPlayer?.Volume = value;

        // Mute state and the button glyph both read the volume, so one assignment updates three
        // visual states.
        OnPropertyChanged(nameof(IsMuted));
        OnPropertyChanged(nameof(VolumeGlyph));
        OnPropertyChanged(nameof(MuteTooltip));
    }

    /// <summary>
    /// Tells the player bar whether the user is currently dragging the seek bar, so the engine's
    /// position reports stop competing with the pointer.
    /// </summary>
    /// <param name="isSeeking">True when a drag starts, false when it ends.</param>
    /// <remarks>
    /// The view calls this because drag state is a property of the control, not of the playback
    /// model. On release the engine is asked to seek once, to the final value the user chose.
    /// </remarks>
    public void SetSeeking(bool isSeeking)
    {
        if (_isSeeking == isSeeking)
        {
            return;
        }

        _isSeeking = isSeeking;

        if (!isSeeking)
        {
            _audioPlayer?.Seek(TimeSpan.FromSeconds(PositionSeconds));
        }
    }

    /// <summary>
    /// Copies the playhead reported by the engine into <see cref="PositionSeconds"/>.
    /// </summary>
    private void OnEnginePositionChanged(object? sender, TimeSpan position)
    {
        if (_isSeeking)
        {
            return;
        }

        _isApplyingEnginePosition = true;

        try
        {
            PositionSeconds = position.TotalSeconds;
        }
        finally
        {
            _isApplyingEnginePosition = false;
        }
    }

    /// <summary>
    /// Leaves the playhead at the end of the track when it finishes. Advancing to the next track
    /// is queue behaviour the shell does not have yet, so the bar simply stops.
    /// </summary>
    private void OnEnginePlaybackEnded(object? sender, EventArgs e)
    {
        IsPlaying = false;

        _isApplyingEnginePosition = true;

        try
        {
            PositionSeconds = CurrentTrack.Duration.TotalSeconds;
        }
        finally
        {
            _isApplyingEnginePosition = false;
        }
    }

    /// <summary>
    /// Stops the visual play state and publishes the engine's message so the shell can show it.
    /// </summary>
    private void OnEnginePlaybackFailed(object? sender, string message)
    {
        IsPlaying = false;
        StatusMessage = message;

        PlaybackFailed?.Invoke(this, CurrentTrack);
    }

    /// <summary>
    /// Clears a previous failure message as soon as the user retries, so a stale error cannot
    /// outlive the action that fixed it.
    /// </summary>
    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));

    /// <summary>
    /// Formats a duration as <c>m:ss</c> or <c>h:mm:ss</c> for long tracks.
    /// </summary>
    private static string FormatTime(TimeSpan value) => TimeSpanFormatting.ToClock(value);
}
