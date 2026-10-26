namespace SongPlayer.Services.Bilibili;

/// <summary>
/// A Bilibili video id together with the audio URL that was just resolved for it.
/// </summary>
/// <param name="Bvid">Video id that was resolved, e.g. <c>BV1GJ411x7h7</c>.</param>
/// <param name="AudioUri">
/// Temporary CDN URL of the audio stream. Bilibili signs these with a deadline, so the value is a
/// snapshot: it is valid for this playback attempt and must never be written to disk.
/// </param>
public sealed record ResolvedAudio(string Bvid, Uri AudioUri);
