using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MassiveSlicer.Controls;

/// <summary>
/// Vertical dual-handle range slider (edit-mode LAYERS window).
/// Double-click a thumb to type a layer number inline.
/// MassiveMAKE palette: gray track + lime accent fill.
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

    // Compact layout for the narrowed LAYERS panel (~55% of prior width).
    private const double TrackX = 30;
    private const double PadY = 16;
    private const double ThumbW = 16, ThumbH = 10;
    private const double ThumbHitPad = 10;
    private const double TrackHalf = 2.0;
    private const double EditW = 44, EditH = 22;

    private static readonly Color ColTrack    = Color.Parse("#3a3a3a");
    private static readonly Color ColFill     = Color.Parse("#40b840");
    private static readonly Color ColFillDim  = Color.Parse("#2a6a2a");
    private static readonly Color ColTickMaj  = Color.Parse("#6c6c6c");
    private static readonly Color ColTickMin  = Color.Parse("#454545");
    private static readonly Color ColLabel    = Color.Parse("#acacac");
    private static readonly Color ColThumbVal = Color.Parse("#40b840");
    private static readonly Color ColThumb    = Color.Parse("#e8e8e8");
    private static readonly Color ColThumbRim = Color.Parse("#171717");
    private static readonly Color ColEditBg   = Color.Parse("#2b2b2b");
    private static readonly Color ColEditBorder = Color.Parse("#40b840");

    private bool _dragging;
    private bool _dragHigh;
    private bool _lastDragHigh = true;
    private bool _coercing;
    private bool _editHigh;
    private bool _suppressDragFromEdit;
    private double _prevMaximum = 100;

    private readonly TextBox _editBox;

    static DualRangeSlider()
    {
        AffectsRender<DualRangeSlider>(MinimumProperty, MaximumProperty,
            LowValueProperty, HighValueProperty);
        LowValueProperty.Changed.AddClassHandler<DualRangeSlider>((s, _) => s.CoerceRange());
        HighValueProperty.Changed.AddClassHandler<DualRangeSlider>((s, _) => s.CoerceRange());
        MinimumProperty.Changed.AddClassHandler<DualRangeSlider>((s, _) => s.CoerceRange());
        MaximumProperty.Changed.AddClassHandler<DualRangeSlider>((s, e) =>
        {
            // When the layer count arrives/grows, keep the top handle at the top of the stack
            // if it was already at (or past) the previous maximum.
            double oldMax = e.OldValue is double om ? om : s._prevMaximum;
            double newMax = s.Maximum;
            s._prevMaximum = newMax;
            if (newMax > oldMax && s.HighValue >= oldMax - 0.5)
                s.SetCurrentValue(HighValueProperty, newMax);
            s.CoerceRange();
        });
    }

    public DualRangeSlider()
    {
        ClipToBounds = false;
        Focusable = true;

        _editBox = new TextBox
        {
            Width = EditW,
            Height = EditH,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(4, 2),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible = false,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(ColEditBg),
            BorderBrush = new SolidColorBrush(ColEditBorder),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(ColFill),
            CaretBrush = new SolidColorBrush(ColFill),
        };
        _editBox.KeyDown += OnEditKeyDown;
        _editBox.LostFocus += OnEditLostFocus;

        LogicalChildren.Add(_editBox);
        VisualChildren.Add(_editBox);

        ToolTip.SetTip(this,
            "Drag handles to set the visible layer window.\nDouble-click a handle to type a layer number.");
    }

    private void CoerceRange()
    {
        if (_coercing) return;
        _coercing = true;
        try
        {
            double min = Minimum, max = Math.Max(min + 1, Maximum);
            double lo = Math.Clamp(Math.Round(LowValue), min, max);
            double hi = Math.Clamp(Math.Round(HighValue), min, max);
            if (hi <= lo)
            {
                if (_dragHigh || _lastDragHigh || _editHigh)
                    lo = Math.Max(min, hi - 1);
                else
                    hi = Math.Min(max, lo + 1);
            }
            if (Math.Abs(lo - LowValue) > 0.01)  SetCurrentValue(LowValueProperty, lo);
            if (Math.Abs(hi - HighValue) > 0.01) SetCurrentValue(HighValueProperty, hi);
        }
        finally { _coercing = false; }

        if (_editBox.IsVisible)
            InvalidateArrange();
    }

    private double ValueToY(double v)
    {
        double span = Math.Max(1, Maximum - Minimum);
        double t = Math.Clamp((v - Minimum) / span, 0, 1);
        return PadY + (1 - t) * (Bounds.Height - PadY * 2);
    }

    private double YToValue(double y)
    {
        double trackH = Math.Max(1, Bounds.Height - PadY * 2);
        double t = 1 - Math.Clamp((y - PadY) / trackH, 0, 1);
        return Math.Round(Minimum + t * (Maximum - Minimum));
    }

    private Rect ThumbHitRect(double y) =>
        new(TrackX - ThumbW / 2 - ThumbHitPad, y - ThumbH / 2 - ThumbHitPad,
            ThumbW + ThumbHitPad * 2, ThumbH + ThumbHitPad * 2);

    public override void Render(DrawingContext ctx)
    {
        double h = Bounds.Height;
        if (h < 40) return;

        double yLow  = ValueToY(LowValue);
        double yHigh = ValueToY(HighValue);
        if (yHigh > yLow) (yHigh, yLow) = (yLow, yHigh);

        var trackBrush = new SolidColorBrush(ColTrack);
        var fillBrush  = new SolidColorBrush(ColFill);
        var dimBrush   = new SolidColorBrush(ColFillDim);
        var tickMaj    = new SolidColorBrush(ColTickMaj);
        var tickMin    = new SolidColorBrush(ColTickMin);
        var textBrush  = new SolidColorBrush(ColLabel);
        var valBrush   = new SolidColorBrush(ColThumbVal);
        var thumbBrush = new SolidColorBrush(ColThumb);
        var rimPen     = new Pen(new SolidColorBrush(ColThumbRim), 1);

        double trackTop = PadY;
        double trackH   = h - PadY * 2;
        ctx.DrawRectangle(trackBrush, null,
            new RoundedRect(new Rect(TrackX - TrackHalf, trackTop, TrackHalf * 2, trackH), TrackHalf));

        double fillTop = yHigh;
        double fillH   = Math.Max(0, yLow - yHigh);
        if (fillH > 0.5)
        {
            ctx.DrawRectangle(dimBrush, null,
                new RoundedRect(new Rect(TrackX - TrackHalf - 1.5, fillTop, TrackHalf * 2 + 3, fillH), 3));
            ctx.DrawRectangle(fillBrush, null,
                new RoundedRect(new Rect(TrackX - TrackHalf, fillTop, TrackHalf * 2, fillH), TrackHalf));
        }

        DrawTicks(ctx, tickMaj, tickMin, textBrush, h);

        // Thumbs + live value labels (always show min/max window ends).
        if (!(_editBox.IsVisible && _editHigh))
        {
            DrawThumb(ctx, yHigh, thumbBrush, rimPen, isTop: true);
            DrawValueLabel(ctx, yHigh, (int)Math.Round(HighValue), valBrush, above: true);
        }
        if (!(_editBox.IsVisible && !_editHigh))
        {
            DrawThumb(ctx, yLow, thumbBrush, rimPen, isTop: false);
            DrawValueLabel(ctx, yLow, (int)Math.Round(LowValue), valBrush, above: false);
        }
    }

    private void DrawThumb(DrawingContext ctx, double y, IBrush fill, IPen rim, bool isTop)
    {
        var rect = new RoundedRect(
            new Rect(TrackX - ThumbW / 2, y - ThumbH / 2, ThumbW, ThumbH),
            ThumbH / 2);
        ctx.DrawRectangle(fill, rim, rect);
        var pip = new SolidColorBrush(ColFill);
        double pipY = isTop ? y + 1.5 : y - 1.5;
        ctx.DrawRectangle(pip, null,
            new RoundedRect(new Rect(TrackX - 3.5, pipY - 1, 7, 2), 1));
    }

    /// <summary>Lime value next to the thumb (right side) so top always shows e.g. 338.</summary>
    private void DrawValueLabel(DrawingContext ctx, double y, int value, IBrush brush, bool above)
    {
        var typeface = new Typeface("Inter, SF Pro Text, Segoe UI, sans-serif");
        var ft = new FormattedText(
            value.ToString(),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 10, brush);
        double x = TrackX + ThumbW / 2 + 4;
        // Keep inside control width.
        if (x + ft.Width > Bounds.Width - 2)
            x = TrackX - ThumbW / 2 - 4 - ft.Width;
        double ty = above ? y - ft.Height - 2 : y + 2;
        ty = Math.Clamp(ty, 0, Math.Max(0, Bounds.Height - ft.Height));
        ctx.DrawText(ft, new Point(x, ty));
    }

    private void DrawTicks(DrawingContext ctx, IBrush majBrush, IBrush minBrush, IBrush textBrush, double h)
    {
        double range = Math.Max(1, Maximum - Minimum);
        double trackH = Math.Max(1, h - PadY * 2);

        double rawStep = range / Math.Max(2, trackH / 40);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(rawStep, 1e-6))));
        double ratio = rawStep / mag;
        double majorStep = ratio <= 1 ? mag
            : ratio <= 2 ? mag * 2
            : ratio <= 5 ? mag * 5
            : mag * 10;
        double minorStep = majorStep >= 10 ? majorStep / 5 : 1;

        var typeface = new Typeface("Inter, SF Pro Text, Segoe UI, sans-serif");
        var majPen = new Pen(majBrush, 1);
        var minPen = new Pen(minBrush, 1);

        if (minorStep > 0 && minorStep < majorStep)
        {
            double start = Math.Ceiling(Minimum / minorStep) * minorStep;
            for (double v = start; v <= Maximum + 0.001; v += minorStep)
            {
                if (Math.Abs(v / majorStep - Math.Round(v / majorStep)) < 1e-6) continue;
                // Skip labels that collide with live high/low values.
                if (NearHandle(v)) continue;
                double y = ValueToY(Math.Round(v));
                ctx.DrawLine(minPen, new Point(TrackX - 8, y), new Point(TrackX - 4, y));
            }
        }

        var majors = new SortedSet<double> { Minimum, Maximum };
        double mStart = Math.Ceiling(Minimum / majorStep) * majorStep;
        for (double v = mStart; v < Maximum - 0.001; v += majorStep)
            majors.Add(Math.Round(v));

        foreach (double v in majors)
        {
            double vv = Math.Round(v);
            // Live high/low labels already drawn next to thumbs — avoid double text.
            if (NearHandle(vv)) 
            {
                double yTick = ValueToY(vv);
                ctx.DrawLine(majPen, new Point(TrackX - 11, yTick), new Point(TrackX - 4, yTick));
                continue;
            }
            double y = ValueToY(vv);
            ctx.DrawLine(majPen, new Point(TrackX - 11, y), new Point(TrackX - 4, y));
            var ft = new FormattedText(
                ((int)vv).ToString(),
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 9, textBrush);
            double lx = Math.Max(1, TrackX - 13 - ft.Width);
            ctx.DrawText(ft, new Point(lx, y - ft.Height / 2));
        }
    }

    private bool NearHandle(double v)
    {
        return Math.Abs(v - HighValue) < 0.6 || Math.Abs(v - LowValue) < 0.6;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _editBox.Measure(new Size(EditW, EditH));
        double w = double.IsInfinity(availableSize.Width)  ? 50 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height;
        return new Size(Math.Max(48, w), Math.Max(80, h));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_editBox.IsVisible)
        {
            double y = ValueToY(_editHigh ? HighValue : LowValue);
            double x = TrackX - EditW / 2;
            x = Math.Clamp(x, 0, Math.Max(0, finalSize.Width - EditW));
            y = Math.Clamp(y - EditH / 2, 0, Math.Max(0, finalSize.Height - EditH));
            _editBox.Arrange(new Rect(x, y, EditW, EditH));
        }
        else
        {
            _editBox.Arrange(new Rect(0, 0, 0, 0));
        }
        return finalSize;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_editBox.IsVisible)
        {
            var p = e.GetPosition(_editBox);
            if (p.X >= 0 && p.Y >= 0 && p.X <= _editBox.Bounds.Width && p.Y <= _editBox.Bounds.Height)
                return;
            CommitEdit();
        }

        var pt = e.GetPosition(this);
        double dHigh = Math.Abs(pt.Y - ValueToY(HighValue));
        double dLow  = Math.Abs(pt.Y - ValueToY(LowValue));

        const double stickyPx = 10;
        if (Math.Abs(dHigh - dLow) < stickyPx)
            _dragHigh = _lastDragHigh;
        else
            _dragHigh = dHigh <= dLow;

        _lastDragHigh = _dragHigh;
        _dragging = true;
        _suppressDragFromEdit = false;
        ApplyDrag(pt.Y);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || _suppressDragFromEdit) return;
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

    protected override void OnDoubleTapped(TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        var p = e.GetPosition(this);
        double yHigh = ValueToY(HighValue);
        double yLow  = ValueToY(LowValue);
        bool nearHigh = ThumbHitRect(yHigh).Contains(p);
        bool nearLow  = ThumbHitRect(yLow).Contains(p);
        if (!nearHigh && !nearLow)
            nearHigh = Math.Abs(p.Y - yHigh) <= Math.Abs(p.Y - yLow);

        _dragging = false;
        _suppressDragFromEdit = true;
        e.Pointer.Capture(null);
        BeginEdit(nearHigh);
        e.Handled = true;
    }

    private void BeginEdit(bool high)
    {
        _editHigh = high;
        _lastDragHigh = high;
        double v = high ? HighValue : LowValue;
        _editBox.Text = ((int)Math.Round(v)).ToString();
        _editBox.IsVisible = true;
        InvalidateArrange();
        InvalidateVisual();
        Dispatcher.UIThread.Post(() =>
        {
            _editBox.Focus();
            _editBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void CommitEdit()
    {
        if (!_editBox.IsVisible) return;
        if (int.TryParse(_editBox.Text?.Trim(), out int n))
        {
            double min = Minimum;
            double max = Math.Max(min + 1, Maximum);
            if (_editHigh)
            {
                double floor = Math.Min(max, Math.Max(min + 1, LowValue + 1));
                HighValue = Math.Clamp(n, floor, max);
            }
            else
            {
                double ceil = Math.Max(min, Math.Min(max - 1, HighValue - 1));
                LowValue = Math.Clamp(n, min, ceil);
            }
        }
        EndEdit();
    }

    private void CancelEdit() => EndEdit();

    private void EndEdit()
    {
        _editBox.IsVisible = false;
        _suppressDragFromEdit = false;
        InvalidateArrange();
        InvalidateVisual();
        Focus();
    }

    private void OnEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelEdit();
            e.Handled = true;
        }
    }

    private void OnEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_editBox.IsVisible)
            CommitEdit();
    }

    private void ApplyDrag(double y)
    {
        double v = YToValue(y);
        double min = Minimum;
        double max = Math.Max(min + 1, Maximum);

        if (_dragHigh)
        {
            double floor = Math.Min(max, Math.Max(min + 1, LowValue + 1));
            HighValue = Math.Clamp(v, floor, max);
        }
        else
        {
            double ceil = Math.Max(min, Math.Min(max - 1, HighValue - 1));
            LowValue = Math.Clamp(v, min, ceil);
        }
    }
}
