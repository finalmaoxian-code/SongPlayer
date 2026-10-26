using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SongPlayer.Controls;

/// <summary>
/// Flat slider used for the seek bar and the volume control.
/// <para>
/// The stock WPF <see cref="Slider"/> chrome is heavy and its <c>Track</c> /
/// repeat-button plumbing is awkward to restyle, so this control paints the track, the
/// filled portion and the hover dot itself. Behaviour that matters - keyboard stepping,
/// click-to-seek, dragging, data binding and automation - is inherited from
/// <see cref="Slider"/>; only the visuals are replaced.
/// </para>
/// </summary>
public sealed class SlimSlider : Slider
{
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(SlimSlider),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush),
        typeof(Brush),
        typeof(SlimSlider),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThumbBrushProperty = DependencyProperty.Register(
        nameof(ThumbBrush),
        typeof(Brush),
        typeof(SlimSlider),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackThicknessProperty = DependencyProperty.Register(
        nameof(TrackThickness),
        typeof(double),
        typeof(SlimSlider),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThumbDiameterProperty = DependencyProperty.Register(
        nameof(ThumbDiameter),
        typeof(double),
        typeof(SlimSlider),
        new FrameworkPropertyMetadata(11.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Colour of the unfilled part of the bar.</summary>
    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>Colour of the portion left of the thumb.</summary>
    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    /// <summary>Colour of the thumb dot shown while hovering or dragging.</summary>
    public Brush? ThumbBrush
    {
        get => (Brush?)GetValue(ThumbBrushProperty);
        set => SetValue(ThumbBrushProperty, value);
    }

    /// <summary>Visible height of the bar in device independent pixels.</summary>
    public double TrackThickness
    {
        get => (double)GetValue(TrackThicknessProperty);
        set => SetValue(TrackThicknessProperty, value);
    }

    /// <summary>Diameter of the thumb dot.</summary>
    public double ThumbDiameter
    {
        get => (double)GetValue(ThumbDiameterProperty);
        set => SetValue(ThumbDiameterProperty, value);
    }

    static SlimSlider()
    {
        // Give the type its own default style key. Without this the control inherits
        // Slider's stock theme template, whose own track and thumb would be painted on
        // top of everything OnRender draws.
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SlimSlider),
            new FrameworkPropertyMetadata(typeof(SlimSlider)));
    }

    public SlimSlider()
    {
        Height = 16;
        Focusable = true;
        Cursor = Cursors.Hand;
        IsMoveToPointEnabled = true;

        // Empty template: the control is a pure drawing surface, so keep the default
        // style from contributing any visual tree at all.
        Template = new ControlTemplate(typeof(SlimSlider));
    }

    /// <summary>
    /// Fraction of the track that is filled, clamped to [0, 1].
    /// A slider whose range is empty renders as an empty track instead of dividing by zero.
    /// </summary>
    private double FillRatio
    {
        get
        {
            double range = Maximum - Minimum;
            if (range <= 0 || double.IsNaN(range))
            {
                return 0;
            }

            return Math.Clamp((Value - Minimum) / range, 0, 1);
        }
    }

    /// <summary>
    /// Paints track, fill and thumb. The control has no template, so this is the only
    /// place that produces visual output.
    /// </summary>
    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double thickness = Math.Max(1, TrackThickness);
        double radius = thickness / 2;
        double centerY = height / 2;

        // Unfilled track: spans the full width so the clickable area is obvious.
        dc.DrawRoundedRectangle(
            ResolveBrush(TrackBrush, "Surface4Brush", Color.FromRgb(0x2C, 0x32, 0x3E)),
            null,
            new Rect(0, centerY - radius, width, thickness),
            radius,
            radius);

        // Filled portion.
        double fillWidth = width * FillRatio;
        if (fillWidth > 0)
        {
            dc.DrawRoundedRectangle(
                ResolveBrush(FillBrush, "AccentBrush", Color.FromRgb(0xA7, 0x8B, 0xFA)),
                null,
                new Rect(0, centerY - radius, fillWidth, thickness),
                radius,
                radius);
        }

        // Thumb dot: hidden at rest, revealed on hover/drag to keep the bar calm.
        if (!IsMouseOver && !IsDragging)
        {
            return;
        }

        double diameter = Math.Max(2, ThumbDiameter);
        var center = new Point(
            Math.Clamp(fillWidth, diameter / 2, Math.Max(diameter / 2, width - diameter / 2)),
            centerY);

        dc.DrawEllipse(
            ResolveBrush(ThumbBrush, "TextPrimaryBrush", Color.FromRgb(0xF3, 0xF4, 0xF8)),
            null,
            center,
            diameter / 2,
            diameter / 2);
    }

    /// <summary>
    /// Jumps to the clicked position. Handled in preview so the base <see cref="Slider"/>
    /// drag logic never starts from a stale thumb position (the control has no template
    /// thumb for it to grab).
    /// </summary>
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        if (!IsEnabled)
        {
            return;
        }

        Focus();
        CaptureMouse();
        IsDragging = true;
        UpdateValueFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    /// <summary>Follows the pointer while the bar is held down.</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (IsDragging)
        {
            UpdateValueFromPoint(e.GetPosition(this));
        }
    }

    /// <summary>Ends the drag and releases the mouse capture.</summary>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!IsDragging)
        {
            return;
        }

        IsDragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>
    /// Repaints when hover or capture state changes, because the thumb visibility
    /// depends on both.
    /// </summary>
    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        InvalidateVisual();
    }

    /// <summary>Hides the thumb again once the pointer leaves.</summary>
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        InvalidateVisual();
    }

    /// <summary>Maps a pointer position on the bar to a value in the slider range.</summary>
    private void UpdateValueFromPoint(Point position)
    {
        double width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        double ratio = Math.Clamp(position.X / width, 0, 1);
        Value = Minimum + (ratio * (Maximum - Minimum));
    }

    /// <summary>
    /// Returns the explicitly assigned brush, otherwise the matching design token, so the
    /// control still renders sensibly when it is used outside a themed window.
    /// </summary>
    private Brush ResolveBrush(Brush? explicitBrush, string resourceKey, Color fallback)
    {
        if (explicitBrush is not null)
        {
            return explicitBrush;
        }

        if (TryFindResource(resourceKey) is Brush themed)
        {
            return themed;
        }

        var brush = new SolidColorBrush(fallback);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Whether the pointer is currently held down on the bar. Tracked separately from
    /// <see cref="UIElement.IsMouseCaptured"/> so the visual state survives capture loss.
    /// </summary>
    private bool IsDragging
    {
        get => _isDragging;
        set
        {
            if (_isDragging == value)
            {
                return;
            }

            _isDragging = value;
            InvalidateVisual();
        }
    }

    private bool _isDragging;
}
