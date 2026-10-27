namespace SongPlayer.Services.Bilibili;

/// <summary>
/// Everything Bilibili reports about a video: the playable audio URL plus the metadata its
/// Bilibili video-info endpoint returns.
/// </summary>
/// <param name="Bvid">Video id that was resolved.</param>
/// <param name="AudioUri">
/// Temporary CDN URL of the audio stream. Bilibili signs these with a deadline, so the value is a
/// snapshot: it is valid for this playback attempt and must never be written to disk.
/// </param>
/// <param name="Title">Video title. Often the song name, but not guaranteed to be one.</param>
/// <param name="Uploader">
/// Name of the account that uploaded the video. Not the artist: the uploader may be a label, a
/// re-upload channel or an ordinary user.
/// </param>
/// <param name="CoverUri">
/// Video thumbnail URL, or <see langword="null"/> when it could not be read. Useful as
/// artwork, but it is a video thumbnail rather than an album cover.
/// </param>
/// <param name="Description">Video description, kept for reference and not shown as lyrics.</param>
/// <param name="Duration">
/// Video length reported by the metadata endpoint, or <see cref="TimeSpan.Zero"/> when it could not be
/// read. Preferred over probing the audio segment, which only knows the length of the stream it
/// happened to download.
/// </param>
/// <param name="Captions">
/// Timed subtitles that were publicly readable, in playback order. Empty is the common case and
/// does not indicate a failure.
/// </param>
public sealed record ResolvedMedia(
    string Bvid,
    Uri AudioUri,
    string Title,
    string Uploader,
    Uri? CoverUri,
    string Description,
    TimeSpan Duration,
    IReadOnlyList<BilibiliCaption> Captions);
