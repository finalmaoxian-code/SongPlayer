using System;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SongPlayer.Services.Audio;

/// <summary>
/// Captures the audio SongPlayer itself is playing, so the visualiser can react to the actual
/// signal instead of to a simulated one.
/// </summary>
/// <remarks>
/// <para>
/// LibVLC owns the audio path and does not expose its decoded samples, so the only way to see
/// the real waveform without writing an audio renderer is to ask Windows for a copy. A
/// *process loopback* capture does exactly that: WASAPI hands back the mix that the given
/// process rendered, which means other applications cannot leak into the visualiser and the
/// stream keeps working for local files and Bilibili streams alike.
/// </para>
/// <para>
/// Process loopback is an asynchronous activation on Windows, so the constructor cannot do the
/// work: <see cref="Start"/> builds the recorder on a background thread and every failure ends
/// up in <see cref="Faulted"/>. A missing capture device must never be a reason for the music to
/// stop, so nothing here throws at the caller.
/// </para>
/// </remarks>
public sealed class WasapiAudioTap : IDisposable
{
    /// <summary>Capture buffer length in milliseconds. 100 ms is two visual frames.</summary>
    private const int BufferMilliseconds = 100;

    private readonly object _gate = new();

    private WasapiRecorder? _recorder;
    private bool _captureStarted;
    private bool _disposed;
    private long _lastFrameTicks;

    /// <summary>
    /// Creates a tap for one analyser.
    /// </summary>
    /// <example>
    /// <code>
    /// var tap = new WasapiAudioTap(new SpectrumAnalyzer(44100, 2));
    /// tap.Start();
    /// </code>
    /// </example>
    /// <param name="analyzer">
    /// Analyser to feed. It must be built for the sample rate the capture device reports, so the
    /// tap replaces it if the device disagrees with what was assumed.
    /// </param>
    public WasapiAudioTap(SpectrumAnalyzer analyzer)
    {
        Analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
    }

    /// <summary>
    /// Raised once if the capture could not be created, with a message for the log rather than
    /// for the user.
    /// </summary>
    public event EventHandler<string>? Faulted;

    /// <summary>
    /// True once the device is delivering buffers, which also means <see cref="Analyzer"/> is
    /// final and safe to render from.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Analyser fed by this tap. Replaced when the device's format needs a different sample
    /// rate, so always read it from here rather than keeping the instance that was passed in.
    /// </summary>
    public SpectrumAnalyzer Analyzer { get; private set; }

    /// <summary>
    /// Starts capturing this process. Safe to call when it is already running.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _captureStarted)
            {
                return;
            }

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                Faulted?.Invoke(this, "Per-process audio capture needs Windows 10 version 2004 or newer.");
                return;
            }

            _captureStarted = true;
            _lastFrameTicks = Environment.TickCount64;

            _ = StartAsync();
        }
    }

    /// <summary>
    /// Stops delivering data. The recorder is kept so a restart is cheap; only
    /// <see cref="Dispose"/> tears it down.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_recorder is null || !IsRunning)
            {
                return;
            }

            IsRunning = false;

            // Clearing the flag is what makes the stop reversible: StartRecording on an existing
            // recorder is cheap, while rebuilding it renegotiates the WASAPI stream. Leaving the
            // flag set would turn the first pause into a permanent end of capture.
            _captureStarted = false;

            try
            {
                _recorder.StopRecording();
            }
            catch (InvalidOperationException)
            {
                // The device was already gone; there is nothing left to stop.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            IsRunning = false;
        }

        try
        {
            _recorder?.StopRecording();
        }
        catch (InvalidOperationException)
        {
            // Already stopped.
        }

        _recorder?.Dispose();
        _recorder = null;
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private async System.Threading.Tasks.Task StartAsync()
    {
        try
        {
            // Reuse the device when this is a resume rather than a fresh start.
            if (_recorder is not null && !IsRunning)
            {
                _recorder.StartRecording();
                IsRunning = true;
                return;
            }

            var recorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithBufferLength(BufferMilliseconds)
                .BuildAsync()
                .ConfigureAwait(false);

            lock (_gate)
            {
                if (_disposed)
                {
                    recorder.Dispose();
                    return;
                }

                Analyzer = new SpectrumAnalyzer(recorder.WaveFormat.SampleRate, recorder.WaveFormat.Channels);
                _recorder = recorder;
            }

            recorder.DataAvailable += OnDataAvailable;
            recorder.RecordingStopped += OnRecordingStopped;
            recorder.StartRecording();

            IsRunning = true;

        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Faulted?.Invoke(this, exception.Message);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        IsRunning = false;

        if (e.Exception is not null)
        {
            Faulted?.Invoke(this, e.Exception.Message);
        }
    }

    /// <summary>
    /// Handles one captured buffer.
    /// </summary>
    /// <remarks>
    /// The callback arrives on a WASAPI thread, so nothing here may touch WPF. The analyser is
    /// pure arithmetic over arrays, which is why it can run at this point without a lock: the
    /// render side only ever reads the published float array.
    /// </remarks>
    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var now = Environment.TickCount64;
        var elapsed = (now - _lastFrameTicks) / 1000d;
        _lastFrameTicks = now;

        if (flags.HasFlag(AudioClientBufferFlags.Silent))
        {
            Analyzer.Decay(Math.Max(elapsed, 0));
            return;
        }

        // The recorder hands back 32-bit float samples in the device's channel layout.
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);

        Analyzer.Process(samples, samples.Length, elapsed);
    }
}
