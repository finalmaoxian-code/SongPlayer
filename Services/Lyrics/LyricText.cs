using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SongPlayer.Models;

namespace SongPlayer.Services.Lyrics;

/// <summary>
/// Text helpers shared by the identity resolver, the cache and the matcher.
/// </summary>
/// <remarks>
/// Video titles and file names are written for humans, not for a database query. Some of what they
/// carry is noise to be removed ("官方MV", "高清", "1080P"), and some of it is essential information
/// that must survive ("Live", "Cover", "伴奏", "Remix"), because those words say the timeline will not
/// line up with a studio recording. Everything here therefore removes noise but keeps version words.
/// </remarks>
public static class LyricText
{
    /// <summary>Words that only describe the upload, never the song.</summary>
    private static readonly string[] NoiseWords =
    [
        "官方mv", "官方版", "高清", "高清mv", "超清", "蓝光", "完整版", "无损", "音质",
        "mv", "m/v", "official video", "official music video", "official audio", "official",
        "lyric video", "lyrics video", "with lyrics", "hd", "hq", "4k", "1080p", "720p",
        "music video", "audio only", "字幕版", "歌词版", "字幕",
    ];

    /// <summary>Words that identify a performance rather than the studio release.</summary>
    private static readonly (SongVersion Version, string[] Words)[] VersionWords =
    [
        (SongVersion.Instrumental, ["instrumental", "off vocal", "offvocal", "karaoke", "伴奏", "纯音乐", "无人声"]),
        (SongVersion.Karaoke, ["karaoke", "ktv"]),
        (SongVersion.Live, ["live", "现场", "演唱会", "concert", "unplugged"]),
        (SongVersion.Acoustic, ["acoustic", "不插电"]),
        (SongVersion.Cover, ["cover", "翻唱", "covered by", "翻自"]),
        (SongVersion.Remix, ["remix", "rmx", "混音", "dj版", "bootleg"]),
        (SongVersion.SpedUp, ["sped up", "speed up", "spedup", "nightcore", "加速版", "加速"]),
    ];

    /// <summary>Separators a title uses between song name and artist.</summary>
    private static readonly char[] ArtistSeparators = ['-', '–', '—', '|', '·', '／', '/'];

    /// <summary>Bracket pairs a title uses to quote the song name.</summary>
    /// <remarks>
    /// Chinese and Japanese uploads wrap the song name in a quotation bracket and put everything else
    /// around it, which makes the quoted part the single most reliable piece of information a video
    /// title carries. <c>邓丽君《月亮代表我的心》百万豪装录音棚大声听</c> says the song is
    /// <c>月亮代表我的心</c>; treating the whole line as the name asks the database for a song that
    /// does not exist.
    /// </remarks>
    private static readonly (char Open, char Close)[] QuotePairs =
    [
        ('《', '》'),
        ('〈', '〉'),
        ('「', '」'),
        ('『', '』'),
        ('“', '”'),
        ('"', '"'),
    ];

    /// <summary>Longest piece of text before a quoted name that still reads as a performer.</summary>
    /// <remarks>
    /// Anything longer is a sentence about the video rather than a name, and sending it as the artist
    /// would make the exact-match query miss.
    /// </remarks>
    private const int MaximumQuotedArtistLength = 40;

    /// <summary>
    /// Pulls a quoted song name out of a title, plus the performer when the upload put one in front.
    /// </summary>
    /// <param name="title">Video title or file name.</param>
    /// <returns>
    /// The quoted name and a performer hint, or <c>(null, null)</c> when nothing is quoted. The text
    /// after the quoted name is deliberately ignored: uploads use it for descriptions such as
    /// <c>百万豪装录音棚大声听</c>, and the trailing words are never a performer.
    /// </returns>
    public static (string? SongTitle, string? Artist) ExtractQuotedName(string? title)
    {
        string cleaned = Clean(title);
        if (cleaned.Length == 0)
        {
            return (null, null);
        }

        foreach ((char open, char close) in QuotePairs)
        {
            int start = cleaned.IndexOf(open);
            if (start < 0)
            {
                continue;
            }

            int end = cleaned.IndexOf(close, start + 1);
            if (end <= start + 1)
            {
                continue;
            }

            string name = cleaned[(start + 1)..end].Trim();
            if (name.Length < 2 || Comparable(name).Length == 0)
            {
                continue;
            }

            return (name, ReadQuotedArtist(cleaned[..start]));
        }

        return (null, null);
    }

    /// <summary>Reads the text in front of a quoted name as a possible performer.</summary>
    private static string? ReadQuotedArtist(string prefix)
    {
        // Badges come first: "【4K120帧】The Beatles《Yesterday》" has to yield "The Beatles" and not
        // the badge glued to it.
        string remaining = Regex.Replace(prefix, @"^\s*(?:[、,，.。;；:\s]*[【\[\(（《〈「『][^】\]\)）》〉」』]*[】\]\)）》〉」』])+", " ");
        remaining = RemoveNoise(remaining);
        remaining = remaining.Trim(' ', '-', '–', '—', '|', '·', '／', '/', '、', ',', '，', ':', '：');

        if (remaining.Length == 0 || remaining.Length > MaximumQuotedArtistLength)
        {
            return null;
        }

        return Comparable(remaining).Length == 0 ? null : remaining;
    }

    /// <summary>Removes the quoted part of a title and keeps the words around it.</summary>
    /// <remarks>
    /// Used to offer the surrounding words as a fallback name, so an upload whose quoted text is a
    /// label rather than the song still has a second reading to try.
    /// </remarks>
    public static string RemoveQuoted(string? title)
    {
        string cleaned = Clean(title);
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        string remaining = cleaned;
        foreach ((char open, char close) in QuotePairs)
        {
            remaining = Regex.Replace(
                remaining,
                Regex.Escape(open.ToString()) + "[^" + Regex.Escape(close.ToString()) + "]*" + Regex.Escape(close.ToString()),
                " ");
        }

        return Regex.Replace(remaining, @"\s{2,}", " ").Trim(' ', '-', '–', '—', '|', '/', '·', '、', ',', '，');
    }

    /// <summary>
    /// Decodes entities, folds full-width forms to half-width and collapses whitespace.
    /// </summary>
    /// <remarks>
    /// This is the form stored and queried. It is not yet the comparison form: punctuation is
    /// meaningful for display, so it is kept here and only removed by <see cref="Comparable"/>.
    /// </remarks>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string decoded = text;

        // Titles arrive HTML-escaped surprisingly often, and an unescaped "&amp;" poisons matching.
        for (int pass = 0; pass < 2 && decoded.Contains('&'); pass++)
        {
            string next = WebUtility.HtmlDecode(decoded);
            if (next == decoded)
            {
                break;
            }

            decoded = next;
        }

        StringBuilder builder = new(decoded.Length);
        foreach (char character in decoded)
        {
            builder.Append(character switch
            {
                '\u3000' => ' ',
                >= '\uFF01' and <= '\uFF5E' => (char)(character - 0xFEE0),
                _ => character,
            });
        }

        string collapsed = Regex.Replace(builder.ToString(), @"[\s\u00A0]+", " ").Trim();
        return collapsed;
    }

    /// <summary>
    /// Reduces text to what two titles must share to be considered the same song.
    /// </summary>
    /// <remarks>
    /// Case, punctuation, spacing and bracketing are all removed, because "Creep" and "creep (live)"
    /// have to be comparable. Chinese text survives untouched: it has no case and no word boundaries.
    /// </remarks>
    public static string Comparable(string? text)
    {
        string cleaned = Clean(text);
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new(cleaned.Length);
        foreach (char character in cleaned)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Detects the performance version a title describes, and returns the title with the marker removed.
    /// </summary>
    /// <remarks>
    /// Only an explicit marker counts. A title that says nothing is <see cref="SongVersion.Unknown"/>,
    /// which the matcher treats as "compatible with anything" rather than as "studio": guessing studio
    /// would let a live take quietly inherit a studio timeline.
    /// </remarks>
    public static (SongVersion Version, string Title) DetectVersion(string? title)
    {
        string cleaned = Clean(title);
        if (cleaned.Length == 0)
        {
            return (SongVersion.Unknown, string.Empty);
        }

        SongVersion best = SongVersion.Unknown;
        string remaining = cleaned;

        foreach ((SongVersion version, string[] words) in VersionWords)
        {
            foreach (string word in words)
            {
                if (remaining.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    best = best == SongVersion.Unknown ? version : best;
                    remaining = RemoveWord(remaining, word);
                }
            }
        }

        string stripped = Regex.Replace(remaining, @"[\(\[\{【（]\s*[\)\]\}】）]", " ");
        stripped = Regex.Replace(stripped, @"\s{2,}", " ").Trim(' ', '-', '–', '—', '|', '/', '·');
        return (best, stripped.Length == 0 ? cleaned : stripped);
    }

    /// <summary>Removes display noise such as resolution or "official video" wording.</summary>
    public static string RemoveNoise(string? title)
    {
        string cleaned = Clean(title);
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        string remaining = cleaned;
        foreach (string word in NoiseWords)
        {
            remaining = RemoveWord(remaining, word);
        }

        remaining = Regex.Replace(remaining, @"\s{2,}", " ").Trim(' ', '-', '–', '—', '|', '/');
        return remaining.Length == 0 ? cleaned : remaining;
    }

    /// <summary>
    /// Splits a title into song name and artist around an explicit separator.
    /// </summary>
    /// <remarks>
    /// Only used as a hint, never as proof. Both orders occur in the wild ("Artist - Song" and
    /// "Song - Artist"), Chinese titles routinely use other separators, and a hyphen inside a song
    /// name is common, so the split cannot say which side is the performer. The two sides are
    /// therefore returned as "one side, the other side" and the caller keeps both as candidate song
    /// names instead of promoting one of them to the artist slot: guessing wrong there would send a
    /// wrong artist to an exact-match API call, and would also make the name score reject the correct
    /// record, which is worse than sending none.
    /// </remarks>
    public static (string Title, string? Artist) SplitArtistHint(string? title)
    {
        string cleaned = Clean(title);
        if (cleaned.Length == 0)
        {
            return (string.Empty, null);
        }

        int index = cleaned.IndexOfAny(ArtistSeparators);
        if (index <= 0 || index >= cleaned.Length - 1)
        {
            return (cleaned, null);
        }

        // A separator surrounded by spaces, or a long dash, is the conventional form. Without that
        // signal the split is too likely to cut a real song name in half.
        bool spaced = index > 0 && char.IsWhiteSpace(cleaned[index - 1]) && char.IsWhiteSpace(cleaned[index + 1]);
        bool dash = cleaned[index] is '–' or '—';
        if (!spaced && !dash)
        {
            return (cleaned, null);
        }

        string left = cleaned[..index].Trim();
        string right = cleaned[(index + 1)..].Trim();
        if (left.Length == 0 || right.Length == 0)
        {
            return (cleaned, null);
        }

        // Titles are usually "Artist - Song" in uploads and "Song - Artist" on file shares. Preferring
        // the right-hand side would invert half the cases, so no side is preferred; the caller decides
        // whether it has a trusted artist from metadata instead.
        return (right, left);
    }

    /// <summary>
    /// True when the text reads like a video title rather than a song name.
    /// </summary>
    /// <remarks>
    /// This is the signal that decides whether a search keyword may replace a title as the song
    /// name. An upload that advertises resolution, a lyric-video badge, a bracket-wrapped note or a
    /// clickbait exclamation is describing its own video; an upload called plainly <c>Creep</c> is not,
    /// and in that case the user's keyword is more likely to be the artist than the song, so it must
    /// not win. The check is deliberately evidence-based instead of "the keyword is always better":
    /// both inputs are guesses and only one of them gets to be preferred.
    /// </remarks>
    public static bool LooksLikeVideoTitle(string? text)
    {
        string cleaned = Clean(text);
        if (cleaned.Length == 0)
        {
            return false;
        }

        if (RemoveNoise(cleaned) != cleaned)
        {
            return true;
        }

        foreach (char marker in VideoTitleMarkers)
        {
            if (cleaned.Contains(marker))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Punctuation that advertises a video rather than naming a song.</summary>
    private static readonly char[] VideoTitleMarkers =
    [
        '【', '】', '《', '》', '（', '）', '(', ')', '[', ']',
        '！', '!', '？', '?', '｜', '|',
    ];

    /// <summary>True when text is a placeholder rather than a real name.</summary>
    public static bool IsPlaceholder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        string trimmed = text.Trim();
        return trimmed.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("Unknown Album", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("VA", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("—", StringComparison.Ordinal)
               || trimmed.Equals("-", StringComparison.Ordinal);
    }

    /// <summary>Similarity in the range 0–1, robust for both Latin and CJK text.</summary>
    /// <remarks>
    /// Uses character bigrams, which need no word segmentation and therefore works for Chinese,
    /// Japanese and Korean titles as well as English ones. Very short strings fall back to equality
    /// and prefix checks, because one bigram cannot distinguish anything.
    /// </remarks>
    public static double Similarity(string? left, string? right)
    {
        string a = Comparable(left);
        string b = Comparable(right);

        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (a == b)
        {
            return 1;
        }

        if (a.Length <= 2 || b.Length <= 2)
        {
            return a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal) ? 0.8 : 0;
        }

        // Containment is strong evidence: "creep" inside "creepacoustic" is the same song, while the
        // reverse containment of a long title inside a short one is not.
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
        {
            double ratio = (double)Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length);
            return 0.72 + (0.28 * ratio);
        }

        Dictionary<string, int> grams = new(StringComparer.Ordinal);
        for (int i = 0; i < a.Length - 1; i++)
        {
            string gram = a.Substring(i, 2);
            grams[gram] = grams.TryGetValue(gram, out int count) ? count + 1 : 1;
        }

        int overlap = 0;
        for (int i = 0; i < b.Length - 1; i++)
        {
            string gram = b.Substring(i, 2);
            if (grams.TryGetValue(gram, out int count) && count > 0)
            {
                grams[gram] = count - 1;
                overlap++;
            }
        }

        int total = (a.Length - 1) + (b.Length - 1);
        return total == 0 ? 0 : (2.0 * overlap) / total;
    }

    /// <summary>Shortest keyword that may be removed without a word boundary.</summary>
    /// <remarks>
    /// Two-letter noise words ("mv", "hd", "hq", "4k") are common inside real words — "MVP",
    /// "HDMI", "Move On" — so they must never match as a loose substring. Anything this short only
    /// counts when it stands alone or fills a bracket pair.
    /// </remarks>
    private const int MinimumLooseMatchLength = 4;

    /// <summary>Removes one word from text, without cutting a longer real word in half.</summary>
    /// <remarks>
    /// The previous form matched the bare substring, which quietly destroyed titles: "MVP" lost its
    /// "mv", "Officialdom" lost its "official", and "HDMI" lost its "hd". Removing display noise is
    /// only worth doing while the song name survives it, so a match now has to be one of:
    /// <list type="bullet">
    /// <item>a standalone token, bounded by characters that cannot be part of a word, or</item>
    /// <item>the whole content of a bracket pair, e.g. <c>【高清】</c>, or</item>
    /// <item>the whole remaining text.</item>
    /// </list>
    /// CJK needs no word boundaries — it has none — so a longer CJK keyword ("官方mv", "伴奏") keeps
    /// the loose behaviour, while a short one is treated like any other token.
    /// </remarks>
    private static string RemoveWord(string text, string word)
    {
        if (!text.Contains(word, StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        bool needsBoundary = word.Length < MinimumLooseMatchLength || IsLatin(word);

        // Brackets that become empty are removed too, otherwise "Song ()" survives as a title.
        string body = Regex.Escape(word);
        const string Open = "[\\(\\[\\{【（]";
        const string Close = "[\\)\\]\\}】）]";

        string pattern;
        if (needsBoundary)
        {
            // Two forms, because the bracket is itself a boundary: "【伴奏】" must match even though the
            // character before it is a letter. A bare token still needs non-word characters on both
            // sides, which is what keeps "mv" out of "MVP".
            pattern = $"{Open}\\s*{body}\\s*{Close}"
                + $"|(?<![\\p{{L}}\\p{{N}}]){body}(?![\\p{{L}}\\p{{N}}])";
        }
        else
        {
            pattern = $"{Open}?\\s*{body}\\s*{Close}?";
        }

        string result = Regex.Replace(text, pattern, " ", RegexOptions.IgnoreCase);
        return Regex.Replace(result, @"\s{2,}", " ").Trim();
    }

    /// <summary>True when the text is written with Latin letters rather than CJK ideographs.</summary>
    private static bool IsLatin(string text)
    {
        foreach (char character in text)
        {
            if (character >= '\u4E00' && character <= '\u9FFF')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Renders a duration for messages without locale surprises.</summary>
    public static string Clock(TimeSpan? duration) =>
        duration is { } value && value > TimeSpan.Zero
            ? value.ToString(@"m\:ss", CultureInfo.InvariantCulture)
            : "—";
}
