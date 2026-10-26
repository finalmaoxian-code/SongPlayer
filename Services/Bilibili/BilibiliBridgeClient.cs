using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task<IReadOnlyList<BilibiliSearchResult>> SearchAsync(
        string keyword,
        int limit,
        CancellationToken cancellationToken = default)
    {
        string query = keyword.Trim();

        if (query.Length == 0)
        {
            throw new BilibiliSourceException("Type something to search for first.");
        }

        SearchResponse? response = await GetFromBridgeAsync<SearchResponse>(
            $"{BridgeRoot}/search?keyword={Uri.EscapeDataString(query)}&limit={limit}",
            "The bridge did not answer in time. Try again in a moment.",
            cancellationToken);

        return ReadSearchResults(response?.Results);
    }

    /// <summary>
    /// Keeps the candidates that carry a usable id.
    /// </summary>
    /// <param name="results">Raw rows as the bridge reported them.</param>
    /// <returns>Candidates in the order the endpoint ranked them.</returns>
    /// <remarks>
    /// Every field but the id is allowed to be missing: a hit without a thumbnail or a length is
    /// still worth showing, and the id is the only thing that has to survive for the row to be
    /// actionable. Rows are not re-sorted — the ranking is the endpoint's and re-sorting on this side
    /// would only invent an authority neither side has.
    /// </remarks>
    private static IReadOnlyList<BilibiliSearchResult> ReadSearchResults(
        IReadOnlyList<BridgeSearchItem>? results)
    {
        if (results is null || results.Count == 0)
        {
            return [];
        }

        var candidates = new List<BilibiliSearchResult>(results.Count);

        foreach (BridgeSearchItem item in results)
        {
            string? bvid = item.Bvid?.Trim();

            if (!Bvid.IsValid(bvid))
            {
                continue;
            }

            string title = string.IsNullOrWhiteSpace(item.Title) ? bvid! : item.Title.Trim();
            string uploader = string.IsNullOrWhiteSpace(item.Uploader) ? "Bilibili" : item.Uploader.Trim();

            Uri? cover = TryCreateWebUri(item.CoverUri, out Uri? parsed) ? parsed : null;

            TimeSpan duration = item.DurationSeconds is > 0 and var seconds
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;

            candidates.Add(new BilibiliSearchResult(
                bvid!,
                title,
                uploader,
                duration,
                cover,
                item.Description?.Trim() ?? string.Empty));
        }

        return candidates;
    }

    /// <inheritdoc />
    public async Task<ResolvedMedia> ResolveAsync(string bvid, CancellationToken cancellationToken = default)
    {
        if (!IsValidBvid(bvid))
        {
            throw new BilibiliSourceException("That does not look like a Bilibili video id (BV…).");
        }

        return await ResolveCoreAsync(bvid, cancellationToken);
    }

    /// <summary>
    /// Resolves <paramref name="bvid"/> into a playable stream and its metadata.
    /// </summary>
    /// <param name="bvid">Bilibili video id, e.g. <c>BV1GJ411x7h7</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The audio URL, title, uploader, cover and any readable subtitles.</returns>
    /// <exception cref="BilibiliSourceException">The id is malformed or no audio could be resolved.</exception>
    /// <remarks>
    /// Metadata is best-effort on the bridge side: a video whose details cannot be read still
    /// resolves, in which case the title falls back to the id and the artwork to <c>null</c>. Only
    /// a missing audio stream is treated as a failure.
    /// </remarks>
    private async Task<ResolvedMedia> ResolveCoreAsync(string bvid, CancellationToken cancellationToken)
    {
        BridgeResponse? response = await GetFromBridgeAsync<BridgeResponse>(
            $"{BridgeRoot}/resolve?bvid={Uri.EscapeDataString(bvid)}",
            "The bridge did not answer in time. Is the Bilibili audio still resolvable?",
            cancellationToken);

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
    /// Builds the message shown when the bridge cannot be reached at all.
    /// </summary>
    /// <param name="exception">Socket-level failure raised by the HTTP client.</param>
    /// <remarks>
    /// One definition because both /search and /resolve fail the same way, and telling the user to
    /// start the bridge is the single most useful thing either path can say.
    /// </remarks>
    private static BilibiliSourceException BridgeUnreachable(HttpRequestException exception) =>
        new($"Could not reach the local bridge at {BridgeRoot}. Start it with “node bridge.cjs”. "
            + $"({exception.Message})");

    /// <summary>
    /// Issues a GET against the bridge and deserialises the payload.
    /// </summary>
    /// <typeparam name="TPayload">Expected response shape.</typeparam>
    /// <param name="url">Absolute bridge URL.</param>
    /// <param name="timeoutMessage">Message to use when the bridge stops answering.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deserialised payload, or <c>null</c> when the body was empty.</returns>
    /// <exception cref="BilibiliSourceException">The bridge is unreachable, refused, or answered oddly.</exception>
    /// <remarks>
    /// <see cref="HttpClient.GetFromJsonAsync{TPayload}(string?, CancellationToken)"/> cannot be used
    /// here: it throws on any non-success status and discards the body, and the bridge puts the
    /// reason in that body. A Bilibili rate limit then reached the user as "could not reach the
    /// bridge", which sends them off to restart a process that is running fine.
    /// </remarks>
    private static async Task<TPayload?> GetFromBridgeAsync<TPayload>(
        string url,
        string timeoutMessage,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        HttpResponseMessage response;

        try
        {
            response = await Client.GetAsync(url, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BilibiliSourceException(timeoutMessage);
        }
        catch (HttpRequestException exception)
        {
            throw BridgeUnreachable(exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string reason = await ReadBridgeErrorAsync(response, cancellationToken);

                throw new BilibiliSourceException(
                    $"The bridge refused the request ({(int)response.StatusCode}): {reason}");
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<TPayload>(cancellationToken);
            }
            catch (JsonException)
            {
                throw new BilibiliSourceException("The bridge returned a response SongPlayer cannot read.");
            }
        }
    }

    /// <summary>
    /// Extracts the human-readable reason from a failed bridge response.
    /// </summary>
    /// <param name="response">Non-success response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The bridge's <c>error</c> text, or the status line when the body is unusable.</returns>
    private static async Task<string> ReadBridgeErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            BridgeError? error = await response.Content.ReadFromJsonAsync<BridgeError>(cancellationToken);

            if (!string.IsNullOrWhiteSpace(error?.Error))
            {
                return error.Error.Trim();
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // A plain-text or empty body is not worth another message; the caller adds the status.
        }

        return response.ReasonPhrase ?? "no reason given";
    }

    /// <summary>
    /// Checks the shape of a Bilibili video id before spending a round trip on it.
    /// </summary>
    /// <param name="bvid">Candidate id.</param>
    /// <returns>True when it matches <c>BV</c> plus ten alphanumerics.</returns>
    private static bool IsValidBvid(string bvid) => Bvid.IsValid(bvid);

    /// <summary>
    /// Shape of the bridge's failure payload, e.g. <c>{"error":"Invalid BVID"}</c>.
    /// </summary>
    /// <param name="Error">Reason the bridge refused the request.</param>
    private sealed record BridgeError([property: JsonPropertyName("error")] string? Error);

    /// <summary>
    /// Shape of the bridge's <c>/search</c> payload.
    /// </summary>
    /// <param name="Source">Provider that produced the list.</param>
    /// <param name="Keyword">Keyword that was searched for.</param>
    /// <param name="Results">Candidate videos in ranked order.</param>
    private sealed record SearchResponse(
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("keyword")] string? Keyword,
        [property: JsonPropertyName("results")] IReadOnlyList<BridgeSearchItem>? Results);

    /// <summary>
    /// One candidate as the bridge serialises it.
    /// </summary>
    /// <param name="Bvid">Video id.</param>
    /// <param name="Title">Video title, already stripped of highlight markup.</param>
    /// <param name="Uploader">Uploading account.</param>
    /// <param name="DurationSeconds">Length in seconds, converted from the endpoint's clock string.</param>
    /// <param name="CoverUri">Absolute thumbnail URL.</param>
    /// <param name="Description">Short description.</param>
    private sealed record BridgeSearchItem(
        [property: JsonPropertyName("bvid")] string? Bvid,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("uploader")] string? Uploader,
        [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
        [property: JsonPropertyName("coverUrl")] string? CoverUri,
        [property: JsonPropertyName("description")] string? Description);

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
