namespace SongPlayer.Services.Bilibili;

/// <summary>
/// Shape checks for Bilibili video ids.
/// </summary>
/// <remarks>
/// A separate type rather than a private helper on the bridge client, because two very different
/// callers need the same rule: the client rejects a malformed id before spending a round trip, and
/// the search result mapper drops rows the search endpoint occasionally returns with an empty or
/// truncated id. One definition keeps those two from disagreeing.
/// </remarks>
public static class Bvid
{
    /// <summary>
    /// Length of a Bilibili video id, including the <c>BV</c> prefix.
    /// </summary>
    public const int Length = 12;

    /// <summary>
    /// Checks whether <paramref name="value"/> is <c>BV</c> followed by ten alphanumerics.
    /// </summary>
    /// <param name="value">Candidate id, usually pasted or read from a search result.</param>
    /// <returns>True when the id has the shape the bridge accepts.</returns>
    /// <remarks>
    /// Deliberately case-sensitive on the prefix: the bridge validates the same way, and accepting
    /// <c>bv…</c> here would only move the rejection to the far side of a network call.
    /// </remarks>
    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != Length)
        {
            return false;
        }

        if (!value.StartsWith("BV", StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = 2; i < value.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Extracts the id from a stream key such as <c>bilibili:BV1GJ411x7h7</c>.
    /// </summary>
    /// <param name="streamKey">Stable online identity used by the library.</param>
    /// <returns>The trailing id, or <see langword="null"/> when there is not one.</returns>
    public static string? FromStreamKey(string? streamKey)
    {
        if (string.IsNullOrWhiteSpace(streamKey))
        {
            return null;
        }

        int separator = streamKey.LastIndexOf(':');

        if (separator < 0 || separator == streamKey.Length - 1)
        {
            return null;
        }

        string candidate = streamKey[(separator + 1)..];

        return IsValid(candidate) ? candidate : null;
    }
}
