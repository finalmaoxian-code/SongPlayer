using System.Net.Http;
using System.Text.Json;

namespace SongPlayer.Services.Bilibili;

/// <summary>
/// Talks to Bilibili's public web API directly: search, video metadata, audio stream and subtitles.
/// </summary>
/// <remarks>
/// This replaces the local Node helper. Bilibili's endpoints are reachable from the app itself, so the
/// helper process — and the "start <c>node bridge.cjs</c> first" step that came with it — is gone.
/// <para>
/// Two rules shape the code. First, a signed URL is a snapshot: it carries a deadline, so it is
/// resolved per play request and never stored. Second, metadata is best-effort: a video whose details
/// cannot be read still plays, because making playback depend on an endpoint that rate-limits would
/// turn a working song into an error message.
/// </para>
/// </remarks>
public sealed class BilibiliClient : IBilibiliSource
{
    /// <summary>Search results the page can usefully show.</summary>
    private const int MaximumSearchResults = 30;

    /// <summary>
    /// Audio quality asked for when several streams are offered.
    /// </summary>
    /// <remarks>
    /// <c>fnval: 16</c> is what selects the DASH payload at all; without it Bilibili answers with the
    /// legacy single-file format, which for music videos is usually a lower bitrate.
    /// </remarks>
    private const int DashFlag = 16;

    private readonly HttpClient _client;
    private readonly WbiSigner _signer;

    /// <summary>
    /// Creates a client that uses the shared connection.
    /// </summary>
    public BilibiliClient()
        : this(BilibiliWeb.Client, new WbiSigner()) { }

    /// <summary>
    /// Creates a client over an injected transport, for tests.
    /// </summary>
    /// <param name="client">HTTP client; its default headers are expected to be set already.</param>
    /// <param name="signer">Signer for the endpoints that need one.</param>
    public BilibiliClient(HttpClient client, WbiSigner signer)
    {
        _client = client;
        _signer = signer;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BilibiliSearchResult>> SearchAsync(
        string keyword,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        string query = keyword.Trim();

        if (query.Length == 0)
        {
            throw new BilibiliSourceException("Type something to search for first.");
        }

        // The endpoint takes the keyword unescaped inside the signed string; Sign escapes it.
        string signed = await _signer.SignAsync(
            $"keyword={query}&search_type=video",
            cancellationToken
        );

        using JsonDocument document = await BilibiliWeb.GetJsonAsync(
            _client,
            $"{BilibiliWeb.ApiRoot}/x/web-interface/wbi/search/type?{signed}",
            cancellationToken
        );

        JsonElement result = ReadPayload(document);

        if (
            !result.TryGetProperty("result", out JsonElement rows)
            || rows.ValueKind != JsonValueKind.Array
        )
        {
            // A genuinely empty search answers without a result array; that is a miss, not a failure.
            return [];
        }

        var candidates = new List<BilibiliSearchResult>();
        int requested = limit > 0 ? Math.Min(limit, MaximumSearchResults) : 20;

        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (candidates.Count >= requested)
            {
                break;
            }

            if (ReadString(row, "bvid") is not { } bvid || !Bvid.IsValid(bvid))
            {
                continue;
            }

            candidates.Add(
                new BilibiliSearchResult(
                    bvid,
                    ReadString(row, "title") is { } title ? StripHighlight(title) : bvid,
                    ReadString(row, "author") ?? "Unknown uploader",
                    BilibiliWeb.ParseClock(ReadString(row, "duration")),
                    BilibiliWeb.AbsoluteUri(ReadString(row, "pic")),
                    ReadString(row, "description") ?? string.Empty
                )
            );
        }

        return candidates;
    }

    /// <inheritdoc />
    public async Task<ResolvedMedia> ResolveAsync(
        string bvid,
        CancellationToken cancellationToken = default
    )
    {
        if (!Bvid.IsValid(bvid))
        {
            throw new BilibiliSourceException("That does not look like a Bilibili video id (BV…).");
        }

        VideoInfo info = await ReadVideoAsync(bvid, cancellationToken);

        Uri audio = await ReadAudioAsync(bvid, info.Cid, cancellationToken);

        // Subtitles are the one part that usually needs a login, so they are read after the audio is
        // already secured and their absence is never allowed to fail the play.
        IReadOnlyList<BilibiliCaption> captions = await TryReadCaptionsAsync(
            bvid,
            info.Cid,
            cancellationToken
        );

        return new ResolvedMedia(
            bvid,
            audio,
            info.Title,
            info.Uploader,
            info.CoverUri,
            info.Description,
            info.Duration,
            captions
        );
    }

    /// <summary>
    /// Reads the video's metadata and the content id needed to ask for its stream.
    /// </summary>
    /// <param name="bvid">Video id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="BilibiliSourceException">The video does not exist or Bilibili refused the call.</exception>
    /// <remarks>
    /// A missing <c>cid</c> is fatal — it is the key to the stream — but every other field is allowed to
    /// fall back, so a metadata hiccup does not stop playback.
    /// </remarks>
    private async Task<VideoInfo> ReadVideoAsync(string bvid, CancellationToken cancellationToken)
    {
        string signed = await _signer.SignAsync($"bvid={bvid}", cancellationToken);

        using JsonDocument document = await BilibiliWeb.GetJsonAsync(
            _client,
            $"{BilibiliWeb.ApiRoot}/x/web-interface/wbi/view?{signed}",
            cancellationToken
        );

        JsonElement data = ReadPayload(document);

        long cid = ReadLong(data, "cid");

        // A multi-part upload carries its cid per page; the first page is the one the app plays.
        if (
            data.TryGetProperty("pages", out JsonElement pages)
            && pages.ValueKind == JsonValueKind.Array
            && pages.GetArrayLength() > 0
            && ReadLong(pages[0], "cid") is > 0 and var pageCid
        )
        {
            cid = pageCid;
        }

        if (cid <= 0)
        {
            throw new BilibiliSourceException(
                "Bilibili did not report a playable stream for that video."
            );
        }

        // The length of the page is the length of the audio about to be heard; the video-level value is
        // the fallback for payloads without a pages array.
        TimeSpan duration = ReadSeconds(data, "duration");

        return new VideoInfo(
            cid,
            ReadString(data, "title") ?? bvid,
            ReadString(data, "owner", "name") ?? "Bilibili",
            BilibiliWeb.AbsoluteUri(ReadString(data, "pic")),
            ReadString(data, "desc") ?? string.Empty,
            duration
        );
    }

    /// <summary>
    /// Asks for the DASH audio streams and picks the best one.
    /// </summary>
    /// <param name="bvid">Video id.</param>
    /// <param name="cid">Content id of the page to play.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="BilibiliSourceException">No audio stream could be resolved.</exception>
    /// <remarks>
    /// The highest bandwidth is chosen rather than the first entry: Bilibili returns several encodings
    /// and their order is not a promise. The legacy <c>durl</c> payload is used only when the DASH list is
    /// empty, which happens for a handful of old uploads.
    /// </remarks>
    private async Task<Uri> ReadAudioAsync(
        string bvid,
        long cid,
        CancellationToken cancellationToken
    )
    {
        string signed = await _signer.SignAsync(
            $"bvid={bvid}&cid={cid}&fnval={DashFlag}&platform=pc",
            cancellationToken
        );

        using JsonDocument document = await BilibiliWeb.GetJsonAsync(
            _client,
            $"{BilibiliWeb.ApiRoot}/x/player/wbi/playurl?{signed}",
            cancellationToken
        );

        JsonElement data = ReadPayload(document);

        if (
            data.TryGetProperty("dash", out JsonElement dash)
            && dash.ValueKind == JsonValueKind.Object
            && dash.TryGetProperty("audio", out JsonElement audio)
            && audio.ValueKind == JsonValueKind.Array
            && audio.GetArrayLength() > 0
        )
        {
            string? best = audio
                .EnumerateArray()
                .OrderByDescending(stream => ReadLong(stream, "bandwidth"))
                .Select(stream => ReadString(stream, "base_url") ?? ReadString(stream, "baseUrl"))
                .FirstOrDefault(url => BilibiliWeb.AbsoluteUri(url) is not null);

            if (BilibiliWeb.AbsoluteUri(best) is { } stream)
            {
                return stream;
            }
        }

        if (
            data.TryGetProperty("durl", out JsonElement durl)
            && durl.ValueKind == JsonValueKind.Array
            && durl.GetArrayLength() > 0
            && BilibiliWeb.AbsoluteUri(ReadString(durl[0], "url")) is { } legacy
        )
        {
            return legacy;
        }

        throw new BilibiliSourceException(
            "Bilibili did not hand out an audio stream for that video."
        );
    }

    /// <summary>
    /// Reads the publicly visible subtitle track, if there is one.
    /// </summary>
    /// <param name="bvid">Video id.</param>
    /// <param name="cid">Content id of the page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The timed lines, or an empty list.</returns>
    /// <remarks>
    /// Most subtitle tracks are only handed to a signed-in session, so an empty list is the normal
    /// outcome and not something to report. Human-made subtitles are preferred over machine ones, and
    /// only the two Bilibili subtitle hosts are read — the URL comes from a remote payload, so it is not
    /// followed blindly.
    /// </remarks>
    private async Task<IReadOnlyList<BilibiliCaption>> TryReadCaptionsAsync(
        string bvid,
        long cid,
        CancellationToken cancellationToken
    )
    {
        try
        {
            string signed = await _signer.SignAsync($"bvid={bvid}&cid={cid}", cancellationToken);

            using JsonDocument document = await BilibiliWeb.GetJsonAsync(
                _client,
                $"{BilibiliWeb.ApiRoot}/x/player/wbi/v2?{signed}",
                cancellationToken
            );

            if (
                !document.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("subtitle", out JsonElement subtitle)
                || subtitle.ValueKind != JsonValueKind.Object
                || !subtitle.TryGetProperty("subtitles", out JsonElement tracks)
                || tracks.ValueKind != JsonValueKind.Array
            )
            {
                return [];
            }

            JsonElement? chosen = null;

            foreach (JsonElement track in tracks.EnumerateArray())
            {
                chosen ??= track;

                // ai_type 0 marks a human-made track; the machine ones are numbered higher.
                if (
                    track.TryGetProperty("ai_type", out JsonElement aiType)
                    && aiType.ValueKind == JsonValueKind.Number
                    && aiType.GetInt32() == 0
                )
                {
                    chosen = track;
                    break;
                }
            }

            if (
                chosen is not { } selected
                || ReadString(selected, "subtitle_url") is not { } url
                || NormalizeSubtitleUri(url) is not { } subtitleUri
            )
            {
                return [];
            }

            using JsonDocument body = await BilibiliWeb.GetJsonAsync(
                _client,
                subtitleUri,
                cancellationToken
            );

            if (
                !body.RootElement.TryGetProperty("body", out JsonElement lines)
                || lines.ValueKind != JsonValueKind.Array
            )
            {
                return [];
            }

            var captions = new List<BilibiliCaption>(lines.GetArrayLength());

            foreach (JsonElement line in lines.EnumerateArray())
            {
                double seconds = ReadDouble(line, "from");
                string? text = ReadString(line, "content");

                if (double.IsFinite(seconds) && seconds >= 0 && !string.IsNullOrWhiteSpace(text))
                {
                    captions.Add(new BilibiliCaption(seconds, text.Trim()));
                }
            }

            // The highlight walks the list once, so an unordered track would hide lines.
            captions.Sort((left, right) => left.Seconds.CompareTo(right.Seconds));

            return captions;
        }
        catch (BilibiliSourceException)
        {
            // Subtitles are a bonus; a failure here must not turn a playable video into an error.
            return [];
        }
    }

    /// <summary>
    /// Accepts a subtitle URL only when it points at a Bilibili subtitle host.
    /// </summary>
    /// <param name="value">URL from the player payload.</param>
    /// <returns>An absolute URL, or <see langword="null"/> when it is not on an allowed host.</returns>
    /// <remarks>
    /// The payload is remote data, so it is treated as untrusted input: protocol-relative URLs are
    /// upgraded and anything outside <c>bilibili.com</c>/<c>hdslb.com</c> is refused rather than fetched.
    /// </remarks>
    private static string? NormalizeSubtitleUri(string value)
    {
        string raw = value.Trim();

        if (raw.StartsWith("//", StringComparison.Ordinal))
        {
            raw = "https:" + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        bool allowed =
            uri.Host == "bilibili.com"
            || uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host == "hdslb.com"
            || uri.Host.EndsWith(".hdslb.com", StringComparison.OrdinalIgnoreCase);

        return allowed ? uri.AbsoluteUri : null;
    }

    /// <summary>
    /// Reads the <c>data</c> object and turns a non-zero Bilibili code into a user-facing reason.
    /// </summary>
    /// <param name="document">Whole response.</param>
    /// <exception cref="BilibiliSourceException">Bilibili reported a failure code.</exception>
    /// <remarks>
    /// Bilibili reports errors with HTTP 200 and a <c>code</c> field, so the status line cannot be
    /// trusted. The most common codes are translated because "code -404" is not an explanation.
    /// </remarks>
    private static JsonElement ReadPayload(JsonDocument document)
    {
        JsonElement root = document.RootElement;

        if (
            root.TryGetProperty("code", out JsonElement code)
            && code.ValueKind == JsonValueKind.Number
            && code.GetInt64() is var value
            && value != 0
        )
        {
            throw value switch
            {
                -404 => new BilibiliSourceException("Bilibili says that video does not exist."),
                -403 => new BilibiliSourceException(
                    "Bilibili will not hand out that video to this app."
                ),
                -101 => new BilibiliSourceException(
                    "Bilibili asked for a login. Only public videos can be played."
                ),
                _ => new BilibiliSourceException(
                    $"Bilibili refused the request: {ReadString(root, "message") ?? "no reason given"}."
                ),
            };
        }

        return
            root.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Object
            ? data
            : default;
    }

    /// <summary>Reads a string property, tolerating a missing or non-string value.</summary>
    private static string? ReadString(JsonElement element, params string[] path)
    {
        JsonElement current = element;

        foreach (string name in path)
        {
            if (
                current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(name, out current)
            )
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    /// <summary>Reads an integer property, tolerating a numeric string.</summary>
    private static long ReadLong(JsonElement element, string name)
    {
        if (
            element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out JsonElement value)
        )
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out long number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out long parsed) => parsed,
            _ => 0,
        };
    }

    /// <summary>Reads a double property, tolerating a numeric string.</summary>
    private static double ReadDouble(JsonElement element, string name)
    {
        if (
            element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out JsonElement value)
        )
        {
            return double.NaN;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out double number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), out double parsed) =>
                parsed,
            _ => double.NaN,
        };
    }

    /// <summary>Reads a length in seconds.</summary>
    private static TimeSpan ReadSeconds(JsonElement element, string name)
    {
        double seconds = ReadDouble(element, name);
        return double.IsFinite(seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;
    }

    /// <summary>
    /// Removes the <c>&lt;em class="keyword"&gt;</c> markup the search endpoint wraps matches in.
    /// </summary>
    /// <param name="title">Raw title.</param>
    /// <remarks>
    /// The markup is presentational. Leaving it in would show angle brackets in the list and, worse,
    /// confuse the lyric matcher with text that is not part of the title.
    /// </remarks>
    private static string StripHighlight(string title)
    {
        string cleaned = title
            .Replace("<em class=\"keyword\">", string.Empty, StringComparison.Ordinal)
            .Replace("</em>", string.Empty, StringComparison.Ordinal);

        // Entity-decoding matters here: titles routinely contain &amp; and &#39;.
        cleaned = System.Net.WebUtility.HtmlDecode(cleaned).Trim();

        return cleaned.Length == 0 ? title.Trim() : cleaned;
    }

    /// <summary>Metadata read from the view endpoint.</summary>
    /// <param name="Cid">Content id of the page to play.</param>
    /// <param name="Title">Video title.</param>
    /// <param name="Uploader">Uploading account.</param>
    /// <param name="CoverUri">Absolute thumbnail URL, when there is one.</param>
    /// <param name="Description">Video description.</param>
    /// <param name="Duration">Video length, or zero when Bilibili did not report one.</param>
    private readonly record struct VideoInfo(
        long Cid,
        string Title,
        string Uploader,
        Uri? CoverUri,
        string Description,
        TimeSpan Duration
    );
}
