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
    public async Task<ResolvedAudio> ResolveAsync(string bvid, CancellationToken cancellationToken = default)
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

        if (!Uri.TryCreate(response.AudioUrl, UriKind.Absolute, out Uri? audioUri)
            || (audioUri.Scheme != Uri.UriSchemeHttps && audioUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new BilibiliSourceException("The bridge returned an audio URL that is not http(s).");
        }

        return new ResolvedAudio(bvid, audioUri);
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
    private sealed record BridgeResponse(
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("bvid")] string? Bvid,
        [property: JsonPropertyName("audioUrl")] string? AudioUrl);
}
