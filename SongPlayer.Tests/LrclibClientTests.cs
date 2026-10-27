using System.Net;
using System.Net.Http;
using System.Text;
using SongPlayer.Services.Lyrics;

namespace SongPlayer.Tests;

/// <summary>Handler that answers every request with a scripted status, headers and body.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string Body { get; set; } = "{}";

    public TimeSpan? RetryAfter { get; set; }

    public Exception? Throw { get; set; }

    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!.PathAndQuery);

        if (Throw is not null)
        {
            return Task.FromException<HttpResponseMessage>(Throw);
        }

        HttpResponseMessage response = new(Status)
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        };

        if (RetryAfter is { } wait)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
        }

        return Task.FromResult(response);
    }
}

/// <summary>
/// Wire-contract tests for the lyric database client.
/// </summary>
/// <remarks>
/// The shapes asserted here were established by probing the live service: a single object from
/// <c>get</c>, an array from <c>search</c>, 404 for "no such track", 503 for an overloaded server and
/// 429 with <c>Retry-After</c> for throttling. Those four cases lead to different behaviour upstream —
/// a miss may be cached briefly, a failure never — so the mapping is worth pinning down.
/// </remarks>
public sealed class LrclibClientTests
{
    private const string SyncedBody = """
        {
          "id": 496,
          "name": "Creep",
          "trackName": "Creep",
          "artistName": "Radiohead",
          "albumName": "Pablo Honey",
          "duration": 239.0,
          "instrumental": false,
          "plainLyrics": "When you were here before",
          "syncedLyrics": "[00:19.16] When you were here before"
        }
        """;

    [Fact]
    public async Task Get_returns_the_record_from_a_single_object_response()
    {
        StubHandler handler = new() { Body = SyncedBody };
        LrclibClient client = new(handler);

        LrclibRecord? record = await client.GetAsync(new LrclibQuery("Creep", "Radiohead"));

        Assert.NotNull(record);
        Assert.Equal(496, record!.Id);
        Assert.Equal("Creep", record.TrackName);
        Assert.Equal("Radiohead", record.ArtistName);
        Assert.True(record.HasSynced);
        Assert.Equal(TimeSpan.FromSeconds(239), record.DurationSpan);
        Assert.StartsWith("/api/get?", handler.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_returns_null_when_the_service_reports_no_such_track()
    {
        StubHandler handler = new()
        {
            Status = HttpStatusCode.NotFound,
            Body = """{"message":"Failed to find specified track","name":"TrackNotFound","statusCode":404}""",
        };
        LrclibClient client = new(handler);

        Assert.Null(await client.GetAsync(new LrclibQuery("Nope", "Nobody")));
    }

    [Fact]
    public async Task An_overloaded_server_is_a_failure_not_a_miss()
    {
        StubHandler handler = new()
        {
            Status = HttpStatusCode.ServiceUnavailable,
            Body = """{"message":"The server is busy, please retry in a moment","name":"ServerOverloaded","statusCode":503}""",
        };
        LrclibClient client = new(handler);

        LrclibException exception = await Assert.ThrowsAsync<LrclibException>(
            () => client.GetAsync(new LrclibQuery("Creep", "Radiohead")));

        Assert.Equal(LrclibFailureKind.Unavailable, exception.Kind);
    }

    [Fact]
    public async Task A_throttled_response_carries_the_retry_after_the_server_asked_for()
    {
        StubHandler handler = new()
        {
            Status = HttpStatusCode.TooManyRequests,
            Body = "{}",
            RetryAfter = TimeSpan.FromSeconds(30),
        };
        LrclibClient client = new(handler);

        LrclibException exception = await Assert.ThrowsAsync<LrclibException>(
            () => client.SearchAsync(new LrclibQuery(Keyword: "creep")));

        Assert.Equal(LrclibFailureKind.Throttled, exception.Kind);
        Assert.Equal(TimeSpan.FromSeconds(30), exception.RetryAfter);
    }

    [Fact]
    public async Task Search_returns_the_array_the_service_sends()
    {
        StubHandler handler = new()
        {
            Body = """
                [
                  {"id":1,"trackName":"Creep","artistName":"Radiohead","duration":239.0,"instrumental":false,"syncedLyrics":"[00:01.00]a"},
                  {"id":2,"trackName":"Creep (Live)","artistName":"Radiohead","duration":245.0,"instrumental":false,"plainLyrics":"b"}
                ]
                """,
        };
        LrclibClient client = new(handler);

        IReadOnlyList<LrclibRecord> records = await client.SearchAsync(new LrclibQuery(Keyword: "creep"));

        Assert.Equal(2, records.Count);
        Assert.True(records[0].HasSynced);
        Assert.True(records[1].HasPlain);
    }

    [Fact]
    public async Task Search_returns_an_empty_list_when_nothing_matched()
    {
        LrclibClient client = new(new StubHandler { Status = HttpStatusCode.NotFound, Body = "{}" });

        Assert.Empty(await client.SearchAsync(new LrclibQuery(Keyword: "zzz")));
    }

    [Fact]
    public async Task GetById_asks_for_the_record_endpoint()
    {
        StubHandler handler = new() { Body = SyncedBody };
        LrclibClient client = new(handler);

        LrclibRecord? record = await client.GetByIdAsync(496);

        Assert.Equal(496, record!.Id);
        Assert.Equal("/api/get/496", handler.Requests[0]);
    }

    [Fact]
    public async Task A_network_failure_is_reported_as_a_network_failure()
    {
        StubHandler handler = new() { Throw = new HttpRequestException("no route to host") };
        LrclibClient client = new(handler);

        LrclibException exception = await Assert.ThrowsAsync<LrclibException>(
            () => client.GetAsync(new LrclibQuery("Creep", "Radiohead")));

        Assert.Equal(LrclibFailureKind.Network, exception.Kind);
    }

    [Fact]
    public async Task An_unreadable_payload_is_reported_rather_than_silently_dropped()
    {
        LrclibClient client = new(new StubHandler { Body = "<html>still warming up</html>" });

        LrclibException exception = await Assert.ThrowsAsync<LrclibException>(
            () => client.GetByIdAsync(1));

        Assert.Equal(LrclibFailureKind.InvalidResponse, exception.Kind);
    }

    [Fact]
    public async Task Only_known_values_reach_the_query_string()
    {
        StubHandler handler = new() { Body = SyncedBody };
        LrclibClient client = new(handler);

        await client.GetAsync(new LrclibQuery(Title: "Creep", Artist: null, Album: null, Duration: TimeSpan.Zero));

        string request = handler.Requests[0];
        Assert.Contains("track_name=Creep", request, StringComparison.Ordinal);
        Assert.DoesNotContain("artist_name=", request, StringComparison.Ordinal);
        Assert.DoesNotContain("album_name=", request, StringComparison.Ordinal);
        Assert.DoesNotContain("duration=", request, StringComparison.Ordinal);
    }

    /// <summary>
    /// The service validates the duration range and answers 400 outside it, so a length it would
    /// reject must be left out. A DJ set or a long video is a normal track, not an error.
    /// </summary>
    [Fact]
    public async Task A_duration_the_service_would_reject_is_left_out()
    {
        StubHandler handler = new() { Body = SyncedBody };
        LrclibClient client = new(handler);

        await client.GetAsync(new LrclibQuery(Title: "Creep", Artist: null, Album: null, Duration: TimeSpan.FromSeconds(5400)));

        Assert.DoesNotContain("duration=", handler.Requests[0], StringComparison.Ordinal);
    }

    /// <summary>A duration inside the accepted range is still sent, rounded to whole seconds.</summary>
    [Fact]
    public async Task A_duration_inside_the_accepted_range_is_sent()
    {
        StubHandler handler = new() { Body = SyncedBody };
        LrclibClient client = new(handler);

        await client.GetAsync(new LrclibQuery(Title: "Creep", Artist: null, Album: null, Duration: TimeSpan.FromSeconds(238.6)));

        Assert.Contains("duration=239", handler.Requests[0], StringComparison.Ordinal);
    }
}
