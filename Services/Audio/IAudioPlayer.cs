using SongPlayer.Models;

namespace SongPlayer.Services.Audio;

/// <summary>
/// Playback engine used by the player bar.
/// </summary>
/// <remarks>
/// The view models own the user-facing state (which track is loaded, where the seek slider
/// points); the player only executes the resulting instructions. Keeping the contract this
/// narrow means the views never talk to a concrete audio API, so a real engine can replace
/// <see cref="SilentAudioPlayer"/> without touching any view or view model.
/// </remarks>
public interface IAudioPlayer
{
    /// <summary>
    /// Whether the engine is currently playing.
    /// </summary>
    bool IsPlaying { get; }

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
