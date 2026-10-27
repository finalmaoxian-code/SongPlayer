using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;

namespace SongPlayer.Services.Audio;

/// <summary>
/// Where a tap is in its device lifecycle. Distinct from whether capture is wanted.
/// </summary>
public enum AudioCaptureState
{
    /// <summary>No recorder is active. A later <see cref="WasapiAudioTap.Start"/> may succeed.</summary>
    Stopped,

    /// <summary>The device is being activated and has not delivered a buffer yet.</summary>
    Starting,

    /// <summary>The device has accepted <c>StartRecording</c> and has not been retired.</summary>
    Running,

    /// <summary>A stop was requested and the recorder has not been released yet.</summary>
    Stopping,

    /// <summary>The tap has been disposed and will never capture again.</summary>
    Disposed,
}

/// <summary>
/// Captures the audio SongPlayer itself is playing, so the visualiser can react to the actual
/// signal instead of to a simulated one.
/// </summary>
/// <remarks>
/// <para>
/// LibVLC owns the audio path and does not expose its decoded samples. A process-loopback
/// capture asks Windows for a copy of the mix this process rendered, which works for local
/// files and Bilibili streams alike and does not hear other applications.
/// </para>
/// <para>
/// NAudio 3.3's <c>StopRecording</c> only flips the recorder to <c>Stopping</c> and returns.
/// <c>StartRecording</c> throws "Already recording" until the capture thread has published
/// <c>Stopped</c>, which is observable as <c>RecordingStopped</c> — or as <c>Dispose</c>
/// returning, because dispose joins that thread. Reusing a recorder the moment stop returns is
/// therefore not safe, and joining it from the <c>RecordingStopped</c> callback deadlocks
/// because that callback runs on the thread dispose would wait for. This tap never does either.
/// Each start builds a new recorder. Start, stop and dispose only publish what is wanted;
/// one pump applies that to the device, and it is the only caller of
/// <c>StartRecording</c> and <c>Dispose</c>.
/// </para>
/// </remarks>
public sealed class WasapiAudioTap : IDisposable
{
    /// <summary>Capture buffer length in milliseconds.</summary>
    private const int BufferMilliseconds = 100;

    private readonly object _gate = new();
    private readonly IAudioCaptureFactory _factory;
    private readonly uint _processId;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _exhaustedRetryDelay;
    private readonly int _maxAttempts;

    private CancellationTokenSource _wake = new();
    private Task _tail = Task.CompletedTask;

    private SpectrumAnalyzer _analyzer;
    private SpectrumFrame _published = SpectrumFrame.Silent;
    private Session? _live;
    private Session? _orphan;
    private Phase _phase = Phase.Idle;
    private string? _lastFailure;
    private long _lastDataTimestamp;
    private long _retryAt;
    private long _exhaustedAt;
    private int _nextId;
    private int _failures;
    private int _draining;
    private int _again;
    private bool _desired;
    private bool _disposed;

    /// <summary>
    /// Creates a tap for one analyser.
    /// </summary>
    /// <param name="analyzer">Placeholder analyser. The device format replaces it on a successful start.</param>
    public WasapiAudioTap(SpectrumAnalyzer analyzer)
        : this(
            analyzer,
            new WasapiCaptureFactory(),
            (uint)Environment.ProcessId,
            TimeSpan.FromSeconds(1),
            maxAttempts: 4
        ) { }

    /// <summary>
    /// Creates a tap whose device and retry policy are supplied by the caller. Tests use this
    /// to run the lifecycle with no sound card.
    /// </summary>
    internal WasapiAudioTap(
        SpectrumAnalyzer analyzer,
        IAudioCaptureFactory factory,
        uint processId,
        TimeSpan retryDelay,
        int maxAttempts,
        TimeSpan? exhaustedRetryDelay = null
    )
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _processId = processId;
        _retryDelay = retryDelay < TimeSpan.Zero ? TimeSpan.Zero : retryDelay;
        _exhaustedRetryDelay = exhaustedRetryDelay ?? ExhaustedRetryDelay;
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    /// <summary>
    /// How long a spent attempt budget must stay spent before an explicit
    /// <see cref="Start"/> may re-arm it. Long enough that a caller which retries every UI
    /// change cannot turn this into a restart loop, short enough that a device which came
    /// back (a Bluetooth headset reconnecting, another process releasing exclusive mode) is
    /// picked up again without the user having to pause and resume.
    /// </summary>
    private static readonly TimeSpan ExhaustedRetryDelay = TimeSpan.FromSeconds(10);

    private enum Phase
    {
        Idle,
        Starting,
        Running,
        Stopping,
    }

    private enum Work
    {
        None,
        Start,
        Release,
        RetryWait,
    }

    /// <summary>Raised when a capture attempt fails. Handlers must not wait for this tap.</summary>
    public event EventHandler<string>? Faulted;

    /// <summary>True once the current session has successfully started and has not been retired.</summary>
    public bool IsRunning => State == AudioCaptureState.Running;

    /// <summary>True after <see cref="Start"/> until <see cref="Stop"/> or <see cref="Dispose"/>.</summary>
    public bool IsDesiredRunning
    {
        get
        {
            lock (_gate)
            {
                return _desired;
            }
        }
    }

    /// <summary>Last failure a caller can show. Cleared after a start that reaches the device.</summary>
    public string? LastFailure
    {
        get
        {
            lock (_gate)
            {
                return _lastFailure;
            }
        }
    }

    /// <summary>Where the device is, as opposed to whether capture is wanted.</summary>
    public AudioCaptureState State
    {
        get
        {
            lock (_gate)
            {
                return StateUnlocked();
            }
        }
    }

    /// <summary>
    /// Analyser of the session that most recently bound a device. The capture thread is the only
    /// writer. UI code should read <see cref="CurrentFrame"/> instead.
    /// </summary>
    public SpectrumAnalyzer Analyzer
    {
        get
        {
            lock (_gate)
            {
                return _analyzer;
            }
        }
    }

    /// <summary>Newest spectrum the current session has published, or silence if none has.</summary>
    public SpectrumFrame CurrentFrame
    {
        get
        {
            lock (_gate)
            {
                return _published;
            }
        }
    }

    /// <summary>
    /// <see cref="Stopwatch.GetTimestamp"/> of the last buffer accepted for the current session,
    /// or 0 when no buffer has been accepted.
    /// </summary>
    public long LastDataTimestamp
    {
        get
        {
            lock (_gate)
            {
                return _lastDataTimestamp;
            }
        }
    }

    /// <summary>Starts capturing. Idempotent while a session is already starting or running.</summary>
    public void Start()
    {
        string? unsupported = null;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                unsupported = "Per-process audio capture needs Windows 10 version 2004 or newer.";
                _lastFailure = unsupported;
            }
            else if (!_desired)
            {
                _desired = true;
                _failures = 0;
                _retryAt = 0;
                _exhaustedAt = 0;
            }
            else if (_phase is Phase.Starting or Phase.Running || _retryAt > 0)
            {
                // Already wanted, and either underway or waiting out a failure. A repeated
                // Start must not cancel the backoff or spin another attempt per call.
                return;
            }
            else if (_failures > 0)
            {
                // The attempt budget is spent and nothing is scheduled. Staying dead here is
                // what makes a transient device failure permanent: the visualiser keeps asking
                // to start and keeps being told it already is. A fresh explicit request (the
                // overlay reappeared, playback resumed, the track changed) re-arms the budget
                // once the cooldown has passed. No timer and no polling does this — only a
                // caller that actually wants capture again.
                var cooled =
                    _exhaustedAt == 0
                    || Stopwatch.GetTimestamp() - _exhaustedAt
                        >= (long)(_exhaustedRetryDelay.TotalSeconds * Stopwatch.Frequency);

                if (!cooled)
                {
                    return;
                }

                AudioDiagnostics.Event($"retry budget re-armed after {_failures} failures");
                _failures = 0;
                _retryAt = 0;
                _exhaustedAt = 0;
            }
        }

        if (unsupported is not null)
        {
            AudioDiagnostics.Event("start refused: " + unsupported);
            RaiseFaulted(unsupported);
            return;
        }

        AudioDiagnostics.Event("start requested");
        AudioDiagnostics.Target(Describe());
        Pulse();
        Kick();
    }

    /// <summary>
    /// Stops capturing. The request takes effect immediately, including while the device is
    /// still being built; the recorder itself is released on the pump afterwards.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _desired = false;
            _retryAt = 0;
            _failures = 0;
            _exhaustedAt = 0;
        }

        AudioDiagnostics.Event("stop requested");
        AudioDiagnostics.Target(Describe());
        Pulse();
        Kick();
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
            _desired = false;
            _retryAt = 0;
            _exhaustedAt = 0;
        }

        AudioDiagnostics.Event("dispose requested");
        AudioDiagnostics.Target(Describe());
        Pulse();
        Kick();
    }

    /// <summary>Completes when the pump has applied every request made before this call.</summary>
    internal Task WhenQuiescentAsync()
    {
        lock (_gate)
        {
            return _tail;
        }
    }

    /// <summary>One-line description used when a test times out and in the diagnostic log.</summary>
    internal string Describe()
    {
        lock (_gate)
        {
            return $"desired={_desired} state={StateUnlocked()} session={_live?.Id.ToString() ?? "-"} "
                + $"failures={_failures} fault={_lastFailure ?? "-"}";
        }
    }

    private AudioCaptureState StateUnlocked()
    {
        if (_disposed)
        {
            return AudioCaptureState.Disposed;
        }

        if (!_desired && (_live is not null || _orphan is not null || _phase == Phase.Stopping))
        {
            return AudioCaptureState.Stopping;
        }

        return _phase switch
        {
            Phase.Starting => AudioCaptureState.Starting,
            Phase.Running => AudioCaptureState.Running,
            Phase.Stopping => AudioCaptureState.Stopping,
            _ => AudioCaptureState.Stopped,
        };
    }

    private void Kick()
    {
        lock (_gate)
        {
            if (_draining == 1)
            {
                _again = 1;
                return;
            }

            _draining = 1;
            _again = 0;
            _tail = Task.Run(RunPump);
        }
    }

    private async Task RunPump()
    {
        try
        {
            while (true)
            {
                await Drain().ConfigureAwait(false);

                lock (_gate)
                {
                    if (_again == 0)
                    {
                        _draining = 0;

                        // A request can land between the check above and clearing the flag.
                        if (_again == 0)
                        {
                            return;
                        }

                        _draining = 1;
                    }

                    _again = 0;
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AudioDiagnostics.Event("pump failed: " + exception.Message);

            var restart = false;

            lock (_gate)
            {
                _lastFailure = exception.Message;
                _desired = false;
                _exhaustedAt = 0;

                if (_again == 1)
                {
                    _again = 0;
                    restart = true;
                }
                else
                {
                    _draining = 0;
                }
            }

            RaiseFaulted(exception.Message);

            if (restart)
            {
                Kick();
            }
        }
    }

    private async Task Drain()
    {
        while (true)
        {
            Work work;
            Session? session;

            lock (_gate)
            {
                (work, session) = Plan();
            }

            switch (work)
            {
                case Work.None:
                    return;
                case Work.RetryWait:
                    await WaitRetryAsync().ConfigureAwait(false);
                    break;
                case Work.Start:
                    await StartSessionAsync(session!).ConfigureAwait(false);
                    break;
                case Work.Release:
                    AudioDiagnostics.Event($"release session={session!.Id}");
                    DisposeSession(session);
                    break;
            }
        }
    }

    /// <summary>Chooses the next device operation. The caller holds <see cref="_gate"/>.</summary>
    private (Work Work, Session? Session) Plan()
    {
        if (_orphan is not null)
        {
            var orphan = _orphan;
            _orphan = null;
            return (Work.Release, orphan);
        }

        if (_disposed || !_desired)
        {
            if (_live is null)
            {
                _phase = Phase.Idle;
                return (Work.None, null);
            }

            var stopping = _live;
            _live = null;
            _phase = Phase.Stopping;
            return (Work.Release, stopping);
        }

        if (_phase == Phase.Running && _live is not null)
        {
            return (Work.None, null);
        }

        var now = Stopwatch.GetTimestamp();

        if (_retryAt > 0 && now < _retryAt)
        {
            return (Work.RetryWait, null);
        }

        if (_failures >= _maxAttempts && _retryAt == 0)
        {
            return (Work.None, null);
        }

        var session = new Session(this, ++_nextId);
        _live = session;
        _phase = Phase.Starting;
        return (Work.Start, session);
    }

    private async Task StartSessionAsync(Session session)
    {
        IAudioCaptureRecorder recorder;

        try
        {
            AudioDiagnostics.Event($"build begin session={session.Id}");
            recorder = await _factory
                .CreateAsync(_processId, BufferMilliseconds)
                .ConfigureAwait(false);
            AudioDiagnostics.Event(
                $"build done session={session.Id} {recorder.WaveFormat.SampleRate}Hz/{recorder.WaveFormat.Channels}ch"
            );
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            FailSession(session, exception.Message);
            return;
        }

        var analyzer = new SpectrumAnalyzer(
            recorder.WaveFormat.SampleRate,
            recorder.WaveFormat.Channels
        );
        var adopt = false;

        lock (_gate)
        {
            adopt = !_disposed && _desired && ReferenceEquals(_live, session) && !session.Retired;

            if (adopt)
            {
                session.Bind(recorder, analyzer);
                _analyzer = analyzer;
            }
        }

        if (!adopt)
        {
            AudioDiagnostics.Event($"build discarded session={session.Id}");
            SafeDispose(recorder);

            lock (_gate)
            {
                if (ReferenceEquals(_live, session))
                {
                    _live = null;
                    _phase = Phase.Idle;
                }
            }

            return;
        }

        // Stop/Dispose cannot release the device until this method returns — they only flip
        // the request. Re-read it immediately before starting so a stop that won the race
        // never calls StartRecording.
        lock (_gate)
        {
            adopt = !_disposed && _desired && ReferenceEquals(_live, session) && !session.Retired;
        }

        if (!adopt)
        {
            AudioDiagnostics.Event($"start aborted session={session.Id}");
            DisposeSession(session);

            lock (_gate)
            {
                if (ReferenceEquals(_live, session))
                {
                    _live = null;
                    _phase = Phase.Idle;
                }
            }

            return;
        }

        try
        {
            recorder.StartRecording();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            FailSession(session, exception.Message);
            return;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_live, session) && !session.Retired && !_disposed && _desired)
            {
                _phase = Phase.Running;
                _failures = 0;
                _retryAt = 0;
                _exhaustedAt = 0;
                _lastFailure = null;
                AudioDiagnostics.Event($"recording started session={session.Id}");
            }
        }

        AudioDiagnostics.Target(Describe());
    }

    private async Task WaitRetryAsync()
    {
        TimeSpan delay;
        CancellationToken token;

        lock (_gate)
        {
            var ticks = _retryAt - Stopwatch.GetTimestamp();
            delay =
                ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);
            token = _wake.Token;
        }

        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        AudioDiagnostics.Event($"retry wait {delay.TotalMilliseconds:0}ms");

        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // Stop, dispose, or a newer start cancelled the wait. The token source may already
            // have been replaced. The next plan reads whatever those requests wrote.
        }
    }

    private void FailSession(Session session, string message)
    {
        lock (_gate)
        {
            _lastFailure = message;

            if (ReferenceEquals(_live, session))
            {
                _live = null;
                _phase = Phase.Idle;
            }

            CountFailure(session);
        }

        AudioDiagnostics.Event($"session {session.Id} failed: {message}");
        AudioDiagnostics.Target(Describe());
        DisposeSession(session);
        RaiseFaulted(message);
    }

    /// <summary>Records one failed attempt and arms a single delayed retry. Caller holds the lock.</summary>
    private void CountFailure(Session session)
    {
        if (session.FailureCounted)
        {
            return;
        }

        session.FailureCounted = true;
        _failures++;

        if (_desired && !_disposed && _failures < _maxAttempts)
        {
            _retryAt = Stopwatch.GetTimestamp() + (long)(_retryDelay.TotalSeconds * Stopwatch.Frequency);
        }
        else
        {
            _retryAt = 0;
            _exhaustedAt = Stopwatch.GetTimestamp();
        }
    }

    private void HandleData(Session session, ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags)
    {
        if (session.Retired || session.Analyzer is null)
        {
            return;
        }

        try
        {
            var analyzer = session.Analyzer;
            var now = Stopwatch.GetTimestamp();
            var elapsed = session.ConsumeElapsed(now);

            if (flags.HasFlag(AudioClientBufferFlags.Silent) || buffer.IsEmpty)
            {
                analyzer.Decay(elapsed);
            }
            else
            {
                var samples = MemoryMarshal.Cast<byte, float>(buffer);
                analyzer.Process(samples, samples.Length, elapsed);
            }

            var frame = analyzer.BuildFrame();

            lock (_gate)
            {
                if (session.Retired || !ReferenceEquals(_live, session))
                {
                    return;
                }

                _published = frame;
                _lastDataTimestamp = now;
            }

            AudioDiagnostics.Frame();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AudioDiagnostics.Event($"data session={session.Id}: {exception.Message}");
        }
    }

    private void HandleStopped(Session session, Exception? exception)
    {
        string? fault;

        lock (_gate)
        {
            // An intentional release retires the session before dispose joins the thread, and a
            // callback that belongs to a session we already replaced must not touch the new one.
            if (session.Retired || !ReferenceEquals(_live, session))
            {
                return;
            }

            _live = null;
            _phase = Phase.Idle;
            _orphan = session;
            fault = exception?.Message ?? "Capture stopped unexpectedly.";
            _lastFailure = fault;
            CountFailure(session);
        }

        AudioDiagnostics.Event($"recording stopped session={session.Id}: {fault}");
        AudioDiagnostics.Target(Describe());
        RaiseFaulted(fault);
        Pulse();
        Kick();
    }

    private static void DisposeSession(Session session)
    {
        var recorder = session.Detach();

        if (recorder is null)
        {
            return;
        }

        SafeDispose(recorder);
    }

    private static void SafeDispose(IAudioCaptureRecorder recorder)
    {
        try
        {
            recorder.Dispose();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AudioDiagnostics.Event("recorder dispose: " + exception.Message);
        }
    }

    private void RaiseFaulted(string message)
    {
        var handler = Faulted;

        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AudioDiagnostics.Event("fault handler: " + exception.Message);
        }
    }

    private void Pulse()
    {
        CancellationTokenSource previous;

        lock (_gate)
        {
            previous = _wake;
            _wake = new CancellationTokenSource();
        }

        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already signalled.
        }

        previous.Dispose();
    }

    /// <summary>
    /// One activation. Its recorder, analyser and callbacks are never shared with the next one.
    /// </summary>
    private sealed class Session
    {
        private readonly WasapiAudioTap _owner;
        private int _retired;
        private long _lastTimestamp;

        public Session(WasapiAudioTap owner, int id)
        {
            _owner = owner;
            Id = id;
        }

        public int Id { get; }

        public SpectrumAnalyzer? Analyzer { get; private set; }

        public IAudioCaptureRecorder? Recorder { get; private set; }

        public bool FailureCounted { get; set; }

        public bool Retired => Volatile.Read(ref _retired) != 0;

        public void Bind(IAudioCaptureRecorder recorder, SpectrumAnalyzer analyzer)
        {
            Recorder = recorder;
            Analyzer = analyzer;
            recorder.DataAvailable += OnData;
            recorder.RecordingStopped += OnStopped;
        }

        /// <summary>
        /// Unhooks and returns the recorder exactly once. The caller disposes it outside the
        /// state lock and never from <see cref="OnStopped"/>.
        /// </summary>
        public IAudioCaptureRecorder? Detach()
        {
            if (Interlocked.Exchange(ref _retired, 1) != 0)
            {
                return null;
            }

            var recorder = Recorder;
            Recorder = null;

            if (recorder is not null)
            {
                recorder.DataAvailable -= OnData;
                recorder.RecordingStopped -= OnStopped;
            }

            return recorder;
        }

        public double ConsumeElapsed(long now)
        {
            var previous = _lastTimestamp;
            _lastTimestamp = now;

            if (previous == 0)
            {
                return 0.1;
            }

            var elapsed = Stopwatch.GetElapsedTime(previous, now).TotalSeconds;
            return Math.Clamp(double.IsFinite(elapsed) ? elapsed : 0, 0, 0.5);
        }

        private void OnData(
            ReadOnlySpan<byte> buffer,
            AudioClientBufferFlags flags,
            long devicePosition,
            long qpcPosition
        ) => _owner.HandleData(this, buffer, flags);

        private void OnStopped(object? sender, NAudio.Wave.StoppedEventArgs e) =>
            _owner.HandleStopped(this, e.Exception);
    }
}
