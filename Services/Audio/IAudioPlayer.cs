using SongPlayer.Models;

namespace SongPlayer.Services.Audio;

/// <summary>
/// Playback engine used by the player bar.
/// </summary>
/// <remarks>
/// The view models own the user-facing state (which track is loaded, where the seek slider
/// points); the player only executes the resulting instructions and reports back what the audio
/// hardware is actually doing. Keeping the contract this narrow means the views never talk to a
/// concrete audio API, so <see cref="MediaPlayerAudioPlayer"/> can be swapped for another engine
/// without touching any view or view model.
/// <para>
/// The engine is the authority on the playhead once playback starts: it raises
/// <see cref="PositionChanged"/> and the player bar mirrors that value. The command methods are for
/// user intent only, so a position update coming back from the engine must never be echoed as a
/// seek.
/// </para>
/// </remarks>
public interface IAudioPlayer
{
    /// <summary>
    /// Whether the engine is currently playing.
    /// </summary>
    bool IsPlaying { get; }

    /// <summary>
    /// Output volume in the range 0-100, matching the unit the player bar slider uses.
    /// </summary>
    double Volume { get; set; }

    /// <summary>
    /// Raised while playing with the position the audio hardware has reached.
    /// </summary>
    event EventHandler<TimeSpan>? PositionChanged;

    /// <summary>
    /// Raised once when the loaded track reaches its end.
    /// </summary>
    event EventHandler? PlaybackEnded;

    /// <summary>
    /// Raised when a track cannot be decoded or opened. The argument is a message fit for the UI.
    /// </summary>
    event EventHandler<string>? PlaybackFailed;

    /// <summary>
    /// Starts or resumes playback of <paramref name="track"/> at <paramref name="position"/>.
    /// </summary>
    /// <param name="track">Track to play. Tracks without a file path cannot be played.</param>
    /// <param name="position">Position to resume from; <see cref="TimeSpan.Zero"/> starts over.</param>
    void Play(Track track, TimeSpan position);

    /// <summary>
    /// Pauses playback, keeping the current position.
    /// </summary>
    void Pause();

    /// <summary>
    /// Stops playback and resets the position to the beginning of the track.
    /// </summary>
    void Stop();

    /// <summary>
    /// Moves the playhead to <paramref name="position"/> without changing the play state.
    /// </summary>
    /// <param name="position">Target position inside the loaded track.</param>
    void Seek(TimeSpan position);
}
