using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace SongPlayer.Services.Bilibili;

/// <summary>
/// Reads the real duration of a Bilibili DASH audio segment out of its MP4 boxes.
/// </summary>
/// <remarks>
/// A Bilibili audio stream is a fragmented MP4 whose <c>mvhd</c> declares
/// <c>duration = 0</c> — it is written for live-ish delivery, so the header cannot know the
/// length. The truth is in the <c>sidx</c> box: it lists every subsegment with its duration in
/// the media timescale. Both boxes live in the first couple of kilobytes, so one ranged request
/// answers the question without downloading the track.
/// <para>
/// Without this, an online track has a length of zero, which makes the seek slider span nothing
/// and the time label read <c>00:00</c> for a track that is actually minutes long.
/// </para>
/// </remarks>
public static class DashSegmentProbe
{
    /// <summary>
    /// Bytes requested from the start of the segment. Covers <c>ftyp</c>, <c>moov</c> and
    /// <c>sidx</c> for every Bilibili segment seen so far, while staying small enough to be free.
    /// </summary>
    private const int ProbeBytes = 16 * 1024;

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>
    /// Determines the duration of the media at <paramref name="audioUri"/>.
    /// </summary>
    /// <param name="audioUri">Temporary CDN URL of the audio segment.</param>
    /// <param name="referer">Referer the CDN expects.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The duration, or <see langword="null"/> when it could not be determined.</returns>
    /// <remarks>
    /// Returns <see langword="null"/> instead of throwing: a duration that cannot be read is a
    /// cosmetic loss (the slider stays at zero length), not a reason to refuse playback.
    /// </remarks>
    public static async Task<TimeSpan?> TryReadDurationAsync(
        Uri audioUri,
        string referer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioUri);

        byte[]? head;

        try
        {
            head = await ReadHeadAsync(audioUri, referer, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return null;
        }

        if (head is null)
        {
            return null;
        }

        // A fragmented segment has a sidx; a plain MP4 carries the length in mvhd instead.
        TimeSpan? fromSidx = ReadSidxDuration(head);

        if (fromSidx is { } sidxDuration && sidxDuration > TimeSpan.Zero)
        {
            return sidxDuration;
        }

        TimeSpan? fromHeader = ReadMovieHeaderDuration(head);

        return fromHeader is { } headerDuration && headerDuration > TimeSpan.Zero
            ? headerDuration
            : null;
    }

    /// <summary>
    /// Downloads the first <see cref="ProbeBytes"/> of the segment.
    /// </summary>
    /// <param name="audioUri">Segment URL.</param>
    /// <param name="referer">Referer the CDN expects.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The bytes read, or <see langword="null"/> when the server refused the range.</returns>
    private static async Task<byte[]?> ReadHeadAsync(
        Uri audioUri,
        string referer,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, audioUri);

        request.Headers.Range = new RangeHeaderValue(0, ProbeBytes - 1);
        request.Headers.Referrer = new Uri(referer);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        using HttpResponseMessage response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Sums the subsegment durations of the first <c>sidx</c> box.
    /// </summary>
    /// <param name="head">Bytes from the start of the segment.</param>
    /// <returns>The duration, or <see langword="null"/> when there is no usable <c>sidx</c>.</returns>
    private static TimeSpan? ReadSidxDuration(byte[] head)
    {
        foreach ((int payloadStart, int payloadEnd) in EnumerateBoxes(head, "sidx"))
        {
            int cursor = payloadStart;

            if (payloadEnd - cursor < 12)
            {
                continue;
            }

            byte version = head[cursor];

            cursor += 4; // version and flags
            cursor += 4; // reference_ID

            uint timescale = ReadUInt32(head, cursor);
            cursor += 4;

            cursor += version == 0 ? 8 : 16; // earliest_presentation_time + first_offset

            if (payloadEnd - cursor < 4)
            {
                continue;
            }

            cursor += 2; // reserved

            ushort referenceCount = ReadUInt16(head, cursor);
            cursor += 2;

            if (timescale == 0)
            {
                continue;
            }

            ulong totalTicks = 0;
            int available = (payloadEnd - cursor) / 12;

            for (int i = 0; i < Math.Min(referenceCount, available); i++)
            {
                // Each reference is referenced_size (4), subsegment_duration (4), SAP flags (4).
                totalTicks += ReadUInt32(head, cursor + 4);
                cursor += 12;
            }

            if (totalTicks > 0)
            {
                return TimeSpan.FromSeconds((double)totalTicks / timescale);
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the duration declared by <c>moov/mvhd</c>, used for non-fragmented files.
    /// </summary>
    /// <param name="head">Bytes from the start of the segment.</param>
    /// <returns>The duration, or <see langword="null"/> when the header declares none.</returns>
    private static TimeSpan? ReadMovieHeaderDuration(byte[] head)
    {
        foreach ((int payloadStart, int payloadEnd) in EnumerateBoxes(head, "mvhd"))
        {
            int cursor = payloadStart;

            if (payloadEnd - cursor < 20)
            {
                continue;
            }

            byte version = head[cursor];

            cursor += 4; // version and flags

            if (version == 1)
            {
                cursor += 16; // creation and modification times
            }
            else
            {
                cursor += 8;
            }

            if (payloadEnd - cursor < 8)
            {
                continue;
            }

            uint timescale = ReadUInt32(head, cursor);
            cursor += 4;

            if (timescale == 0)
            {
                continue;
            }

            ulong duration = version == 1
                ? (ulong)(((long)ReadUInt32(head, cursor) << 32) | ReadUInt32(head, cursor + 4))
                : ReadUInt32(head, cursor);

            if (duration > 0)
            {
                return TimeSpan.FromSeconds((double)duration / timescale);
            }
        }

        return null;
    }

    /// <summary>
    /// Walks the top-level box chain looking for <paramref name="type"/>.
    /// </summary>
    /// <param name="buffer">Bytes from the start of the segment.</param>
    /// <param name="type">Four-character box type to find.</param>
    /// <returns>Payload ranges of every matching box.</returns>
    /// <remarks>
    /// Interleaved fragment boxes are skipped by their declared size, so a <c>moof</c> that happens
    /// to contain the searched-for characters cannot be mistaken for a real match.
    /// </remarks>
    private static IEnumerable<(int Start, int End)> EnumerateBoxes(byte[] buffer, string type)
    {
        int cursor = 0;

        while (cursor + 8 <= buffer.Length)
        {
            long size = ReadUInt32(buffer, cursor);

            int headerSize = 8;

            if (size == 1)
            {
                if (cursor + 16 > buffer.Length)
                {
                    yield break;
                }

                size = (long)(((ulong)ReadUInt32(buffer, cursor + 8) << 32)
                    | ReadUInt32(buffer, cursor + 12));
                headerSize = 16;
            }
            else if (size == 0)
            {
                // Box runs to the end of the file.
                size = buffer.Length - cursor;
            }

            if (size < headerSize)
            {
                yield break;
            }

            string boxType = System.Text.Encoding.ASCII.GetString(buffer, cursor + 4, 4);
            int payloadStart = cursor + headerSize;
            int payloadEnd = (int)Math.Min(cursor + size, buffer.Length);

            if (string.Equals(boxType, type, StringComparison.Ordinal))
            {
                yield return (payloadStart, payloadEnd);
            }

            cursor += (int)size;
        }
    }

    private static uint ReadUInt32(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24)
        | ((uint)buffer[offset + 1] << 16)
        | ((uint)buffer[offset + 2] << 8)
        | buffer[offset + 3];

    private static ushort ReadUInt16(byte[] buffer, int offset) =>
        (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
}
