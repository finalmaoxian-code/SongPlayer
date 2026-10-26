using SongPlayer.Models;

namespace SongPlayer.Services.Audio;

/// <summary>
/// Placeholder player that keeps the transport state without producing sound.
/// </summary>
/// <remarks>
/// The UI ships before the audio engine does. This implementation records what the shell asked
/// for — play, pause, seek — so the buttons, the seek slider and the karaoke highlight all
/// behave exactly as they will with a real player, and it is the seam where the real engine is
/// dropped in later. It never advances the position on its own: without a clock there is
/// nothing to interpolate, and pretending otherwise would desynchronise the lyric highlight.
/// </remarks>
public sealed class SilentAudioPlayer : IAudioPlayer
{
    /// <summary>
    /// Track handed to the last <see cref="Play"/> call.
    /// </summary>
    public Track? CurrentTrack { get; private set; }

    /// <summary>
    /// Position of the virtual playhead.
    /// </summary>
    public TimeSpan Position { get; private set; }

    /// <inheritdoc />
    public bool IsPlaying { get; private set; }

    /// <inheritdoc />
    public void Play(Track track, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(track);

        CurrentTrack = track;
        Position = position;
        IsPlaying = true;
    }

    /// <inheritdoc />
    public void Pause() => IsPlaying = false;

    /// <inheritdoc />
    public void Stop()
    {
        IsPlaying = false;
        Position = TimeSpan.Zero;
    }

    /// <inheritdoc />
    public void Seek(TimeSpan position) => Position = position;
}
