using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SongPlayer.Services.Audio;

/// <summary>
/// The slice of a WASAPI recorder that <see cref="WasapiAudioTap"/> actually uses.
/// </summary>
/// <remarks>
/// The tap's hard part is not talking to WASAPI, it is staying correct when start and stop race
/// with an asynchronous activation. That logic is worth testing on a machine with no sound card
/// and no audio session, so the device sits behind this interface and the tests drive a fake.
/// Keeping the surface this small is deliberate: every extra member is one more thing a fake has
/// to imitate faithfully.
/// </remarks>
internal interface IAudioCaptureRecorder : IDisposable
{
    /// <summary>Format the device delivers, which decides the analyser's sample rate.</summary>
    WaveFormat WaveFormat { get; }

    /// <summary>Raised for every captured buffer, on a device thread.</summary>
    event CaptureDataAvailableHandler? DataAvailable;

    /// <summary>Raised when the device stops on its own, including on error.</summary>
    event EventHandler<StoppedEventArgs>? RecordingStopped;

    /// <summary>Asks the device to begin delivering buffers. May complete asynchronously.</summary>
    void StartRecording();

    /// <summary>Asks the device to stop delivering buffers.</summary>
    void StopRecording();
}

/// <summary>
/// Creates the WASAPI recorder behind <see cref="IAudioCaptureRecorder"/>.
/// </summary>
internal interface IAudioCaptureFactory
{
    /// <summary>
    /// Activates a process-loopback capture for the given process.
    /// </summary>
    /// <param name="processId">Process whose rendered audio should be captured.</param>
    /// <param name="bufferMilliseconds">Buffer length to request.</param>
    /// <returns>A recorder that has not been started yet.</returns>
    Task<IAudioCaptureRecorder> CreateAsync(uint processId, int bufferMilliseconds);
}

/// <summary>
/// The real factory: an asynchronous WASAPI process-loopback activation.
/// </summary>
internal sealed class WasapiCaptureFactory : IAudioCaptureFactory
{
    /// <inheritdoc />
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
    public async Task<IAudioCaptureRecorder> CreateAsync(uint processId, int bufferMilliseconds)
    {
        var recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback(processId, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithBufferLength(bufferMilliseconds)
            .BuildAsync()
            .ConfigureAwait(false);

        return new WasapiCaptureRecorder(recorder);
    }

    /// <summary>Adapts the NAudio recorder to the small surface the tap depends on.</summary>
    private sealed class WasapiCaptureRecorder : IAudioCaptureRecorder
    {
        private readonly WasapiRecorder _recorder;

        public WasapiCaptureRecorder(WasapiRecorder recorder)
        {
            _recorder = recorder;
            _recorder.DataAvailable += ForwardData;
            _recorder.RecordingStopped += ForwardStopped;
        }

        public event CaptureDataAvailableHandler? DataAvailable;

        public event EventHandler<StoppedEventArgs>? RecordingStopped;

        public WaveFormat WaveFormat => _recorder.WaveFormat;

        public void StartRecording() => _recorder.StartRecording();

        public void StopRecording() => _recorder.StopRecording();

        public void Dispose()
        {
            _recorder.DataAvailable -= ForwardData;
            _recorder.RecordingStopped -= ForwardStopped;
            DataAvailable = null;
            RecordingStopped = null;
            _recorder.Dispose();
        }

        private void ForwardData(
            ReadOnlySpan<byte> buffer,
            AudioClientBufferFlags flags,
            long devicePosition,
            long qpcPosition
        ) => DataAvailable?.Invoke(buffer, flags, devicePosition, qpcPosition);

        private void ForwardStopped(object? sender, StoppedEventArgs e) =>
            RecordingStopped?.Invoke(this, e);
    }
}
