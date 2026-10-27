using System.Net;
using System.Net.Http;
using System.Text;
using SongPlayer.Services.Bilibili;

namespace SongPlayer.Tests;

/// <summary>
/// Answers a request from a script instead of the network.
/// </summary>
/// <remarks>
/// Routes are matched on a substring of the request URI so a test states which endpoint it is
/// describing (<c>"/wbi/search/type"</c>) rather than reproducing the signature parameters, which are
/// covered separately.
/// </remarks>
internal sealed class RoutedHandler : HttpMessageHandler
{
    private readonly List<(string Match, Func<HttpResponseMessage> Answer)> _routes = [];

    /// <summary>Requests that were made, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Registers an answer for any URI containing <paramref name="match"/>.</summary>
    /// <param name="match">Substring of the request URI.</param>
    /// <param name="body">JSON body to answer with.</param>
    /// <param name="status">Status code to answer with.</param>
    public RoutedHandler On(string match, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((match, () => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

        return this;
    }

    /// <summary>Registers a transport failure for any URI containing <paramref name="match"/>.</summary>
    /// <param name="match">Substring of the request URI.</param>
    /// <param name="exception">Failure to raise.</param>
    public RoutedHandler OnFailure(string match, Exception exception)
    {
        _routes.Add((match, () => throw exception));

        return this;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string uri = request.RequestUri?.PathAndQuery ?? string.Empty;
        Requests.Add(uri);

        foreach ((string match, Func<HttpResponseMessage> answer) in _routes)
        {
            if (uri.Contains(match, StringComparison.Ordinal))
            {
                return Task.FromResult(answer());
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>
/// Tests for the direct Bilibili client.
/// </summary>
/// <remarks>
/// These run without a network: every payload below mirrors a real response captured from Bilibili, so
/// the parsing rules are pinned while the transport stays replaceable.
/// </remarks>
public sealed class BilibiliClientTests
{
    private const string NavBody = """
        {"code":-101,"message":"账号未登录","data":{"isLogin":false,
         "wbi_img":{"img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
                    "sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}
        """;

    private const string ViewBody = """
        {"code":0,"message":"OK","data":{
          "bvid":"BV1xx411c7mD","title":"Radiohead - Creep","cid":62131,"pic":"//i0.hdslb.com/bfs/a.jpg",
          "desc":"a description","duration":2055,"owner":{"name":"some uploader"},
          "pages":[{"cid":62131,"duration":2055}]}}
        """;

    private const string PlayurlBody = """
        {"code":0,"message":"OK","data":{"dash":{"audio":[
          {"id":30216,"bandwidth":67000,"baseUrl":"https://cdn.example/low.m4s"},
          {"id":30280,"bandwidth":192000,"base_url":"https://cdn.example/high.m4s"}]}}}
        """;

    private const string SearchBody = """
        {"code":0,"message":"OK","data":{"result":[
          {"bvid":"BV1xx411c7mD","title":"Radiohead - <em class=\"keyword\">Creep</em>","author":"u1",
           "duration":"4:03","pic":"//i0.hdslb.com/bfs/b.jpg","description":"d1"},
          {"bvid":"not-an-id","title":"junk","author":"u2","duration":"1:00"},
          {"bvid":"BV1AM4y1z7Mn","title":"Creep live","author":"u3","duration":"1:02:03"}]}}
        """;

    private const string SubtitlesBody = """
        {"code":0,"message":"OK","data":{"subtitle":{"subtitles":[
          {"lan":"en","ai_type":1,"subtitle_url":"//aisubtitle.hdslb.com/ai.json"},
          {"lan":"zh","ai_type":0,"subtitle_url":"//aisubtitle.hdslb.com/human.json"}]}}}
        """;

    private const string HumanSubtitleBody = """
        {"body":[{"from":2.5,"content":"second"},{"from":0.5,"content":"first"},{"from":-1,"content":"bad"}]}
        """;

    private static BilibiliClient Create(RoutedHandler handler, out HttpClient client)
    {
        client = BilibiliWeb.CreateClient(handler);

        // Fixed keys keep the signature out of these assertions; WbiSignerTests covers it.
        var signer = new WbiSigner(_ => Task.FromResult(("7cd084941338484aae1ad9425b84077c", "4932caff0ff746eab6f01bf08b70ac45")));

        return new BilibiliClient(client, signer);
    }

    [Fact]
    public async Task Resolve_reads_the_best_audio_stream_and_the_video_metadata()
    {
        var handler = new RoutedHandler()
            .On("/wbi/view", ViewBody)
            .On("/wbi/playurl", PlayurlBody)
            .On("/wbi/v2", SubtitlesBody)
            .On("human.json", HumanSubtitleBody);

        BilibiliClient client = Create(handler, out _);

        ResolvedMedia media = await client.ResolveAsync("BV1xx411c7mD");

        // Highest bandwidth wins; the first entry in the payload is the quiet one.
        Assert.Equal("https://cdn.example/high.m4s", media.AudioUri.AbsoluteUri);
        Assert.Equal("Radiohead - Creep", media.Title);
        Assert.Equal("some uploader", media.Uploader);
        Assert.Equal(TimeSpan.FromSeconds(2055), media.Duration);
        Assert.Equal("https://i0.hdslb.com/bfs/a.jpg", media.CoverUri?.AbsoluteUri);

        // Human subtitles are preferred and the lines are sorted.
        Assert.Equal(2, media.Captions.Count);
        Assert.Equal(0.5, media.Captions[0].Seconds);
        Assert.Equal("first", media.Captions[0].Text);
    }

    [Fact]
    public async Task A_missing_subtitle_track_does_not_fail_the_resolve()
    {
        var handler = new RoutedHandler()
            .On("/wbi/view", ViewBody)
            .On("/wbi/playurl", PlayurlBody)
            .On("/wbi/v2", """{"code":0,"data":{"subtitle":{"subtitles":[]}}}""");

        BilibiliClient client = Create(handler, out _);

        ResolvedMedia media = await client.ResolveAsync("BV1xx411c7mD");

        Assert.Empty(media.Captions);
        Assert.Equal(Uri.UriSchemeHttps, media.AudioUri.Scheme);
    }

    [Fact]
    public async Task A_subtitle_host_outside_bilibili_is_not_followed()
    {
        const string hostile = """
            {"code":0,"data":{"subtitle":{"subtitles":[
              {"ai_type":0,"subtitle_url":"//evil.example/track.json"}]}}}
            """;

        var handler = new RoutedHandler()
            .On("/wbi/view", ViewBody)
            .On("/wbi/playurl", PlayurlBody)
            .On("/wbi/v2", hostile)
            .On("track.json", HumanSubtitleBody);

        BilibiliClient client = Create(handler, out _);

        ResolvedMedia media = await client.ResolveAsync("BV1xx411c7mD");

        Assert.Empty(media.Captions);

        // The refused URL must never have been requested.
        Assert.DoesNotContain(handler.Requests, uri => uri.Contains("track.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_video_that_does_not_exist_reports_a_sentence_not_a_code()
    {
        var handler = new RoutedHandler()
            .On("/wbi/view", """{"code":-404,"message":"啥都木有","data":null}""");

        BilibiliClient client = Create(handler, out _);

        BilibiliSourceException exception = await Assert.ThrowsAsync<BilibiliSourceException>(
            () => client.ResolveAsync("BV1xx411c7mD"));

        Assert.Contains("does not exist", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-404", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_login_wall_says_so_instead_of_looking_like_a_network_error()
    {
        var handler = new RoutedHandler()
            .On("/wbi/view", """{"code":-101,"message":"账号未登录","data":null}""");

        BilibiliClient client = Create(handler, out _);

        BilibiliSourceException exception = await Assert.ThrowsAsync<BilibiliSourceException>(
            () => client.ResolveAsync("BV1xx411c7mD"));

        Assert.Contains("public videos", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_video_without_an_audio_stream_fails_with_a_reason()
    {
        var handler = new RoutedHandler()
            .On("/wbi/view", ViewBody)
            .On("/wbi/playurl", """{"code":0,"data":{"dash":{"audio":[]}}}""");

        BilibiliClient client = Create(handler, out _);

        BilibiliSourceException exception = await Assert.ThrowsAsync<BilibiliSourceException>(
            () => client.ResolveAsync("BV1xx411c7mD"));

        Assert.Contains("audio stream", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rate_limiting_is_reported_as_such()
    {
        var handler = new RoutedHandler()
            .On("/wbi/search/type", "{}", HttpStatusCode.TooManyRequests);

        BilibiliClient client = Create(handler, out _);

        BilibiliSourceException exception = await Assert.ThrowsAsync<BilibiliSourceException>(
            () => client.SearchAsync("creep", 20));

        Assert.Contains("rate-limiting", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_cleans_titles_and_drops_rows_without_a_usable_id()
    {
        var handler = new RoutedHandler().On("/wbi/search/type", SearchBody);

        BilibiliClient client = Create(handler, out _);

        IReadOnlyList<BilibiliSearchResult> results = await client.SearchAsync("creep", 20);

        Assert.Equal(2, results.Count);
        Assert.Equal("Radiohead - Creep", results[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(243), results[0].Duration);
        Assert.Equal("https://i0.hdslb.com/bfs/b.jpg", results[0].CoverUri?.AbsoluteUri);

        // A clock with hours is understood too.
        Assert.Equal(TimeSpan.FromSeconds(3723), results[1].Duration);
    }

    [Fact]
    public async Task Search_honours_the_requested_limit()
    {
        var handler = new RoutedHandler().On("/wbi/search/type", SearchBody);

        BilibiliClient client = Create(handler, out _);

        IReadOnlyList<BilibiliSearchResult> results = await client.SearchAsync("creep", 1);

        Assert.Single(results);
    }

    [Fact]
    public async Task An_empty_search_payload_is_a_miss_not_a_failure()
    {
        var handler = new RoutedHandler()
            .On("/wbi/search/type", """{"code":0,"data":{"result":[]}}""");

        BilibiliClient client = Create(handler, out _);

        Assert.Empty(await client.SearchAsync("nothing at all", 20));
    }

    [Fact]
    public async Task A_transport_failure_becomes_one_actionable_sentence()
    {
        var handler = new RoutedHandler()
            .OnFailure("/wbi/search/type", new HttpRequestException("no route to host"));

        BilibiliClient client = Create(handler, out _);

        BilibiliSourceException exception = await Assert.ThrowsAsync<BilibiliSourceException>(
            () => client.SearchAsync("creep", 20));

        Assert.Contains("Could not reach Bilibili", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_json_answer_is_reported_rather_than_parsed_as_an_empty_result()
    {
        var handler = new RoutedHandler().On("/wbi/search/type", "<html>blocked</html>");

        BilibiliClient client = Create(handler, out _);

        BilibiliSourceException exception = await Assert.ThrowsAsync<BilibiliSourceException>(
            () => client.SearchAsync("creep", 20));

        Assert.Contains("cannot read", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_malformed_id_is_rejected_without_a_request()
    {
        var handler = new RoutedHandler();
        BilibiliClient client = Create(handler, out _);

        await Assert.ThrowsAsync<BilibiliSourceException>(() => client.ResolveAsync("nope"));

        Assert.Empty(handler.Requests);
    }
}
