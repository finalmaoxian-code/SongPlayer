using System;
using System.Threading;

namespace SongPlayer.Services.Audio;

/// <summary>
/// Low-frequency diagnostic trace for the capture and render lifecycles.
/// </summary>
/// <remarks>
/// <para>
/// The bug this file exists for is invisible in a debugger: the spectrum keeps drawing, the
/// numbers just stop changing, and the question is whether the audio callback stopped, the
/// render loop stopped, or the render is showing a stale frame. Those three answers need
/// counters, not breakpoints.
/// </para>
/// <para>
/// Everything here is aggregated: event lines are written only when the lifecycle changes state,
/// and the counters are summarised at most once per second. Nothing logs per audio buffer or per
/// rendered frame. Off by default; turn it on with either
/// <c>SONGPLAYER_DIAGNOSTICS=1</c> in the environment or a <c>SongPlayer.diagnostics</c> file next
/// to the executable, which is how it can be enabled for a test run that does not inherit the
/// shell's environment.
/// </para>
/// </remarks>
internal static class AudioDiagnostics
{
    private const string TraceVariable = "SONGPLAYER_DIAGNOSTICS";
    private const string TraceFile = "SongPlayer.diagnostics";

    private static readonly bool Enabled = ResolveEnabled();
    private static readonly object Gate = new();
    private static readonly System.Diagnostics.Stopwatch Clock =
        System.Diagnostics.Stopwatch.StartNew();

    private static long _frames;
    private static long _renders;
    private static long _lastSummaryTick;
    private static double _lastFrameAgeMs;
    private static string _state = "unknown";
    private static string _view = "unknown";

    /// <summary>True when tracing is switched on for this run.</summary>
    public static bool IsEnabled => Enabled;

    /// <summary>Records a lifecycle transition. Cheap and silent when tracing is off.</summary>
    public static void Event(string message)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            Write($"{Clock.Elapsed.TotalSeconds, 8:0.000}s  {message}");
        }
    }

    /// <summary>
    /// Records which state the tap is aiming for. Kept separate from <see cref="Event"/> so the
    /// once-per-second summary can repeat it without flooding the log.
    /// </summary>
    public static void Target(string state)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            _state = state;
        }
    }

    /// <summary>Counts accepted audio buffers.</summary>
    public static void Frame()
    {
        if (!Enabled)
        {
            return;
        }

        Interlocked.Increment(ref _frames);
    }

    /// <summary>
    /// Records what the visualiser last decided: loaded, visible, playing, and which clock is
    /// subscribed. Stored for the summary rather than written on every frame.
    /// </summary>
    public static void View(string state)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            _view = state;
        }
    }

    /// <summary>Counts drawn frames and reports the age of the spectrum they came from.</summary>
    public static void Render(double frameAgeMs)
    {
        if (!Enabled)
        {
            return;
        }

        Interlocked.Increment(ref _renders);

        lock (Gate)
        {
            _lastFrameAgeMs = frameAgeMs;
        }
    }

    /// <summary>
    /// Writes at most one summary line per second: how many audio buffers and rendered frames
    /// arrived, how old the newest spectrum is, and what the tap is trying to be.
    /// </summary>
    public static void Summary()
    {
        if (!Enabled)
        {
            return;
        }

        var now = Environment.TickCount64;

        // Volatile-ish read: two threads writing the same sample is harmless for a log line.
        if (now - Interlocked.Read(ref _lastSummaryTick) < 1000)
        {
            return;
        }

        Interlocked.Exchange(ref _lastSummaryTick, now);

        var frames = Interlocked.Exchange(ref _frames, 0);
        var renders = Interlocked.Exchange(ref _renders, 0);

        string state;
        string view;
        double age;

        lock (Gate)
        {
            age = _lastFrameAgeMs;
            state = _state;
            view = _view;
        }

        Write(
            $"{Clock.Elapsed.TotalSeconds, 8:0.000}s  summary state={state} view={view} frames/s={frames} "
                + $"renders/s={renders} frameAge={age:0}ms"
        );
    }

    private static bool ResolveEnabled()
    {
        var value = Environment.GetEnvironmentVariable(TraceVariable);

        if (value is "1" or "true" or "True")
        {
            return true;
        }

        return System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, TraceFile));
    }

    private static void Write(string line)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "audio-diagnostics.log"),
                line + Environment.NewLine
            );
        }
        catch (System.IO.IOException)
        {
            // Diagnostics must never break the feature they describe.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: a read-only output directory just means no trace.
        }
    }
}
