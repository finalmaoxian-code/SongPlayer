using System.Globalization;
using SongPlayer.Models;

namespace SongPlayer.Services.Lyrics;

/// <summary>
/// A parsed LRC body: timed lines plus whatever global offset the file itself declared.
/// </summary>
/// <param name="Lines">Timed lines, sorted, de-duplicated and stripped of empty entries.</param>
/// <param name="FileOffset">Offset the file asked for through an <c>[offset:…]</c> tag.</param>
public sealed record LrcDocument(IReadOnlyList<LyricLine> Lines, TimeSpan FileOffset)
{
    /// <summary>A document with no timed lines.</summary>
    public static LrcDocument Empty { get; } = new([], TimeSpan.Zero);

    /// <summary>True when there is nothing to show.</summary>
    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>
/// Turns an LRC body into timed lines.
/// </summary>
/// <remarks>
/// Real-world LRC files are hand-edited and inconsistent, so the parser is forgiving in one direction
/// and strict in another: malformed timestamps are skipped rather than aborting the song, but a
/// timestamp is only accepted when it can be expressed without overflow. Supported shapes are
/// <c>[mm:ss]</c>, <c>[mm:ss.x]</c>, <c>[mm:ss.xx]</c>, <c>[mm:ss.xxx]</c> and the
/// <c>[mm:ss:xx]</c> variant some editors emit, with any number of timestamps on one line so a chorus
/// can be written once and tagged repeatedly. Metadata tags are ignored except <c>[offset:…]</c>,
/// whose value is in milliseconds and is added to every line (the convention this app documents).
/// </remarks>
public static class LrcParser
{
    /// <summary>Guard against pathological files: ten hours of timestamps is not a song.</summary>
    private const int MaxMinutes = 600;

    /// <summary>Guard against a file that is really a dump of thousands of tagged lines.</summary>
    private const int MaxLines = 4000;

    /// <summary>Separator used when two texts share one timestamp, such as a translation pair.</summary>
    private const string PairSeparator = "\n";

    /// <summary>
    /// Parses an LRC body. Never throws for bad input; unreadable content simply yields fewer lines.
    /// </summary>
    public static LrcDocument Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return LrcDocument.Empty;
        }

        // A byte-order mark survives File.ReadAllText when the file was written by an old editor, and
        // it would otherwise glue itself to the first timestamp.
        string body = text.TrimStart('\uFEFF');

        List<(TimeSpan Time, string Text)> entries = [];
        TimeSpan fileOffset = TimeSpan.Zero;

        foreach (string rawLine in body.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            List<TimeSpan> stamps = [];
            int cursor = 0;

            // Consume the run of leading bracket tags: timestamps are collected, metadata is read or
            // dropped. Anything after the last bracket is the lyric text for this line.
            while (cursor < line.Length && line[cursor] == '[')
            {
                int close = line.IndexOf(']', cursor + 1);
                if (close < 0)
                {
                    break;
                }

                string inner = line[(cursor + 1)..close].Trim();

                if (TryParseTimestamp(inner, out TimeSpan stamp))
                {
                    stamps.Add(stamp);
                }
                else if (TryReadTag(inner, out string key, out string value)
                         && key.Equals("offset", StringComparison.OrdinalIgnoreCase)
                         && TryParseOffset(value, out TimeSpan declared))
                {
                    fileOffset = declared;
                }

                cursor = close + 1;
            }

            string content = line[cursor..].Trim();
            if (stamps.Count == 0 || content.Length == 0)
            {
                continue;
            }

            foreach (TimeSpan stamp in stamps)
            {
                entries.Add((stamp, content));
            }
        }

        if (entries.Count == 0)
        {
            return new LrcDocument([], fileOffset);
        }

        // The offset tag moves the whole timeline; clamping at zero keeps a negative offset from
        // producing lines that can never be reached.
        List<(TimeSpan Time, string Text)> shifted = entries
            .Select(entry => (Time: Shift(entry.Time, fileOffset), entry.Text))
            .OrderBy(entry => entry.Time)
            .ToList();

        // Two lines may legitimately share a timestamp: an original and its translation, or a chorus
        // written as two rows. Merging keeps both visible; dropping either loses content the file
        // deliberately carried.
        List<LyricLine> lines = [];
        TimeSpan? currentTime = null;
        List<string> currentTexts = [];

        foreach ((TimeSpan time, string content) in shifted)
        {
            if (currentTime is { } time0 && time != time0)
            {
                lines.Add(BuildLine(time0, currentTexts));
                currentTexts = [];
            }

            currentTime = time;
            if (!currentTexts.Contains(content, StringComparer.Ordinal))
            {
                currentTexts.Add(content);
            }

            if (lines.Count >= MaxLines)
            {
                break;
            }
        }

        if (currentTime is { } lastTime && lines.Count < MaxLines)
        {
            lines.Add(BuildLine(lastTime, currentTexts));
        }

        return new LrcDocument(lines, fileOffset);
    }

    /// <summary>Applies the file offset, refusing to produce negative times.</summary>
    private static TimeSpan Shift(TimeSpan time, TimeSpan offset)
    {
        TimeSpan shifted = time + offset;
        return shifted < TimeSpan.Zero ? TimeSpan.Zero : shifted;
    }

    /// <summary>Joins the texts that share one timestamp into a single display line.</summary>
    private static LyricLine BuildLine(TimeSpan time, List<string> texts) =>
        new(time, string.Join(PairSeparator, texts));

    /// <summary>
    /// Parses the inside of a timestamp bracket.
    /// </summary>
    /// <remarks>
    /// Accepts one or two digits of seconds, and a fraction introduced by either a dot or a colon.
    /// Fractions of one or two digits are scaled to milliseconds so <c>[00:12.5]</c> means half a
    /// second rather than five milliseconds.
    /// </remarks>
    private static bool TryParseTimestamp(string inner, out TimeSpan time)
    {
        time = TimeSpan.Zero;

        int firstColon = inner.IndexOf(':');
        if (firstColon <= 0 || firstColon == inner.Length - 1)
        {
            return false;
        }

        string minutesText = inner[..firstColon];
        string rest = inner[(firstColon + 1)..];

        int fractionSeparator = rest.IndexOfAny(['.', ':']);
        string secondsText = fractionSeparator < 0 ? rest : rest[..fractionSeparator];
        string fractionText = fractionSeparator < 0 ? string.Empty : rest[(fractionSeparator + 1)..];

        if (!int.TryParse(minutesText, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
            || !int.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
        {
            return false;
        }

        if (minutes < 0 || minutes > MaxMinutes || seconds is < 0 or > 59)
        {
            return false;
        }

        int milliseconds = 0;
        if (fractionText.Length > 0)
        {
            if (!int.TryParse(fractionText, NumberStyles.None, CultureInfo.InvariantCulture, out int fraction))
            {
                return false;
            }

            milliseconds = fractionText.Length switch
            {
                1 => fraction * 100,
                2 => fraction * 10,
                _ => fractionText.Length > 3 ? fraction / (int)Math.Pow(10, fractionText.Length - 3) : fraction,
            };
        }

        long totalMs = ((long)minutes * 60 * 1000) + (seconds * 1000L) + milliseconds;
        if (totalMs < 0 || totalMs > MaxMinutes * 60_000L)
        {
            return false;
        }

        time = TimeSpan.FromMilliseconds(totalMs);
        return true;
    }

    /// <summary>Splits <c>key:value</c> metadata such as <c>ar</c>, <c>ti</c> or <c>offset</c>.</summary>
    private static bool TryReadTag(string inner, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        int colon = inner.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        key = inner[..colon].Trim();
        value = inner[(colon + 1)..].Trim();
        return key.Length > 0;
    }

    /// <summary>
    /// Parses an <c>[offset:…]</c> value, which is milliseconds and may carry an explicit sign.
    /// </summary>
    /// <remarks>
    /// A positive value shifts every line later. That direction is the one this app documents for its
    /// own user-adjustable offset too, so the two never disagree.
    /// </remarks>
    private static bool TryParseOffset(string value, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;

        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double milliseconds))
        {
            return false;
        }

        // A file claiming an absurd offset is broken; ignoring it is better than shifting the whole
        // song out of existence.
        if (double.IsNaN(milliseconds) || Math.Abs(milliseconds) > TimeSpan.FromMinutes(10).TotalMilliseconds)
        {
            return false;
        }

        offset = TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }
}
