namespace SongPlayer.Services.Bilibili;

/// <summary>
/// Raised when a Bilibili video id cannot be turned into a playable audio URL.
/// </summary>
/// <remarks>
/// Carries a message written for the user: the caller's job is to display it, not to interpret an
/// inner exception or an HTTP status.
/// </remarks>
public sealed class BilibiliSourceException : Exception
{
    /// <summary>
    /// Creates the exception with a user-facing message.
    /// </summary>
    /// <param name="message">Reason to show.</param>
    public BilibiliSourceException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates the exception with a user-facing message and the underlying cause.
    /// </summary>
    /// <param name="message">Reason to show.</param>
    /// <param name="innerException">Original failure, kept for diagnostics.</param>
    public BilibiliSourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
