using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SongPlayer.Services.Bilibili;

/// <summary>
/// Talks to the local Node.js bridge that resolves a Bilibili video id into a playable audio URL.
/// </summary>
/// <remarks>
/// The bridge (<c>SongPlayer.SourceLab/bridge.cjs</c>) is a separate process the user starts by
/// hand; it listens on <c>127.0.0.1</c> only, so nothing here leaves the machine. The audio URL it
/// returns points at a Bilibili CDN and carries a signed deadline, which is why resolving is a
/// per-play operation and the result is never stored.
/// <para>
/// Errors are translated into messages the user can act on (is the bridge running? is the id
/// wrong? did the CDN refuse?) instead of surfacing raw HTTP status codes or a socket exception.
/// The bridge running but returning garbage is a different problem from the bridge not running,
/// so <see cref="ProbeAsync"/> exists to tell those apart before a resolve is attempted.
/// </para>
/// </remarks>
public sealed class BilibiliBridgeClient : IBilibiliSource
{
    private const string BridgeRoot = "http://127.0.0.1:39271";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    /// <inheritdoc />
    public async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(
                $"{BridgeRoot}/health",
                cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<ResolvedMedia> ResolveAsync(string bvid, CancellationToken cancellationToken = default)
    {
        if (!IsValidBvid(bvid))
        {
            throw new BilibiliSourceException("That does not look like a Bilibili video id (BV…).");
        }

        BridgeResponse? response;

        try
        {
            response = await Client.GetFromJsonAsync<BridgeResponse>(
                $"{BridgeRoot}/resolve?bvid={Uri.EscapeDataString(bvid)}",
                cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BilibiliSourceException(
                "The bridge did not answer in time. Is the Bilibili audio still resolvable?");
        }
        catch (HttpRequestException exception)
        {
            throw new BilibiliSourceException(
                $"Could not reach the local bridge at {BridgeRoot}. Start it with “node bridge.cjs”. "
                + $"({exception.Message})");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new BilibiliSourceException("The bridge returned a response SongPlayer cannot read.");
        }

        if (response is null || string.IsNullOrWhiteSpace(response.AudioUrl))
        {
            throw new BilibiliSourceException(
                "The bridge found no audio stream for that video.");
        }

        if (!TryCreateWebUri(response.AudioUrl, out Uri? audioUri))
        {
            throw new BilibiliSourceException("The bridge returned an audio URL that is not http(s).");
        }

        // Metadata is optional on purpose: a video whose details could not be fetched still plays,
        // it just shows the id instead of a title. Treating that as a failure would make the
        // feature depend on an endpoint that is allowed to rate-limit.
        string title = string.IsNullOrWhiteSpace(response.Title) ? bvid : response.Title.Trim();
        string uploader = string.IsNullOrWhiteSpace(response.Uploader) ? "Bilibili" : response.Uploader.Trim();

        Uri? coverUri = TryCreateWebUri(response.CoverUrl, out Uri? cover) ? cover : null;

        TimeSpan duration = response.DurationSeconds is > 0 and var seconds
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;

        return new ResolvedMedia(
            bvid,
            audioUri,
            title,
            uploader,
            coverUri,
            response.Description?.Trim() ?? string.Empty,
            duration,
            ReadCaptions(response.Captions));
    }

    /// <summary>
    /// Keeps the subtitle lines that carry a plausible timestamp and some text.
    /// </summary>
    /// <param name="captions">Raw lines as the bridge reported them.</param>
    /// <returns>Lines in playback order; empty when the video has no readable subtitles.</returns>
    /// <remarks>
    /// The ordering is done here rather than trusted from the payload: the highlight walks the
    /// list once and stops at the first line in the future, so an unordered list would silently
    /// hide lines.
    /// </remarks>
    private static IReadOnlyList<BilibiliCaption> ReadCaptions(IReadOnlyList<BridgeCaption>? captions)
    {
        if (captions is null || captions.Count == 0)
        {
            return [];
        }

        return captions
            .Where(line => line.Seconds is >= 0 and var seconds && !double.IsNaN(seconds))
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .Select(line => new BilibiliCaption(line.Seconds, line.Text!.Trim()))
            .OrderBy(line => line.Seconds)
            .ToArray();
    }

    /// <summary>
    /// Parses an absolute http(s) URL, rejecting relative and non-web values.
    /// </summary>
    /// <param name="value">Candidate string.</param>
    /// <param name="uri">Parsed URI when the method returns true.</param>
    private static bool TryCreateWebUri(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? candidate))
        {
            return false;
        }

        if (candidate.Scheme != Uri.UriSchemeHttps && candidate.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    /// <summary>
    /// Checks the shape of a Bilibili video id before spending a round trip on it.
    /// </summary>
    /// <param name="bvid">Candidate id.</param>
    /// <returns>True when it matches <c>BV</c> plus ten alphanumerics.</returns>
    private static bool IsValidBvid(string bvid) =>
        bvid.Length == 12
        && bvid.StartsWith("BV", StringComparison.Ordinal)
        && bvid.Skip(2).All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// Shape of the bridge's <c>/resolve</c> payload.
    /// </summary>
    /// <param name="Source">Provider that produced the URL, always <c>bilivideo</c>.</param>
    /// <param name="Bvid">Video id that was resolved.</param>
    /// <param name="AudioUrl">Temporary CDN URL of the audio stream.</param>
    /// <param name="Title">Video title reported by the Bilibili view endpoint.</param>
    /// <param name="Uploader">Name of the uploading account.</param>
    /// <param name="CoverUrl">Video thumbnail URL.</param>
    /// <param name="Description">Video description.</param>
    /// <param name="DurationSeconds">Video length in seconds.</param>
    /// <param name="Captions">Readable subtitle tracks, already flattened into timed lines.</param>
    private sealed record BridgeResponse(
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("bvid")] string? Bvid,
        [property: JsonPropertyName("audioUrl")] string? AudioUrl,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("uploader")] string? Uploader,
        [property: JsonPropertyName("coverUrl")] string? CoverUrl,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
        [property: JsonPropertyName("captions")] IReadOnlyList<BridgeCaption>? Captions);

    /// <summary>
    /// One subtitle line as the bridge serialises it.
    /// </summary>
    /// <param name="Seconds">Start of the line, in seconds.</param>
    /// <param name="Text">Text of the line.</param>
    private sealed record BridgeCaption(
        [property: JsonPropertyName("seconds")] double Seconds,
        [property: JsonPropertyName("text")] string? Text);
}
