using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SongPlayer.Services.Audio;

namespace SongPlayer.Controls;

/// <summary>
/// Draws the peak-ball visualiser behind the Now Playing line sheet: a row of rounded bars
/// driven by the real playback spectrum, mirrored vertically so it reads as a soundscape rather
/// than a bar chart.
/// </summary>
/// <remarks>
/// <para>
/// The control owns its audio tap instead of receiving one through a binding. Capture costs a
/// WASAPI stream and a thread, and the view is the only thing that knows whether the visualiser
/// is on screen. Opening the overlay, switching back from lyrics, or resuming playback all ask
/// the same coordinator to start; hiding or unloading asks it to stop. The tap is disposed on
/// unload and created again on the next load, so a detach cannot permanently kill the control.
/// </para>
/// <para>
/// One clock runs at a time. While the control is loaded, visible and playing,
/// <see cref="CompositionTarget.Rendering"/> advances the drawn bars. While it is loaded and
/// visible but paused, a 33 ms timer keeps the idle wave moving. Hidden or unloaded, neither
/// runs and capture is stopped. The clock always measures real elapsed time, including when
/// playback is on but the capture has gone quiet — invalidating alone would freeze the wave.
/// </para>
/// </remarks>
public sealed class AudioVisualizer : FrameworkElement
{
    /// <summary>Number of bars the visualiser draws.</summary>
    private const int BarCount = SpectrumAnalyzer.BandCount;

    /// <summary>Vertical gap between the two mirrored halves, in pixels.</summary>
    private const double SeamHeight = 2;

    /// <summary>Space between bars, in pixels.</summary>
    private const double BarGap = 3;

    /// <summary>Bars below this height are dropped so silence draws nothing, not a dotted line.</summary>
    private const double MinBarHeight = 2;

    private static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush),
        typeof(Brush),
        typeof(AudioVisualizer),
        new FrameworkPropertyMetadata(
            Brushes.MediumPurple,
            FrameworkPropertyMetadataOptions.AffectsRender
        )
    );

    private static readonly DependencyProperty IdleBrushProperty = DependencyProperty.Register(
        nameof(IdleBrush),
        typeof(Brush),
        typeof(AudioVisualizer),
        new FrameworkPropertyMetadata(
            Brushes.DimGray,
            FrameworkPropertyMetadataOptions.AffectsRender
        )
    );

    private static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying),
        typeof(bool),
        typeof(AudioVisualizer),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.None, OnIsPlayingChanged)
    );

    private readonly DispatcherTimer _idleTimer;
    private readonly SpectrumDisplay _display = new();

    private WasapiAudioTap? _tap;
    private string? _fault;
    private string? _lastReconcile;
    private long _clockStamp;
    private bool _loaded;
    private bool _rendering;
    private bool _idleClock;

    /// <summary>Creates the visualiser and its idle animation timer.</summary>
    public AudioVisualizer()
    {
        SnapsToDevicePixels = true;

        _idleTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };

        // The overlay and the lyrics card are collapsed rather than removed, so a visual-parent
        // change is not what tells this control it appeared. IsVisibleChanged is. Loaded and
        // Unloaded still matter: unload has to release the device, and a later load has to be
        // willing to create a new one.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>Brush of the bars while music is playing.</summary>
    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    /// <summary>Brush of the flat, slowly waving line drawn while nothing is playing.</summary>
    public Brush IdleBrush
    {
        get => (Brush)GetValue(IdleBrushProperty);
        set => SetValue(IdleBrushProperty, value);
    }

    /// <summary>
    /// True while the transport is playing. Combined with visibility to decide whether the
    /// spectrum should be captured.
    /// </summary>
    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_display.ShowIdle)
        {
            DrawIdle(drawingContext, width, height);

            if (IsPlaying && !string.IsNullOrWhiteSpace(_fault))
            {
                DrawFault(drawingContext, width, height);
            }

            return;
        }

        DrawBars(drawingContext, width, height);
    }

    /// <inheritdoc />
    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        Reconcile();
    }

    /// <inheritdoc />
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    private void DrawBars(DrawingContext context, double width, double height)
    {
        var levels = _display.Levels;
        var slot = width / BarCount;
        var barWidth = Math.Max(2, slot - BarGap);
        var half = (height - SeamHeight) / 2;
        var brush = AccentBrush;

        for (var i = 0; i < BarCount; i++)
        {
            var level = i < levels.Length ? levels[i] : 0f;
            var magnitude = Math.Pow(Math.Clamp(level, 0f, 1f), 0.85);
            var barHeight = magnitude * half;

            if (barHeight < MinBarHeight)
            {
                continue;
            }

            var x = (i * slot) + (BarGap / 2);
            var radius = Math.Min(barWidth / 2, 4);

            context.DrawRoundedRectangle(
                brush,
                null,
                new Rect(x, half - barHeight, barWidth, barHeight),
                radius,
                radius
            );
            context.DrawRoundedRectangle(
                brush,
                null,
                new Rect(x, half + SeamHeight, barWidth, barHeight),
                radius,
                radius
            );
        }

        var pulse = Math.Clamp(_display.Pulse, 0, 1);

        if (pulse > 0.01)
        {
            var glowBrush = new SolidColorBrush(
                Color.FromArgb((byte)(40 + (pulse * 90)), 167, 139, 250)
            );
            glowBrush.Freeze();
            context.DrawRectangle(
                glowBrush,
                null,
                new Rect(0, half + (SeamHeight / 2) - (pulse * 6), width, Math.Max(2, pulse * 12))
            );
        }
    }

    private void DrawIdle(DrawingContext context, double width, double height)
    {
        var slot = width / BarCount;
        var barWidth = Math.Max(2, slot - BarGap);
        var half = (height - SeamHeight) / 2;
        var brush = IdleBrush;
        var phase = _display.IdlePhase;

        for (var i = 0; i < BarCount; i++)
        {
            var wave = 0.5 + (0.5 * Math.Sin(phase + (i * 0.35)));
            var barHeight = Math.Max(MinBarHeight, wave * half * 0.16);
            var x = (i * slot) + (BarGap / 2);
            var radius = Math.Min(barWidth / 2, 3);

            context.DrawRoundedRectangle(
                brush,
                null,
                new Rect(x, half - barHeight, barWidth, barHeight),
                radius,
                radius
            );
            context.DrawRoundedRectangle(
                brush,
                null,
                new Rect(x, half + SeamHeight, barWidth, barHeight),
                radius,
                radius
            );
        }
    }

    private void DrawFault(DrawingContext context, double width, double height)
    {
        var text = _fault ?? string.Empty;

        if (text.Length > 96)
        {
            text = text[..96];
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            IdleBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );

        if (formatted.Width > width - 16)
        {
            formatted.MaxTextWidth = Math.Max(0, width - 16);
            formatted.MaxLineCount = 1;
        }

        context.DrawText(formatted, new Point(8, Math.Max(0, height - formatted.Height - 4)));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        AudioDiagnostics.Event($"visualizer loaded visible={IsVisible} playing={IsPlaying}");
        Reconcile();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        AudioDiagnostics.Event($"visualizer unloaded visible={IsVisible} playing={IsPlaying}");
        Reconcile();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AudioDiagnostics.Event($"visualizer visible={IsVisible} playing={IsPlaying} loaded={_loaded}");
        Reconcile();
    }

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var visualizer = (AudioVisualizer)d;
        AudioDiagnostics.Event(
            $"visualizer playing={visualizer.IsPlaying} visible={visualizer.IsVisible} loaded={visualizer._loaded}"
        );
        visualizer.Reconcile();
    }

    /// <summary>
    /// Applies the one rule the control has: capture only while loaded, visible and playing;
    /// animate only while loaded and visible; release the device on unload.
    /// </summary>
    private void Reconcile()
    {
        if (!_loaded)
        {
            DetachClock();
            ReleaseTap();
            LogReconcile("unloaded");
            return;
        }

        var visible = IsVisible;
        var capture = visible && IsPlaying;

        if (!visible || !IsPlaying)
        {
            _fault = null;
            ToolTip = null;
        }

        if (capture)
        {
            EnsureCapture();
        }
        else
        {
            _tap?.Stop();
        }

        if (!visible)
        {
            DetachClock();
        }
        else if (IsPlaying)
        {
            UseRenderClock();
        }
        else
        {
            UseIdleClock();
        }

        LogReconcile(capture ? "capture" : visible ? "visible-idle" : "hidden");
        InvalidateVisual();
    }

    private void EnsureCapture()
    {
        if (_tap is null)
        {
            _tap = new WasapiAudioTap(new SpectrumAnalyzer(44100, 2));
            _tap.Faulted += OnTapFaulted;
        }

        _tap.Start();
    }

    private void ReleaseTap()
    {
        if (_tap is null)
        {
            return;
        }

        _tap.Faulted -= OnTapFaulted;
        _tap.Dispose();
        _tap = null;
    }

    private void UseRenderClock()
    {
        if (_idleClock)
        {
            _idleTimer.Stop();
            _idleTimer.Tick -= OnClockTick;
            _idleClock = false;
            _clockStamp = 0;
        }

        if (_rendering)
        {
            return;
        }

        _rendering = true;
        _clockStamp = 0;
        CompositionTarget.Rendering += OnClockTick;
        AudioDiagnostics.Event("rendering subscribed");
    }

    private void UseIdleClock()
    {
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnClockTick;
            _rendering = false;
            _clockStamp = 0;
            AudioDiagnostics.Event("rendering unsubscribed");
        }

        if (_idleClock)
        {
            return;
        }

        _idleClock = true;
        _clockStamp = 0;

        // Subscribed here rather than in the constructor: a timer that outlives the control
        // keeps it alive, and this control is created and dropped once per overlay open.
        _idleTimer.Tick -= OnClockTick;
        _idleTimer.Tick += OnClockTick;
        _idleTimer.Start();
        AudioDiagnostics.Event("idle clock started");
    }

    private void DetachClock()
    {
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnClockTick;
            _rendering = false;
            AudioDiagnostics.Event("rendering unsubscribed");
        }

        if (_idleClock)
        {
            _idleTimer.Stop();
            _idleTimer.Tick -= OnClockTick;
            _idleClock = false;
            AudioDiagnostics.Event("idle clock stopped");
        }

        _clockStamp = 0;
    }

    private void OnClockTick(object? sender, EventArgs e) => AdvanceClock();

    private void AdvanceClock()
    {
        var now = Stopwatch.GetTimestamp();
        var dt = _clockStamp == 0 ? 0.016 : Stopwatch.GetElapsedTime(_clockStamp, now).TotalSeconds;
        _clockStamp = now;

        var tap = _tap;
        var frame = tap?.CurrentFrame ?? SpectrumFrame.Silent;
        var ageMs = -1d;
        var fresh = false;

        if (tap is not null && frame.Timestamp != 0)
        {
            ageMs = Stopwatch.GetElapsedTime(frame.Timestamp, now).TotalMilliseconds;
            fresh =
                IsPlaying
                && tap.IsRunning
                && ageMs <= SpectrumDisplay.FreshWindowSeconds * 1000d;
        }

        if (
            !fresh
            && tap is { IsRunning: false, State: AudioCaptureState.Stopped, LastFailure: { Length: > 0 } reason }
        )
        {
            _fault = reason;
            ToolTip = reason;
        }
        else if (fresh)
        {
            _fault = null;
            ToolTip = null;
        }

        _display.Advance(frame, fresh, dt);

        if (AudioDiagnostics.IsEnabled)
        {
            AudioDiagnostics.View(
                $"loaded={_loaded} visible={IsVisible} playing={IsPlaying} rendering={_rendering} "
                    + $"idleClock={_idleClock} tap={tap?.State.ToString() ?? "none"} "
                    + $"desired={tap?.IsDesiredRunning.ToString() ?? "false"}"
            );
        }

        AudioDiagnostics.Render(ageMs);
        AudioDiagnostics.Summary();
        InvalidateVisual();
    }

    private void OnTapFaulted(object? sender, string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            try
            {
                Dispatcher.BeginInvoke(() => OnTapFaulted(sender, message));
            }
            catch (InvalidOperationException)
            {
                // The dispatcher is gone; the control is already leaving the tree.
            }

            return;
        }

        if (!IsPlaying)
        {
            return;
        }

        _fault = message;
        ToolTip = message;
        InvalidateVisual();
    }

    private void LogReconcile(string mode)
    {
        var line =
            $"{mode} loaded={_loaded} visible={IsVisible} playing={IsPlaying} "
            + $"rendering={_rendering} idleClock={_idleClock} tap={_tap?.State.ToString() ?? "none"} "
            + $"desired={_tap?.IsDesiredRunning.ToString() ?? "false"}";

        if (line == _lastReconcile)
        {
            return;
        }

        _lastReconcile = line;
        AudioDiagnostics.Event("visualizer " + line);

        if (AudioDiagnostics.IsEnabled)
        {
            AudioDiagnostics.View(line);
        }
    }
}
