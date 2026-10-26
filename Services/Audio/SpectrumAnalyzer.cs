using System;
using System.Linq;
using NAudio.Dsp;

namespace SongPlayer.Services.Audio;

/// <summary>
/// Turns raw audio buffers into normalised frequency band levels for the Now Playing
/// visualiser.
/// </summary>
/// <remarks>
/// <para>
/// The analyser is deliberately dumb about the graph: it holds no devices, no timers and no
/// state that scoped to one track. It receives mono samples, keeps a sliding window, and answers
/// <see cref="BandLevels"/> with values in 0..1 that a view can paint.
/// </para>
/// <para>
/// Two decisions shape everything else. First, levels are expressed relative to a *running*
/// noise floor rather than to absolute sample magnitudes, because the loopback stream carries
/// whatever the machine is playing - a quiet acoustic track and a loud master must both use the
/// full height of the bars, and a silent room must sit at zero even though digital silence still
/// produces tiny non-zero FFT bins. Second, the transform runs on a downsampled band of the
/// spectrum: 96 bins cover 40 Hz to ~9 kHz, which is where music lives, and the high bins that
/// only carry hiss are dropped instead of being amplified.
/// </para>
/// </remarks>
public sealed class SpectrumAnalyzer
{
    /// <summary>Number of bars the visualiser draws.</summary>
    public const int BandCount = 32;

    /// <summary>FFT window size. 2048 samples at 44.1 kHz is ~46 ms, one visual frame.</summary>
    private const int FftSize = 2048;

    /// <summary>Lowest frequency the first bar represents, in Hz.</summary>
    private const double MinFrequency = 40;

    /// <summary>
    /// Highest frequency the last bar represents, in Hz. Everything above is mostly cymbal
    /// wash and codec noise, so it would make every bar twitch at once.
    /// </summary>
    private const double MaxFrequency = 9000;

    /// <summary>How fast a bar falls when the music stops, per second (0..1 scale).</summary>
    private const double FallPerSecond = 1.6;

    /// <summary>
    /// Minimum height of the noise floor in dB. Without a floor the first silent buffer would
    /// normalise every subsequent one against zero and paint a full spectrum out of hiss.
    /// </summary>
    private const double MinFloorDb = -70;

    /// <summary>
    /// How fast the noise floor is allowed to climb, in dB per second.
    /// </summary>
    /// <remarks>
    /// This used to be a proportional follow, which was wrong in a way that took a screenshot to
    /// see: for a band that holds a steady tone the floor climbed onto the signal and normalised
    /// it to nothing. A floor has to remember the quiet, so it drops to any new minimum at once
    /// and creeps back up over tens of seconds.
    /// </remarks>
    private const double FloorRiseDbPerSecond = 2.5;

    private readonly Complex[] _fftBuffer = new Complex[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _monoSamples = new float[FftSize];
    private readonly double[] _timeline = new double[BandCount];
    private readonly double[] _peaks = new double[BandCount];
    private readonly int[] _bandStart = new int[BandCount];
    private readonly int[] _bandEnd = new int[BandCount];
    private readonly float[] _levels = new float[BandCount];
    private readonly double[] _levelValues = new double[BandCount];
    private readonly double[] _spread = new double[BandCount];
    private readonly double[] _noiseFloorDb = new double[BandCount];

    private int _sampleCount;
    private int _writeIndex;
    private const double Smoothing = 0.35;
    /// <summary>
    /// Dynamic range the bars cover, in dB above the noise floor. 45 dB is roughly the span
    /// between the quietest and loudest thing in a mastered track, so a bar reaches full height
    /// at a real peak instead of needing headroom it will never see.
    /// </summary>
    private const double RangeDb = 45;

    /// <summary>
    /// Creates an analyser for a stream with the given sample rate and channel count.
    /// </summary>
    /// <param name="sampleRate">Source sample rate, for example 44100 or 48000.</param>
    /// <param name="channels">Number of interleaved channels in the incoming buffers.</param>
    public SpectrumAnalyzer(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        SampleRate = sampleRate;
        Channels = channels;

        // A Hann window trades a little frequency resolution for much less spectral leakage,
        // which is what stops a single loud bass note from lighting the upper bars too.
        for (var i = 0; i < FftSize; i++)
        {
            _window[i] = (float)FastFourierTransform.HannWindow(i, FftSize);
        }

        BuildBands();

        for (var band = 0; band < BandCount; band++)
        {
            _noiseFloorDb[band] = -55;
        }
    }

    /// <summary>Sample rate of the stream this analyser was built for.</summary>
    public int SampleRate { get; }

    /// <summary>Channel count of the stream this analyser was built for.</summary>
    public int Channels { get; }

    /// <summary>
    /// Latest band levels, each in 0..1, ordered from bass to treble. Index 0 is ~40 Hz.
    /// </summary>
    public ReadOnlySpan<float> BandLevels => _levels;

    /// <summary>
    /// Per-bar phase offsets so the bars do not pulse in lockstep; a visualiser that breathes
    /// as one block reads as a progress animation, not as music.
    /// </summary>
    public ReadOnlySpan<double> BarPhases => _timeline;

    /// <summary>
    /// Height in 0..1 of the "how loud is it overall" pulse drawn behind the bars.
    /// </summary>
    public double PulseLevel { get; private set; }

    /// <summary>
    /// True when the incoming audio is quiet enough to be considered silence, which lets the
    /// view switch to an idle animation instead of drawing empty bars.
    /// </summary>
    public bool IsSilent { get; private set; } = true;

    /// <summary>
    /// Feeds one interleaved audio buffer into the analyser and recomputes the band levels.
    /// </summary>
    /// <param name="samples">Interleaved samples as they arrived from the capture device.</param>
    /// <param name="count">Number of valid entries at the start of <paramref name="samples"/>.</param>
    /// <param name="elapsedSeconds">
    /// Time since the previous call, used to make the fall-off independent of frame rate.
    /// </param>
    public void Process(ReadOnlySpan<float> samples, int count, double elapsedSeconds)
    {
        if (count <= 0)
        {
            Decay(Math.Max(elapsedSeconds, 0));
            return;
        }

        // Mix down to mono: a spectrum is a picture of content, not of stereo placement, and
        // averaging is cheaper than analysing both channels separately.
        var frames = count / Channels;

        for (var i = 0; i < frames; i++)
        {
            var sum = 0f;

            for (var channel = 0; channel < Channels; channel++)
            {
                sum += samples[(i * Channels) + channel];
            }

            _monoSamples[_writeIndex] = sum / Channels;
            _writeIndex = (_writeIndex + 1) % FftSize;

            if (_sampleCount < FftSize)
            {
                _sampleCount++;
            }
        }

        if (_sampleCount < FftSize)
        {
            // Not enough history yet: keep the bars where they are rather than flashing.
            Decay(Math.Max(elapsedSeconds, 0));
            return;
        }

        // Age the bars first, then let the fresh frame raise them. The other order would subtract
        // the fall-off from the peak that was just measured, which silently caps every bar at
        // roughly half height no matter how loud the music is.
        DecayPeaks(Math.Max(elapsedSeconds, 0));
        Analyse(Math.Max(elapsedSeconds, 0));
        Publish();
    }

    /// <summary>
    /// Fades the bars towards zero without new audio, for the moment the track ends or the
    /// window is minimised.
    /// </summary>
    /// <param name="elapsedSeconds">Time since the previous update.</param>
    public void Decay(double elapsedSeconds)
    {
        DecayPeaks(elapsedSeconds);
        Publish();
    }

    /// <summary>
    /// Lowers the instantaneous peaks without republishing the smoothed bars.
    /// </summary>
    private void DecayPeaks(double elapsedSeconds)
    {
        var fall = Math.Min(1, FallPerSecond * elapsedSeconds);

        for (var i = 0; i < BandCount; i++)
        {
            _peaks[i] = Math.Max(_peaks[i] - fall, 0);
        }

        PulseLevel = Math.Max(PulseLevel - (fall * 1.5), 0);
        IsSilent = _peaks.Max() <= 0.001;
    }

    /// <summary>
    /// Eases the published bar heights towards the instantaneous peaks.
    /// </summary>
    /// <remarks>
    /// Smoothing happens here rather than inside the analysis so a pause fades out through the
    /// same code path that a track change does, and so a dropped frame cannot make the bars jump.
    /// </remarks>
    private void Publish()
    {
        for (var i = 0; i < BandCount; i++)
        {
            _levelValues[i] += (_peaks[i] - _levelValues[i]) * Smoothing;
            _levels[i] = (float)Math.Clamp(_levelValues[i], 0, 1);
        }
    }

    private void Analyse(double elapsedSeconds)
    {
        var offset = _writeIndex;

        for (var i = 0; i < FftSize; i++)
        {
            var sample = _monoSamples[(offset + i) % FftSize];
            _fftBuffer[i].X = sample * _window[i];
            _fftBuffer[i].Y = 0;
        }

        FastFourierTransform.FFT(true, (int)Math.Log2(FftSize), _fftBuffer);

        var binHz = (double)SampleRate / FftSize;

        for (var band = 0; band < BandCount; band++)
        {
            var peak = 0d;

            // Take the loudest bin in the band instead of the average: an average over a wide
            // treble band would let one narrow sound be flattened by its empty neighbours.
            for (var bin = _bandStart[band]; bin < _bandEnd[band]; bin++)
            {
                var real = _fftBuffer[bin].X;
                var imaginary = _fftBuffer[bin].Y;
                var magnitude = Math.Sqrt((real * real) + (imaginary * imaginary));

                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            var db = 20 * Math.Log10(peak + 1e-9);

            // Each band tracks its own floor. A single shared floor meant the treble band, which
            // is always the quietest, overwrote the bass floor every frame and the low bars never
            // recovered.
            var floor = Math.Min(db, _noiseFloorDb[band] + (FloorRiseDbPerSecond * Math.Max(elapsedSeconds, 0.001)));
            _noiseFloorDb[band] = Math.Max(floor, MinFloorDb);

            var normalised = (db - _noiseFloorDb[band]) / RangeDb;
            normalised = Math.Clamp(normalised, 0, 1);

            // A gentle exponent lifts quiet passages into view without clipping loud ones.
            normalised = Math.Pow(normalised, 0.9);

            _spread[band] = normalised;
        }

        // Bleed a fraction of each band into its neighbours. A pure tone would otherwise light
        // exactly one bar, which reads as a rendering glitch rather than as a spectrum; real
        // music already has energy in neighbouring bands, so the bleed is invisible there.
        for (var band = 0; band < BandCount; band++)
        {
            var own = _spread[band];
            var left = band > 0 ? _spread[band - 1] : 0;
            var right = band < BandCount - 1 ? _spread[band + 1] : 0;
            var blended = Math.Max(own, Math.Max(left, right) * 0.35);
            _peaks[band] = Math.Max(_peaks[band], blended);
        }

        var energy = 0d;

        for (var band = 0; band < BandCount; band++)
        {
            energy += _peaks[band];
        }

        PulseLevel = Math.Max(PulseLevel, Math.Clamp(energy / BandCount, 0, 1));
        IsSilent = false;
    }

    /// <summary>
    /// Maps each bar to a range of FFT bins, logarithmically.
    /// </summary>
    /// <remarks>
    /// Linear bands would hand eleven bars to the top two octaves and one bar to everything
    /// below 300 Hz, which is where most music's energy is. A log scale matches how pitch is
    /// heard and gives the bass the room it deserves.
    /// </remarks>
    private void BuildBands()
    {
        var binHz = (double)SampleRate / FftSize;

        for (var band = 0; band < BandCount; band++)
        {
            var lowRatio = (double)band / BandCount;
            var highRatio = (double)(band + 1) / BandCount;

            var lowHz = MinFrequency * Math.Pow(MaxFrequency / MinFrequency, lowRatio);
            var highHz = MinFrequency * Math.Pow(MaxFrequency / MinFrequency, highRatio);

            var start = Math.Max(1, (int)(lowHz / binHz));
            var end = Math.Max(start + 1, (int)Math.Ceiling(highHz / binHz));

            _bandStart[band] = start;
            _bandEnd[band] = Math.Min(end, FftSize / 2);

            _timeline[band] = band * 0.42;
        }
    }
}
