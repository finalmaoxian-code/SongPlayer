namespace SongPlayer.Services.Bilibili;

/// <summary>
/// One timed subtitle line extracted from a video's public subtitle track.
/// </summary>
/// <param name="Seconds">Start of the line, in seconds from the beginning of the video.</param>
/// <param name="Text">The subtitle text itself.</param>
/// <remarks>
/// A subtitle is not necessarily a lyric: some uploaders provide timed lyrics, some provide
/// ordinary dialogue captions, and many provide nothing at all. The type is named after what the
/// data actually is so presentation code does not claim more than the source can support.
/// </remarks>
public sealed record BilibiliCaption(double Seconds, string Text);
