namespace SongPlayer.Models;

/// <summary>
/// Which card the Now Playing overlay shows on its right-hand side.
/// </summary>
public enum OverlayView
{
    /// <summary>
    /// The live spectrum. The default, because it is meaningful for every track — including the
    /// online ones that almost never come with timed lines.
    /// </summary>
    Visualizer,

    /// <summary>
    /// The timed lines: lyrics for a local track, subtitles for a video.
    /// </summary>
    Lyrics,
}
