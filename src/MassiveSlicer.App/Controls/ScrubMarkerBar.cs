using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Collections.Generic;

namespace MassiveSlicer.Controls;

/// <summary>
/// Draws scrubber markers (unreachable / singularity ticks) in a single Render pass
/// instead of instantiating one visual element per marker.
/// </summary>
public class ScrubMarkerBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> MarkersProperty =
        AvaloniaProperty.Register<ScrubMarkerBar, IReadOnlyList<double>?>(nameof(Markers));

    public static readonly StyledProperty<IBrush?> MarkerBrushProperty =
        AvaloniaProperty.Register<ScrubMarkerBar, IBrush?>(nameof(MarkerBrush));

    public IReadOnlyList<double>? Markers
    {
        get => GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public IBrush? MarkerBrush
    {
        get => GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    static ScrubMarkerBar()
    {
        AffectsRender<ScrubMarkerBar>(MarkersProperty, MarkerBrushProperty);
    }

    public override void Render(DrawingContext context)
    {
        var markers = Markers;
        var brush   = MarkerBrush;
        if (markers is null || markers.Count == 0 || brush is null) return;

        double h = Bounds.Height;
        foreach (var x in markers)
            context.FillRectangle(brush, new Rect(x, 0, 1, h));
    }
}
