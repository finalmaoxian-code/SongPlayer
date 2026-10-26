namespace SongPlayer.Models;

/// <summary>
/// Formatting of durations for the shell.
/// </summary>
/// <remarks>
/// One home for the clock format: the player bar, the library rows and the tooltips all show the
/// same number, and a second ternary elsewhere would eventually disagree about the hour separator.
/// </remarks>
public static class TimeSpanFormatting
{
    /// <summary>
    /// Formats a duration as <c>m:ss</c>, or <c>h:mm:ss</c> for tracks longer than an hour.
    /// </summary>
    /// <param name="value">Duration to format; negative values are clamped to zero.</param>
    /// <returns>The formatted clock text.</returns>
    public static string ToClock(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}"
            : $"{value.Minutes:D2}:{value.Seconds:D2}";
    }
}
