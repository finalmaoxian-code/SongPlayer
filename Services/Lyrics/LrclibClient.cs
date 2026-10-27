using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SongPlayer.Services.Lyrics;

/// <summary>
/// Query parameters for a lyric database lookup.
/// </summary>
/// <remarks>
/// Every field is optional on purpose. The live API treats a supplied field as an exact filter, so a
/// zero-length duration or an invented artist name turns a match into a guaranteed miss. Callers only
/// fill in what they actually know.
/// </remarks>
/// <param name="Title">Song name, when it is known to be a song name.</param>
/// <param name="Artist">Performing artist, when that is known and not a stand-in for something else.</param>
/// <param name="Album">Album name, when known.</param>
/// <param name="Duration">Track length, when known and non-zero.</param>
/// <param name="Keyword">Free-text search text, used when the structured fields are too thin.</param>
public sealed record LrclibQuery(
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    TimeSpan? Duration = null,
    string? Keyword = null
)
{
    /// <summary>True when a structured exact query can be attempted.</summary>
    public bool CanQueryExactly => !string.IsNullOrWhiteSpace(Title);

    /// <summary>True when there is at least one search term.</summary>
    public bool HasAny => CanQueryExactly || !string.IsNullOrWhiteSpace(Keyword);
}

/// <summary>
/// One record from the lyric database.
/// </summary>
/// <param name="Id">Record identifier, stable enough to persist and fetch again later.</param>
/// <param name="TrackName">Song name the record claims.</param>
/// <param name="ArtistName">Artist the record claims.</param>
/// <param name="AlbumName">Album the record claims.</param>
/// <param name="Duration">Length the record claims, or <see langword="null"/> when it does not say.</param>
/// <param name="Instrumental">True when the record is flagged as having no vocals.</param>
/// <param name="PlainLyrics">Untimed lyric text.</param>
/// <param name="SyncedLyrics">LRC body with timestamps.</param>
public sealed record LrclibRecord(
    long Id,
    string? TrackName,
    string? ArtistName,
    string? AlbumName,
    double? Duration,
    bool Instrumental,
    string? PlainLyrics,
    string? SyncedLyrics
)
{
    /// <summary>Length as a time span, or <see langword="null"/> when unusable.</summary>
    public TimeSpan? DurationSpan =>
        Duration is { } seconds && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>True when the record carries a timed lyric body.</summary>
    public bool HasSynced => !string.IsNullOrWhiteSpace(SyncedLyrics);

    /// <summary>True when the record carries untimed lyric text.</summary>
    public bool HasPlain => !string.IsNullOrWhiteSpace(PlainLyrics);

    /// <summary>True when picking this record would put something on screen.</summary>
    public bool HasContent => HasSynced || HasPlain || Instrumental;
}

/// <summary>
/// Why a lyric database request failed.
/// </summary>
/// <remarks>
/// The distinctions matter because they lead to different behaviour: a throttled or unavailable
/// server is worth retrying later and must never be cached as "no lyrics"; a rejected request means
/// the query was wrong; a network failure is the user's connection.
/// </remarks>
public enum LrclibFailureKind
{
    /// <summary>No connection, DNS failure, TLS failure.</summary>
    Network,

    /// <summary>The request took longer than the client timeout.</summary>
    Timeout,

    /// <summary>The server answered 429; <see cref="LrclibException.RetryAfter"/> may say for how long.</summary>
    Throttled,

    /// <summary>The server answered 5xx (the live API returns 503 "ServerOverloaded" while busy).</summary>
    Unavailable,

    /// <summary>The request was rejected or the payload was not the documented shape.</summary>
    InvalidResponse,
}

/// <summary>Raised when a lyric database request could not be completed.</summary>
public sealed class LrclibException : Exception
{
    /// <summary>Creates the exception.</summary>
    public LrclibException(
        LrclibFailureKind kind,
        string message,
        TimeSpan? retryAfter = null,
        Exception? inner = null
    )
        : base(message, inner)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }

    /// <summary>Which kind of failure this is.</summary>
    public LrclibFailureKind Kind { get; }

    /// <summary>How long the server asked us to wait, when it said.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// Reads lyric records from the lyric database over HTTP.
/// </summary>
/// <remarks>
/// Deliberately thin: it knows the wire contract and nothing about matching. Implementations must not
/// block the calling thread and must translate transport trouble into <see cref="LrclibException"/>
/// rather than returning an empty list, because "the server is down" and "the song has no lyrics" are
/// different answers.
/// </remarks>
public interface ILrclibClient
{
    /// <summary>
    /// Exact lookup. Returns <see langword="null"/> when the database has no such record.
    /// </summary>
    Task<LrclibRecord?> GetAsync(LrclibQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Free-form or structured search. Returns an empty list when nothing matched.
    /// </summary>
    Task<IReadOnlyList<LrclibRecord>> SearchAsync(
        LrclibQuery query,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Fetches one record by identifier, used to honour a saved manual choice.
    /// </summary>
    Task<LrclibRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>
/// HTTP implementation of <see cref="ILrclibClient"/> for the public LRCLIB database.
/// </summary>
/// <remarks>
/// The contract implemented here was established by probing the live service rather than from the
/// published docs page, which is a client-side app that renders nothing useful to a plain HTTP
/// request: <c>/api/get</c> answers with a single object or a 404 carrying
/// <c>{"name":"TrackNotFound"}</c>; <c>/api/search</c> answers with an array capped at 20 entries;
/// <c>/api/get/{id}</c> answers with a single object; an overloaded server answers 503
/// <c>{"name":"ServerOverloaded"}</c>; and 429 responses advertise <c>Retry-After</c>. Duration is an
/// exact filter when supplied, which is why it is only sent with a real value.
/// </remarks>
public sealed class LrclibClient : ILrclibClient
{
    /// <summary>Base address of the service.</summary>
    private static readonly Uri BaseAddress = new("https://lrclib.net/api/");

    /// <summary>How long a single request may take before it is treated as a timeout.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    /// <summary>Shortest duration the service accepts in an exact query.</summary>
    private const double MinimumDurationSeconds = 1;

    /// <summary>Longest duration the service accepts in an exact query.</summary>
    /// <remarks>
    /// The endpoint validates this range and answers 400 outside it, so a value beyond it must be
    /// left out rather than sent. One hour is the service's own limit, not this app's opinion.
    /// </remarks>
    private const double MaximumDurationSeconds = 3600;

    /// <summary>
    /// One client per instance: a handler per request would leak sockets, and the lyric database is
    /// queried on every track change, so the composition root keeps one instance for the process.
    /// </summary>
    /// <remarks>
    /// The handler is injectable so the wire contract — status codes, headers and payload shapes — can
    /// be exercised without the live service. Tests pass a stub handler; production passes nothing and
    /// gets a pooled default handler with decompression enabled.
    /// </remarks>
    private readonly HttpClient _client;

    /// <summary>Options for reading records; the property names already match the wire format.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Creates a client talking to the public lyric database.</summary>
    public LrclibClient()
        : this(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { }

    /// <summary>Creates a client over a supplied handler, used by the contract tests.</summary>
    /// <param name="handler">Handler that answers the requests.</param>
    public LrclibClient(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        HttpClient client = new(handler, disposeHandler: false)
        {
            BaseAddress = BaseAddress,
            Timeout = RequestTimeout,
        };

        // The service asks clients to identify themselves; an anonymous flood is what gets throttled.
        client.DefaultRequestHeaders.Add("User-Agent", "SongPlayer/1.0");
        client.DefaultRequestHeaders.Add("Lrclib-Client", "SongPlayer/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _client = client;
    }

    /// <inheritdoc />
    public async Task<LrclibRecord?> GetAsync(
        LrclibQuery query,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!query.CanQueryExactly)
        {
            return null;
        }

        string url = "get" + BuildQueryString(query, includeKeyword: false);
        return await SendRecordAsync(url, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LrclibRecord>> SearchAsync(
        LrclibQuery query,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!query.HasAny)
        {
            return [];
        }

        string url = "search" + BuildQueryString(query, includeKeyword: true);
        using HttpResponseMessage response = await SendAsync(url, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
        {
            return [];
        }

        ThrowIfFailed(response);

        LrclibRecord[]? records;
        try
        {
            records = await response
                .Content.ReadFromJsonAsync<LrclibRecord[]>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new LrclibException(
                LrclibFailureKind.InvalidResponse,
                "The lyric database returned a response this app cannot read.",
                inner: exception
            );
        }

        return records is { Length: > 0 } ? records : [];
    }

    /// <inheritdoc />
    public Task<LrclibRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default
    ) => SendRecordAsync($"get/{id}", cancellationToken);

    /// <summary>Sends a request that either yields one record or nothing.</summary>
    private async Task<LrclibRecord?> SendRecordAsync(
        string url,
        CancellationToken cancellationToken
    )
    {
        using HttpResponseMessage response = await SendAsync(url, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
        {
            return null;
        }

        ThrowIfFailed(response);

        try
        {
            return await response
                .Content.ReadFromJsonAsync<LrclibRecord>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new LrclibException(
                LrclibFailureKind.InvalidResponse,
                "The lyric database returned a response this app cannot read.",
                inner: exception
            );
        }
    }

    /// <summary>Sends the request, mapping transport trouble onto <see cref="LrclibException"/>.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        string url,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await _client
                .GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller did not cancel, so this is the client timeout rather than a user action.
            throw new LrclibException(
                LrclibFailureKind.Timeout,
                "The lyric database did not answer in time."
            );
        }
        catch (HttpRequestException exception)
        {
            throw new LrclibException(
                LrclibFailureKind.Network,
                "Could not reach the lyric database.",
                inner: exception
            );
        }
    }

    /// <summary>Translates a non-success status code into the matching failure kind.</summary>
    private static void ThrowIfFailed(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new LrclibException(
                LrclibFailureKind.Throttled,
                "The lyric database is rate limiting this app. Try again shortly.",
                ReadRetryAfter(response)
            );
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new LrclibException(
                LrclibFailureKind.Unavailable,
                "The lyric database is busy right now. Try again in a moment.",
                ReadRetryAfter(response)
            );
        }

        throw new LrclibException(
            LrclibFailureKind.InvalidResponse,
            $"The lyric database rejected the request ({(int)response.StatusCode})."
        );
    }

    /// <summary>Reads <c>Retry-After</c> when the server sent one, in either allowed form.</summary>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } header)
        {
            if (header.Delta is { } delta)
            {
                return delta;
            }

            if (header.Date is { } date)
            {
                TimeSpan wait = date - DateTimeOffset.UtcNow;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
        }

        return null;
    }

    /// <summary>Builds the parameter string, adding only the values that are actually known.</summary>
    private static string BuildQueryString(LrclibQuery query, bool includeKeyword)
    {
        List<string> parts = [];

        if (!string.IsNullOrWhiteSpace(query.Title))
        {
            parts.Add("track_name=" + Uri.EscapeDataString(query.Title.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(query.Artist))
        {
            parts.Add("artist_name=" + Uri.EscapeDataString(query.Artist.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(query.Album))
        {
            parts.Add("album_name=" + Uri.EscapeDataString(query.Album.Trim()));
        }

        if (query.Duration is { } duration && duration > TimeSpan.Zero)
        {
            // The service filters on this value, so a rounded-but-wrong length loses the match.
            // It also validates the range and answers 400 outside it — a long video or a DJ set is
            // a perfectly normal track, so an out-of-range length is left out instead of being
            // sent and turning a usable search into a hard failure.
            double seconds = Math.Round(duration.TotalSeconds);
            if (seconds >= MinimumDurationSeconds && seconds <= MaximumDurationSeconds)
            {
                parts.Add(
                    "duration="
                        + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                );
            }
        }

        if (includeKeyword && !string.IsNullOrWhiteSpace(query.Keyword))
        {
            parts.Add("q=" + Uri.EscapeDataString(query.Keyword.Trim()));
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }
}
