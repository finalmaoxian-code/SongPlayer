using System;
using System.Windows;
using System.Windows.Controls;
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
/// is on screen: opening the overlay starts it, closing it stops it, and pausing fades it out.
/// Handing the tap to a view model would move that lifecycle into object lifetime rules that
/// have nothing to do with drawing.
/// </para>
/// <para>
/// Rendering is frame-driven through <see cref="CompositionTarget.Rendering"/> only while the
/// control is visible and something is playing, so a paused overlay costs no CPU at all.
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

    /// <summary>How quickly the idle wave moves, in radians per second.</summary>
    private const double IdleSpeed = 1.1;

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
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.None,
            OnIsPlayingChanged
        )
    );

    private readonly DispatcherTimer _idleTimer;

    private WasapiAudioTap? _tap;
    private bool _isRendering;
    private double _idlePhase;

    /// <summary>Creates the visualiser and its idle animation timer.</summary>
    public AudioVisualizer()
    {
        SnapsToDevicePixels = true;

        // The idle wave is animated by a timer rather than by the composition clock so it stops
        // the instant the overlay closes, instead of leaving a registered handler behind.
        _idleTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };

        _idleTimer.Tick += OnIdleTick;

        // The overlay is collapsed rather than removed, so this control never sees a visual-parent
        // change when it appears: its ancestors only flip Visibility. Watching IsVisible is the
        // only notification that arrives in that case, and without it capture would never start.
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
    /// True while the transport is playing. Drives the capture lifecycle: starting capture on
    /// true, releasing it on false.
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

        var analyzer = _tap?.Analyzer;

        if (analyzer is null || analyzer.IsSilent)
        {
            DrawIdle(drawingContext, width, height);
            return;
        }

        DrawBars(drawingContext, analyzer, width, height);
    }

    /// <inheritdoc />
    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);

        if (oldParent is null)
        {
            // Becoming visible again: re-evaluate capture, since IsVisible notifications are
            // unreliable for elements inside a collapsed overlay.
            UpdateCaptureState();
        }
        else if (!IsVisible)
        {
            StopCapture();
        }
    }

    /// <summary>
    /// Draws the mirrored spectrum. Each bar is a rounded rectangle centred on the seam, with its
    /// height taken from the corresponding band and its colour fading towards the edges.
    /// </summary>
    private void DrawBars(
        DrawingContext context,
        SpectrumAnalyzer analyzer,
        double width,
        double height
    )
    {
        var levels = analyzer.BandLevels;
        var slot = width / BarCount;
        var barWidth = Math.Max(2, slot - BarGap);
        var half = (height - SeamHeight) / 2;
        var brush = AccentBrush;

        for (var i = 0; i < BarCount; i++)
        {
            var level = Math.Clamp(levels[i], 0f, 1f);

            // A gentle curve keeps quiet detail visible without letting loud passages jump.
            var magnitude = Math.Pow(level, 0.85);
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

        // The seam glows with overall loudness, which is what makes the shape feel like it is
        // breathing with the track rather than twitching per frequency.
        var pulse = Math.Clamp(analyzer.PulseLevel, 0, 1);

        if (pulse > 0.01)
        {
            var glow = new Rect(
                0,
                half + (SeamHeight / 2) - (pulse * 6),
                width,
                Math.Max(2, pulse * 12)
            );
            context.DrawRectangle(
                new SolidColorBrush(Color.FromArgb((byte)(40 + (pulse * 90)), 167, 139, 250)),
                null,
                glow
            );
        }
    }

    /// <summary>
    /// Draws the idle state: a flat, slowly travelling wave that says "paused", not "broken".
    /// </summary>
    private void DrawIdle(DrawingContext context, double width, double height)
    {
        var slot = width / BarCount;
        var barWidth = Math.Max(2, slot - BarGap);
        var half = (height - SeamHeight) / 2;
        var brush = IdleBrush;

        for (var i = 0; i < BarCount; i++)
        {
            var wave = 0.5 + (0.5 * Math.Sin(_idlePhase + (i * 0.35)));
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

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateCaptureState();
    }

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var visualizer = (AudioVisualizer)d;
        visualizer.UpdateCaptureState();
    }

    private void UpdateCaptureState()
    {
        if (IsPlaying && IsVisible)
        {
            StartCapture();
        }
        else
        {
            StopCapture();
        }
    }

    private void StartCapture()
    {
        _tap ??= new WasapiAudioTap(new SpectrumAnalyzer(44100, 2));
        _tap.Start();

        if (_isRendering)
        {
            return;
        }

        _isRendering = true;
        _idleTimer.Stop();
        CompositionTarget.Rendering += OnCompositionRendering;
    }

    private void StopCapture()
    {
        if (_isRendering)
        {
            CompositionTarget.Rendering -= OnCompositionRendering;
            _isRendering = false;
        }

        _tap?.Stop();

        // One idle tick keeps the wave alive while the overlay is open and paused.
        if (IsVisible)
        {
            _idleTimer.Start();
        }
        else
        {
            _idleTimer.Stop();
        }

        InvalidateVisual();
    }

    private void OnCompositionRendering(object? sender, EventArgs e) => InvalidateVisual();

    private void OnIdleTick(object? sender, EventArgs e)
    {
        _idlePhase += 0.06;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
