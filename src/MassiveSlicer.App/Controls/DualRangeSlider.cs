using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MassiveSlicer.Controls;

/// <summary>
/// Vertical dual-handle range slider (the edit-mode LAYERS window): ONE track,
/// a filled band between the two pill thumbs, tick marks with value labels on
/// the left, top thumb = upper bound, bottom thumb = lower bound. Values are
/// integers (layer numbers), Maximum at the top of the track, Minimum at the
/// bottom. Drag either thumb, or click anywhere to grab the nearest one.
/// </summary>
public class DualRangeSlider : Control
{
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<DualRangeSlider, double>(nameof(Minimum), 1);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<DualRangeSlider, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<double> LowValueProperty =
        AvaloniaProperty.Register<DualRangeSlider, double>(nameof(LowValue), 1,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> HighValueProperty =
        AvaloniaProperty.Register<DualRangeSlider, double>(nameof(HighValue), 100,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public double Minimum   { get => GetValue(MinimumProperty);   set => SetValue(MinimumProperty, value); }
    public double Maximum   { get => GetValue(MaximumProperty);   set => SetValue(MaximumProperty, value); }
    public double LowValue  { get => GetValue(LowValueProperty);  set => SetValue(LowValueProperty, value); }
    public double HighValue { get => GetValue(HighValueProperty); set => SetValue(HighValueProperty, value); }

    private const double TrackX = 58;      // track center (labels + ticks live left of it)
    private const double PadY = 12;        // breathing room for the thumbs
    private const double ThumbW = 22, ThumbH = 12;

    private bool _dragging;
    private bool _dragHigh;

    static DualRangeSlider()
    {
        AffectsRender<DualRangeSlider>(MinimumProperty, MaximumProperty,
            LowValueProperty, HighValueProperty);
    }

    private double ValueToY(double v)
    {
        double span = Math.Max(1, Maximum - Minimum);
        double t = Math.Clamp((v - Minimum) / span, 0, 1);
        return PadY + (1 - t) * (Bounds.Height - PadY * 2);
    }

    private double YToValue(double y)
    {
        double t = 1 - Math.Clamp((y - PadY) / Math.Max(1, Bounds.Height - PadY * 2), 0, 1);
        return Math.Round(Minimum + t * (Maximum - Minimum));
    }

    public override void Render(DrawingContext ctx)
    {
        double h = Bounds.Height;
        if (h < 40) return;

        var trackBrush = new SolidColorBrush(Color.Parse("#33FFFFFF"));
        var fillBrush  = new SolidColorBrush(Color.Parse("#6B7BFF"));
        var tickBrush  = new SolidColorBrush(Color.Parse("#55FFFFFF"));
        var textBrush  = new SolidColorBrush(Color.Parse("#B0B8C4"));
        var thumbBrush = new SolidColorBrush(Color.Parse("#F2F2F2"));

        double yLow = ValueToY(LowValue);
        double yHigh = ValueToY(HighValue);

        // Track + filled range band.
        ctx.DrawRectangle(trackBrush, null,
            new RoundedRect(new Rect(TrackX - 2, PadY, 4, h - PadY * 2), 2));
        ctx.DrawRectangle(fillBrush, null,
            new RoundedRect(new Rect(TrackX - 2.5, yHigh, 5, Math.Max(0, yLow - yHigh)), 2.5));

        // Ticks + labels on the left at a "nice" step.
        double range = Math.Max(1, Maximum - Minimum);
        double rawStep = range / Math.Max(2, (h - PadY * 2) / 44);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        double step = rawStep / mag <= 1 ? mag : rawStep / mag <= 2.5 ? mag * 2.5 : rawStep / mag <= 5 ? mag * 5 : mag * 10;
        var typeface = new Typeface("Inter, San Francisco, Segoe UI, sans-serif");
        for (double v = Minimum; v <= Maximum + 0.001; v += step)
        {
            double vv = Math.Round(v);
            double y = ValueToY(vv);
            ctx.DrawLine(new Pen(tickBrush, 1), new Point(TrackX - 14, y), new Point(TrackX - 6, y));
            var ft = new FormattedText(((int)vv).ToString(),
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10, textBrush);
            ctx.DrawText(ft, new Point(TrackX - 20 - ft.Width, y - ft.Height / 2));
        }
        // Always mark the extremes.
        foreach (double vv in new[] { Minimum, Maximum })
        {
            double y = ValueToY(vv);
            ctx.DrawLine(new Pen(tickBrush, 1), new Point(TrackX - 14, y), new Point(TrackX - 6, y));
        }

        // Pill thumbs (top = high, bottom = low).
        foreach (double y in new[] { yHigh, yLow })
            ctx.DrawRectangle(thumbBrush, new Pen(new SolidColorBrush(Color.Parse("#40000000")), 1),
                new RoundedRect(new Rect(TrackX - ThumbW / 2, y - ThumbH / 2, ThumbW, ThumbH), ThumbH / 2));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        double dHigh = Math.Abs(p.Y - ValueToY(HighValue));
        double dLow = Math.Abs(p.Y - ValueToY(LowValue));
        _dragHigh = dHigh <= dLow;
        _dragging = true;
        ApplyDrag(p.Y);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        ApplyDrag(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void ApplyDrag(double y)
    {
        double v = YToValue(y);
        if (_dragHigh)
            HighValue = Math.Clamp(v, Math.Min(LowValue + 1, Maximum), Maximum);
        else
            LowValue = Math.Clamp(v, Minimum, Math.Max(HighValue - 1, Minimum));
    }
}
