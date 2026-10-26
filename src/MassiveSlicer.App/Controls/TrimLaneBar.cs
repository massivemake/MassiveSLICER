using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;

namespace MassiveSlicer.Controls;

/// <summary>
/// One section on the playback timeline. <see cref="StartX"/> and <see cref="EndX"/>
/// are lane pixels. A missing end is a single handle.
/// </summary>
public sealed record TrimLaneSpan(double StartX, double EndX, bool HasStart, bool HasEnd);

/// <summary>
/// Span under the scrubber with a draggable start and end. A drag reports which
/// end moved and the pointer X. The view-model turns that into a scrub index.
/// </summary>
public class TrimLaneBar : Control
{
    public static readonly StyledProperty<TrimLaneSpan?> SpanProperty =
        AvaloniaProperty.Register<TrimLaneBar, TrimLaneSpan?>(nameof(Span));

    public TrimLaneSpan? Span
    {
        get => GetValue(SpanProperty);
        set => SetValue(SpanProperty, value);
    }

    /// <summary>Raised while an end is dragged: (isStart, pointerX).</summary>
    public Action<bool, double>? EndDragged { get; set; }

    private static readonly IBrush HandleBrush = new SolidColorBrush(Color.FromRgb(120, 176, 216));
    private static readonly Pen SpanPen = new(HandleBrush, 3);

    private bool _dragging;
    private bool _dragStart;

    static TrimLaneBar()
    {
        AffectsRender<TrimLaneBar>(SpanProperty);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var span = Span;
        if (span is null || (!span.HasStart && !span.HasEnd)) return;

        double cy = Bounds.Height / 2.0;
        if (span.HasStart && span.HasEnd)
            context.DrawLine(SpanPen, new Point(span.StartX, cy), new Point(span.EndX, cy));
        if (span.HasStart)
            context.FillRectangle(HandleBrush, new Rect(span.StartX - 2, cy - 6, 4, 12));
        if (span.HasEnd)
            context.FillRectangle(HandleBrush, new Rect(span.EndX - 2, cy - 6, 4, 12));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var span = Span;
        if (span is null) return;
        double x = e.GetPosition(this).X;
        bool hitStart = span.HasStart && Math.Abs(x - span.StartX) <= 8;
        bool hitEnd = span.HasEnd && Math.Abs(x - span.EndX) <= 8;
        if (!hitStart && !hitEnd) return;
        if (hitStart && hitEnd)
            _dragStart = Math.Abs(x - span.StartX) <= Math.Abs(x - span.EndX);
        else
            _dragStart = hitStart;
        _dragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        EndDragged?.Invoke(_dragStart, e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        EndDragged?.Invoke(_dragStart, e.GetPosition(this).X);
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
