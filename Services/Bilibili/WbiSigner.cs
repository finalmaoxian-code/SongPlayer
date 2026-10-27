using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SongPlayer.Services.Bilibili;

/// <summary>
/// Signs a Bilibili web API query the way the site's own pages do.
/// </summary>
/// <remarks>
/// Bilibili requires a <c>w_rid</c> signature on several endpoints. The algorithm is public — it is
/// documented in the community API reference and implemented by every third-party client — and it is
/// short enough to reimplement from that description rather than vendoring someone else's source.
/// <para>
/// The keys come from <c>/x/web-interface/nav</c>, which answers even when nobody is logged in. They
/// rotate, so they are cached with an expiry rather than read once per request: signing costs one
/// extra HTTP round trip otherwise, which would double the latency of every lookup.
/// </para>
/// <para>
/// The class is an instance rather than a static because tests need to inject fixed keys; the app
/// creates exactly one and shares it.
/// </para>
/// </remarks>
public sealed class WbiSigner
{
    /// <summary>How long a fetched key pair is reused.</summary>
    /// <remarks>
    /// Bilibili rotates the pair roughly daily and does not announce when. An hour is short enough to
    /// survive a rotation without the user seeing a failed lookup and long enough that a burst of
    /// searches costs one key fetch in total.
    /// </remarks>
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// Characters of the shuffled key that are actually used.
    /// </summary>
    /// <remarks>
    /// The shuffle produces 64 characters but only the first 32 are mixed in; hashing all 64 silently
    /// changes every signature, which the fixed vectors in the tests catch.
    /// </remarks>
    private const int MixinKeyLength = 32;

    /// <summary>
    /// The order the 64 key characters are shuffled into before hashing.
    /// </summary>
    private static readonly int[] MixinKeyOrder =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61,
        26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36,
        20, 34, 44, 52,
    ];

    private readonly Func<CancellationToken, Task<(string ImageKey, string SubKey)>> _keySource;
    private readonly object _gate = new();
    private (string ImageKey, string SubKey) _keys;
    private DateTimeOffset _keysFetchedAt;
    private Task<(string ImageKey, string SubKey)>? _refresh;

    /// <summary>
    /// Creates a signer that reads its keys from the live endpoint.
    /// </summary>
    /// <param name="keySource">Fetches a fresh key pair; defaults to the nav endpoint.</param>
    public WbiSigner(Func<CancellationToken, Task<(string ImageKey, string SubKey)>>? keySource = null)
    {
        _keySource = keySource ?? BilibiliApiKeys.FetchAsync;
    }

    /// <summary>
    /// Appends the signature parameters to an already-built query string.
    /// </summary>
    /// <param name="query">Query string without a leading <c>?</c>, e.g. <c>bvid=BV…&amp;cid=123</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The same query with <c>wts</c> and <c>w_rid</c> added.</returns>
    /// <remarks>
    /// The parameters must be sorted by name and re-escaped before hashing, and the request must send
    /// exactly the signed text — so the signature is built from the same sorted string that is
    /// returned rather than appended to a string built elsewhere.
    /// </remarks>
    public async Task<string> SignAsync(string query, CancellationToken cancellationToken = default)
    {
        (string imageKey, string subKey) = await GetKeysAsync(cancellationToken);
        return Sign(query, imageKey, subKey, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Signs a query with explicit keys and time, without touching the network.
    /// </summary>
    /// <param name="query">Query string without a leading <c>?</c>.</param>
    /// <param name="imageKey">First half of the key pair.</param>
    /// <param name="subKey">Second half of the key pair.</param>
    /// <param name="now">Moment that becomes the <c>wts</c> parameter.</param>
    /// <returns>The signed query.</returns>
    /// <remarks>
    /// Exposed so the signature can be tested against fixed inputs; the expected values in the tests
    /// were produced by the published reference implementation.
    /// </remarks>
    public static string Sign(string query, string imageKey, string subKey, DateTimeOffset now)
    {
        var parameters = new List<KeyValuePair<string, string>>();

        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                continue;
            }

            // Values arrive already escaped; re-escaping the raw part keeps the signed text identical to
            // what is sent, which is the only thing the server checks.
            parameters.Add(new(
                Uri.EscapeDataString(Uri.UnescapeDataString(pair[..separator])),
                Uri.EscapeDataString(Uri.UnescapeDataString(pair[(separator + 1)..]))));
        }

        parameters.Add(new("wts", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        parameters.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));

        string body = string.Join('&', parameters.Select(parameter => $"{parameter.Key}={parameter.Value}"));
        string mixin = MixinKey(imageKey, subKey);
        string signature = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(body + mixin)));

        return body + "&w_rid=" + signature;
    }

    /// <summary>
    /// Derives the 32-character key that is appended before hashing.
    /// </summary>
    /// <param name="imageKey">First half of the key pair.</param>
    /// <param name="subKey">Second half of the key pair.</param>
    private static string MixinKey(string imageKey, string subKey)
    {
        string combined = imageKey + subKey;
        var builder = new StringBuilder(MixinKeyLength);

        foreach (int index in MixinKeyOrder)
        {
            if (builder.Length == MixinKeyLength)
            {
                break;
            }

            if (index < combined.Length)
            {
                builder.Append(combined[index]);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Returns the cached key pair, fetching it once per <see cref="KeyLifetime"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// A failed fetch is not cached, so a transient outage does not disable online playback until the
    /// process restarts. Concurrent callers share one fetch instead of starting one each.
    /// </remarks>
    private async Task<(string ImageKey, string SubKey)> GetKeysAsync(CancellationToken cancellationToken)
    {
        Task<(string ImageKey, string SubKey)> refresh;

        lock (_gate)
        {
            if (_keysFetchedAt != default && DateTimeOffset.UtcNow - _keysFetchedAt < KeyLifetime)
            {
                return _keys;
            }

            refresh = _refresh ??= FetchAsync();
        }

        (string ImageKey, string SubKey) keys;

        try
        {
            keys = await refresh.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!refresh.IsFaulted)
        {
            // This caller walked away. The shared fetch is still worth keeping, so a later caller can
            // reuse it instead of asking Bilibili for the same keys again.
            throw;
        }
        catch
        {
            // The marker has to be cleared by the waiting caller, not by the fetch itself: the fetch
            // method returns its faulted task before any handler of it runs, so clearing the field from
            // inside would be undone by the assignment that just stored the task.
            lock (_gate)
            {
                if (ReferenceEquals(_refresh, refresh))
                {
                    _refresh = null;
                }
            }

            throw;
        }

        lock (_gate)
        {
            _keys = keys;
            _keysFetchedAt = DateTimeOffset.UtcNow;
            _refresh = null;
        }

        return keys;
    }

    /// <summary>Asks the key source for a fresh pair.</summary>
    private Task<(string ImageKey, string SubKey)> FetchAsync() => _keySource(CancellationToken.None);
}
