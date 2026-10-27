using System;
using System.Diagnostics;

namespace SongPlayer.Services.Audio;

/// <summary>
/// One immutable picture of the spectrum: the bar heights, the overall pulse, and when it was
/// taken.
/// </summary>
/// <remarks>
/// <para>
/// The analyser is written on the capture thread and read on the UI thread, so handing the
/// consumer a reference to the analyser's own arrays would be a data race: a render could read
/// half of one frame and half of the next, and the compiler is free to cache values across the
/// calls. A frame copies the numbers once, is never mutated afterwards, and carries a monotonic
/// timestamp so the consumer can tell "quiet" apart from "the audio callback died three seconds
/// ago".
/// </para>
/// <para>
/// <see cref="Timestamp"/> comes from <see cref="Stopwatch.GetTimestamp"/>, never from the wall
/// clock: it cannot jump when the machine's time is adjusted, and it is cheap to read 60 times a
/// second.
/// </para>
/// </remarks>
public sealed class SpectrumFrame
{
    /// <summary>Number of band levels in a frame, matching <see cref="SpectrumAnalyzer.BandCount"/>.</summary>
    public const int BandCount = SpectrumAnalyzer.BandCount;

    private readonly float[] _levels;

    /// <summary>
    /// The frame a fresh tap reports before any audio has arrived.
    /// </summary>
    public static SpectrumFrame Silent { get; } = new(new float[BandCount], 0, 0, 0, true);

    private SpectrumFrame(
        float[] levels,
        long sequence,
        long timestamp,
        double pulseLevel,
        bool isSilent
    )
    {
        _levels = levels;
        Sequence = sequence;
        Timestamp = timestamp;
        PulseLevel = pulseLevel;
        IsSilent = isSilent;
    }

    /// <summary>Band heights, each in 0..1, ordered from bass to treble.</summary>
    public ReadOnlySpan<float> Levels => _levels;

    /// <summary>Monotonically increasing frame number, starting at 1 for the first real frame.</summary>
    public long Sequence { get; }

    /// <summary>
    /// <see cref="Stopwatch.GetTimestamp"/> value captured when this frame was published. Feed it
    /// to <see cref="Stopwatch.GetElapsedTime(long)"/> to measure the frame's age.
    /// </summary>
    public long Timestamp { get; }

    /// <summary>Overall loudness in 0..1, used for the seam glow.</summary>
    public double PulseLevel { get; }

    /// <summary>True when the captured audio was quiet enough to count as silence.</summary>
    public bool IsSilent { get; }

    /// <summary>Level of one band, clamped so a caller never has to range-check the span.</summary>
    /// <param name="band">Band index, 0-based from the bass end.</param>
    public float Level(int band)
    {
        if (band < 0 || band >= BandCount)
        {
            return 0;
        }

        return Math.Clamp(_levels[band], 0f, 1f);
    }

    /// <summary>Copies a freshly published set of levels into a frame.</summary>
    internal static SpectrumFrame Capture(
        float[] levels,
        long sequence,
        double pulseLevel,
        bool isSilent
    )
    {
        var copy = new float[BandCount];
        Array.Copy(levels, copy, BandCount);
        var timestamp = Stopwatch.GetTimestamp();

        // Zero is reserved for "no frame yet" (see Silent). A timestamp of zero only happens
        // at process start, but treating it as missing would freeze the idle wave.
        if (timestamp == 0)
        {
            timestamp = 1;
        }

        return new SpectrumFrame(copy, sequence, timestamp, pulseLevel, isSilent);
    }
}
