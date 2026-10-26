namespace SongPlayer.Models;

/// <summary>
/// A single timed lyric line.
/// </summary>
/// <param name="Time">Moment inside the track at which the line starts being sung.</param>
/// <param name="Text">The lyric text itself.</param>
/// <remarks>
/// Kept as a plain immutable record so the view can bind to it directly and the karaoke
/// highlight can be driven by comparing <see cref="Time"/> with the playback position.
/// </remarks>
public sealed record LyricLine(TimeSpan Time, string Text);
