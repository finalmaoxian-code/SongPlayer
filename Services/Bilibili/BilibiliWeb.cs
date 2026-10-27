using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SongPlayer.Services.Bilibili;

/// <summary>
/// The one place the app talks to Bilibili's public web API.
/// </summary>
/// <remarks>
/// Bilibili is called directly from the app rather than through a helper process. The endpoints used
/// here — view, playurl, search and the subtitle list — are the same ones the website itself calls,
/// need no account, and answer publicly. Keeping them in-process is what removes the "start
/// <c>node bridge.cjs</c> first" step from the user's path entirely.
/// <para>
/// Two headers are load-bearing: the browser user agent (an unidentified client is refused) and the
/// <c>bilibili.com</c> referer (the CDN rejects media requests without it). Both are set once on the
/// shared client so no individual request can forget them.
/// </para>
/// </remarks>
internal static class BilibiliWeb
{
    /// <summary>Origin every REST endpoint hangs off.</summary>
    internal const string ApiRoot = "https://api.bilibili.com";

    /// <summary>Referer the API and the CDN both expect.</summary>
    internal const string WebReferer = "https://www.bilibili.com/";

    /// <summary>Referer the search endpoint expects.</summary>
    internal const string SearchReferer = "https://search.bilibili.com/";

    /// <summary>
    /// Identifies the client as an ordinary desktop browser.
    /// </summary>
    /// <remarks>
    /// Bilibili answers a bare or unusual user agent with an empty payload rather than an error, so this
    /// is not cosmetic — the same request with <c>SongPlayer/1.0</c> returns nothing.
    /// </remarks>
    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>JSON options shared by every response shape.</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HttpClient Shared = CreateClient(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    });

    /// <summary>
    /// Builds a client with the headers every request needs.
    /// </summary>
    /// <param name="handler">Transport, injectable so tests can answer without a network.</param>
    internal static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Referrer = new Uri(WebReferer);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return client;
    }

    /// <summary>The shared client used by the app.</summary>
    internal static HttpClient Client => Shared;

    /// <summary>
    /// Reads a JSON document from an absolute URL.
    /// </summary>
    /// <param name="client">HTTP client to use.</param>
    /// <param name="url">Absolute URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="BilibiliSourceException">The network failed, the call timed out, or the body is not JSON.</exception>
    /// <remarks>
    /// Every failure becomes one of three sentences a user can act on: no connection, timed out, or an
    /// answer this app cannot read. Status codes are not surfaced — Bilibili returns HTML error pages for
    /// some blocked requests, and "HTTP 412" tells nobody anything.
    /// </remarks>
    internal static async Task<JsonDocument> GetJsonAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await client.GetAsync(url, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BilibiliSourceException("Bilibili did not answer in time. Try again in a moment.");
        }
        catch (HttpRequestException exception)
        {
            throw new BilibiliSourceException(
                "Could not reach Bilibili. Check the connection and try again.",
                exception);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.PreconditionFailed)
            {
                throw new BilibiliSourceException(
                    "Bilibili is rate-limiting requests right now. Wait a little and try again.");
            }

            try
            {
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                throw new BilibiliSourceException(
                    "Bilibili returned something this app cannot read. Try again later.");
            }
        }
    }

    /// <summary>
    /// Turns a Bilibili clock (<c>"4:03"</c> or <c>"1:02:03"</c>) into a length.
    /// </summary>
    /// <param name="value">Clock string as the search endpoint reports it.</param>
    /// <returns>The length, or <see cref="TimeSpan.Zero"/> when it cannot be parsed.</returns>
    /// <remarks>
    /// Search reports lengths as text while the video endpoints report seconds, so both shapes have to
    /// be understood. A zero here is not fatal: the length is only ever a tie-breaker.
    /// </remarks>
    internal static TimeSpan ParseClock(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.Zero;
        }

        string[] parts = value.Trim().Split(':');

        if (parts.Length is 0 or > 3)
        {
            return TimeSpan.Zero;
        }

        double total = 0;

        foreach (string part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) || number < 0)
            {
                return TimeSpan.Zero;
            }

            total = (total * 60) + number;
        }

        return total > 0 ? TimeSpan.FromSeconds(total) : TimeSpan.Zero;
    }

    /// <summary>
    /// Turns a protocol-relative or absolute thumbnail into an absolute https URL.
    /// </summary>
    /// <param name="value">Raw value, e.g. <c>//i0.hdslb.com/bfs/…</c>.</param>
    /// <returns>The absolute URL, or <see langword="null"/> when the value is not a web URL.</returns>
    internal static Uri? AbsoluteUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string raw = value.Trim();

        if (raw.StartsWith("//", StringComparison.Ordinal))
        {
            raw = "https:" + raw;
        }

        return Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;
    }
}

/// <summary>
/// Reads the key pair that signs web API requests.
/// </summary>
/// <remarks>
/// <c>/x/web-interface/nav</c> answers <c>code: -101 ("not logged in")</c> for an anonymous caller while
/// still carrying the keys, so a non-zero code here is expected and must not be treated as a failure —
/// only a missing <c>wbi_img</c> is.
/// </remarks>
internal static class BilibiliApiKeys
{
    /// <summary>
    /// Fetches the current <c>img_key</c>/<c>sub_key</c> pair.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The two keys, without their URL or extension.</returns>
    /// <exception cref="BilibiliSourceException">Bilibili is unreachable or stopped publishing keys.</exception>
    internal static async Task<(string ImageKey, string SubKey)> FetchAsync(CancellationToken cancellationToken)
    {
        using JsonDocument document = await BilibiliWeb.GetJsonAsync(
            BilibiliWeb.Client,
            BilibiliWeb.ApiRoot + "/x/web-interface/nav",
            cancellationToken);

        JsonElement data = document.RootElement.TryGetProperty("data", out JsonElement payload)
            ? payload
            : default;

        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("wbi_img", out JsonElement image)
            || ExtractKey(image, "img_url") is not { } imageKey
            || ExtractKey(image, "sub_url") is not { } subKey)
        {
            throw new BilibiliSourceException(
                "Bilibili did not hand out the signing keys this app needs. Try again later.");
        }

        return (imageKey, subKey);
    }

    /// <summary>
    /// Pulls the file name out of a key URL, which is the key itself.
    /// </summary>
    /// <param name="element">Object holding the key URL.</param>
    /// <param name="property">Property name to read.</param>
    private static string? ExtractKey(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value)
            || value.GetString() is not { } url)
        {
            return null;
        }

        int slash = url.LastIndexOf('/');
        int dot = url.LastIndexOf('.');

        return slash >= 0 && dot > slash + 1 ? url[(slash + 1)..dot] : null;
    }
}
