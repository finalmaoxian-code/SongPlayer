using System;

namespace SongPlayer.Services.Audio;

/// <summary>
/// The bars the visualiser actually draws.
/// </summary>
/// <remarks>
/// <para>
/// The analyser is owned by the capture thread. This type is the opposite: only the UI clock
/// calls <see cref="Advance"/>, and it never reaches back into the analyser. A fresh frame
/// replaces the drawn heights; anything older than <see cref="FreshWindowSeconds"/>, or a frame
/// the caller has decided is not live, falls toward zero at a rate of its own. That is what
/// stops a dead callback from pinning the last chord on screen forever.
/// </para>
/// <para>
/// The idle phase moves only while the bars are gone, and only by the time the caller measured.
/// A render loop that merely invalidates does not reach this method, so it cannot leave the
/// wave standing still.
/// </para>
/// </remarks>
internal sealed class SpectrumDisplay
{
    /// <summary>Largest step applied in one call, so a stalled clock cannot skip the fade.</summary>
    public const double MaxDtSeconds = 0.1;

    /// <summary>How long a frame stays "live" before the bars start falling on their own.</summary>
    public const double FreshWindowSeconds = 0.45;

    /// <summary>How fast a stale bar falls, in full-scale units per second.</summary>
    public const double FallPerSecond = 1.6;

    /// <summary>How fast the idle wave travels, in radians per second.</summary>
    public const double IdleRadiansPerSecond = 1.1;

    private const float IdleThreshold = 0.02f;

    private readonly float[] _levels = new float[SpectrumAnalyzer.BandCount];

    /// <summary>Creates a display that starts on the idle wave.</summary>
    public SpectrumDisplay()
    {
        ShowIdle = true;
    }

    /// <summary>Bar heights currently drawn, bass to treble.</summary>
    public ReadOnlySpan<float> Levels => _levels;

    /// <summary>Overall loudness used for the seam glow.</summary>
    public double Pulse { get; private set; }

    /// <summary>Phase of the idle wave, in radians.</summary>
    public double IdlePhase { get; private set; }

    /// <summary>True when the bars have fallen far enough that the idle wave should be drawn.</summary>
    public bool ShowIdle { get; private set; }

    /// <summary>
    /// Moves the drawn bars by one measured step.
    /// </summary>
    /// <param name="frame">Latest published spectrum. Ignored when <paramref name="captureFresh"/> is false.</param>
    /// <param name="captureFresh">
    /// True only when <paramref name="frame"/> came from a capture that is still delivering
    /// buffers. Silence is fresh; a stopped or stalled callback is not.
    /// </param>
    /// <param name="dtSeconds">Seconds since the previous step. Negative and huge values are clamped.</param>
    public void Advance(SpectrumFrame frame, bool captureFresh, double dtSeconds)
    {
        var dt = double.IsFinite(dtSeconds) ? Math.Clamp(dtSeconds, 0, MaxDtSeconds) : 0;

        if (captureFresh)
        {
            for (var i = 0; i < SpectrumAnalyzer.BandCount; i++)
            {
                _levels[i] = frame.Level(i);
            }

            Pulse = Math.Clamp(frame.PulseLevel, 0, 1);
        }
        else
        {
            var fall = (float)(FallPerSecond * dt);

            for (var i = 0; i < SpectrumAnalyzer.BandCount; i++)
            {
                _levels[i] = Math.Max(0, _levels[i] - fall);
            }

            Pulse = Math.Max(0, Pulse - (fall * 1.5));
        }

        var peak = 0f;

        for (var i = 0; i < SpectrumAnalyzer.BandCount; i++)
        {
            if (_levels[i] > peak)
            {
                peak = _levels[i];
            }
        }

        ShowIdle = peak < IdleThreshold;

        if (ShowIdle)
        {
            IdlePhase += dt * IdleRadiansPerSecond;
        }
    }

    /// <summary>Copies the drawn heights so a test can inspect them without sharing the buffer.</summary>
    public float[] SnapshotLevels()
    {
        var copy = new float[SpectrumAnalyzer.BandCount];
        _levels.CopyTo(copy, 0);
        return copy;
    }
}
