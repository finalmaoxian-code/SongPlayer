using SongPlayer.Services.Bilibili;

namespace SongPlayer.Tests;

/// <summary>
/// Tests for the request signature Bilibili requires.
/// </summary>
/// <remarks>
/// The expected values are fixed vectors computed from the published algorithm with the reference key
/// pair, so a refactor that changes the parameter order, the escaping or the key shuffle fails here
/// instead of failing against the live service.
/// </remarks>
public sealed class WbiSignerTests
{
    private const string ImageKey = "7cd084941338484aae1ad9425b84077c";
    private const string SubKey = "4932caff0ff746eab6f01bf08b70ac45";

    private static readonly DateTimeOffset Moment = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void The_signature_matches_the_reference_implementation()
    {
        string signed = WbiSigner.Sign("bvid=BV1xx411c7mD", ImageKey, SubKey, Moment);

        Assert.Equal("bvid=BV1xx411c7mD&wts=1700000000&w_rid=a6c559afe0db97d811712e2602ea9774", signed);
    }

    [Fact]
    public void Parameters_are_sorted_by_name_before_signing()
    {
        // Deliberately out of order on the way in; cid must still come first.
        string signed = WbiSigner.Sign("platform=pc&fnval=16&cid=62131", ImageKey, SubKey, Moment);

        Assert.Equal("cid=62131&fnval=16&platform=pc&wts=1700000000&w_rid=befda58f77b4dedb48b149f5e197c8fc", signed);
    }

    [Fact]
    public void A_keyword_is_re_escaped_exactly_once()
    {
        // An unescaped space has to become %20 in the signed text as well as on the wire.
        string signed = WbiSigner.Sign("keyword=radiohead creep&search_type=video", ImageKey, SubKey, Moment);

        Assert.Equal(
            "keyword=radiohead%20creep&search_type=video&wts=1700000000&w_rid=69b907b5dfa563b03b69b77740900b60",
            signed);
    }

    [Fact]
    public void An_already_escaped_value_is_not_escaped_twice()
    {
        string once = WbiSigner.Sign("keyword=radiohead creep", ImageKey, SubKey, Moment);
        string twice = WbiSigner.Sign("keyword=radiohead%20creep", ImageKey, SubKey, Moment);

        Assert.Equal(once, twice);
    }

    [Fact]
    public async Task A_signature_is_stamped_with_the_current_time()
    {
        var signer = new WbiSigner(_ => Task.FromResult((ImageKey, SubKey)));

        string signed = await signer.SignAsync("bvid=BV1xx411c7mD");

        Assert.Contains("w_rid=", signed, StringComparison.Ordinal);
        Assert.Matches(@"wts=\d{10}", signed);

        // The stamp must be "now", not a constant: Bilibili rejects stale signatures.
        long stamp = long.Parse(signed.Split('&').First(part => part.StartsWith("wts=", StringComparison.Ordinal))[4..]);
        Assert.True(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - stamp) < 60);
    }

    [Fact]
    public async Task Keys_are_fetched_once_and_reused()
    {
        int fetches = 0;
        var signer = new WbiSigner(_ =>
        {
            Interlocked.Increment(ref fetches);
            return Task.FromResult((ImageKey, SubKey));
        });

        await signer.SignAsync("bvid=BV1xx411c7mD");
        await signer.SignAsync("bvid=BV1AM4y1z7Mn");

        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task A_failed_fetch_is_not_cached_so_the_next_call_tries_again()
    {
        int attempts = 0;
        var signer = new WbiSigner(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new BilibiliSourceException("keys unavailable");
            }

            return Task.FromResult((ImageKey, SubKey));
        });

        await Assert.ThrowsAsync<BilibiliSourceException>(() => signer.SignAsync("bvid=BV1xx411c7mD"));

        string signed = await signer.SignAsync("bvid=BV1xx411c7mD");

        Assert.Contains("w_rid=", signed, StringComparison.Ordinal);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_key_fetch()
    {
        int fetches = 0;
        var signer = new WbiSigner(async _ =>
        {
            Interlocked.Increment(ref fetches);
            await Task.Delay(20);
            return (ImageKey, SubKey);
        });

        await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            signer.SignAsync($"bvid=BV1xx411c7mD&index={index}")));

        Assert.Equal(1, fetches);
    }
}
