using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Controls;

/// <summary>One column of a joint trace. Low and high are fractions of that joint's software stops: 0 is min, 1 is max.</summary>
public readonly record struct JointGraphColumn(float Low, float High, bool HasValue);

/// <summary>
/// One joint drawn on the shared chart.
/// When <see cref="Unit"/> is set, a chart that shows only this trace labels the
/// side axis in that unit instead of a shared percent of the stops.
/// </summary>
public readonly record struct JointGraphTrace(
    IBrush Stroke,
    IReadOnlyList<JointGraphColumn> Columns,
    float StopMin = 0,
    float StopMax = 0,
    string? Unit = null)
{
    public bool HasOwnScale => Unit is { Length: > 0 } && StopMax > StopMin;
}

/// <summary>
/// Every visible joint on one chart. Each trace is scaled to its own software stops,
/// so the bottom of the chart is that joint's min and the top is its max.
/// Scroll zooms around the cursor. Right-drag or middle-drag pans.
/// After the chart is clicked, F restores the full program and the full joint range.
/// Home does the same. F does not frame the 3D view while this chart has focus.
/// A left press or drag seeks the scrubber.
/// The chart fills the timeline bar. The left axis is each joint's own stop range.
/// The bottom axis is the move index of the visible window.
/// </summary>
public class JointGraphBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<JointGraphTrace>?> TracesProperty =
        AvaloniaProperty.Register<JointGraphBar, IReadOnlyList<JointGraphTrace>?>(nameof(Traces));

    public static readonly StyledProperty<double> PlayheadXProperty =
        AvaloniaProperty.Register<JointGraphBar, double>(nameof(PlayheadX));

    public static readonly StyledProperty<JointGraphFrame> FrameProperty =
        AvaloniaProperty.Register<JointGraphBar, JointGraphFrame>(nameof(Frame), JointGraphFrame.Home);

    public static readonly StyledProperty<int> MoveCountProperty =
        AvaloniaProperty.Register<JointGraphBar, int>(nameof(MoveCount));

    public IReadOnlyList<JointGraphTrace>? Traces
    {
        get => GetValue(TracesProperty);
        set => SetValue(TracesProperty, value);
    }

    public double PlayheadX
    {
        get => GetValue(PlayheadXProperty);
        set => SetValue(PlayheadXProperty, value);
    }

    public JointGraphFrame Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    /// <summary>Move count of the program. The bottom axis labels are indexes into it.</summary>
    public int MoveCount
    {
        get => GetValue(MoveCountProperty);
        set => SetValue(MoveCountProperty, value);
    }

    static readonly IBrush TrackBrush = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255));
    static readonly IBrush AxisBrush = new SolidColorBrush(Color.FromArgb(200, 186, 186, 186));
    static readonly Pen PlayheadPen = new(new SolidColorBrush(Color.FromRgb(230, 230, 230)), 1);
    static readonly Pen AlertPen = new(new SolidColorBrush(Color.FromRgb(255, 90, 90)), 1.5);
    static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(42, 255, 255, 255)), 1);
    static readonly Pen AxisPen = new(new SolidColorBrush(Color.FromArgb(160, 186, 186, 186)), 1);
    static readonly Typeface AxisTypeface = new("Segoe UI");
    static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    static readonly Cursor PanCursor = new(StandardCursorType.SizeAll);

    bool _dragging;
    bool _panning;
    Point _lastPan;

    static JointGraphBar()
    {
        AffectsRender<JointGraphBar>(TracesProperty, PlayheadXProperty, FrameProperty, MoveCountProperty);
    }

    public JointGraphBar()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.FillRectangle(Brushes.Transparent, new Rect(size));
        if (size.Width < 1 || size.Height < 1) return;

        var plot = PlotOf(size);
        var frame = Frame;
        var traces = Traces;
        JointGraphTrace? sole = traces is { Count: 1 } && traces[0].HasOwnScale ? traces[0] : null;
        context.FillRectangle(TrackBrush, new Rect(plot.X0, plot.Top, plot.Inner, plot.PlotH));
        DrawGrid(context, plot);

        using (context.PushClip(new Rect(plot.X0, plot.Top, plot.Inner, plot.PlotH)))
        {
            if (traces is { Count: > 0 })
            {
                foreach (var trace in traces)
                    DrawTrace(context, trace, frame, plot);
            }

            DrawPlayhead(context, frame, plot);
        }

        DrawAxes(context, frame, plot, MoveCount, sole);
    }

    static void DrawTrace(DrawingContext context, JointGraphTrace trace, JointGraphFrame frame, Plot plot)
    {
        var columns = trace.Columns;
        if (columns is null || columns.Count == 0) return;
        var pen = new Pen(trace.Stroke, 1.75) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        Point? prev = null;
        int n = columns.Count;
        for (int i = 0; i < n; i++)
        {
            var col = columns[i];
            if (!col.HasValue)
            {
                prev = null;
                continue;
            }
            double x = plot.X0 + frame.PlotX((i + 0.5) / n) * plot.Inner;
            double yLo = ScreenY(col.Low, frame, plot);
            double yHi = ScreenY(col.High, frame, plot);
            bool outside = col.Low < 0f || col.High > 1f;
            context.DrawLine(outside ? AlertPen : pen, new Point(x, yLo), new Point(x, yHi));
            var mid = new Point(x, (yLo + yHi) / 2.0);
            if (prev is { } p)
                context.DrawLine(pen, p, mid);
            prev = mid;
        }
    }

    void DrawPlayhead(DrawingContext context, JointGraphFrame frame, Plot plot)
    {
        double along = frame.PlotX(PlayheadX);
        if (along >= 0 && along <= 1)
        {
            double x = plot.X0 + along * plot.Inner;
            context.DrawLine(PlayheadPen, new Point(x, plot.Top), new Point(x, plot.Top + plot.PlotH));
            return;
        }

        double edge = along < 0 ? plot.X0 : plot.X0 + plot.Inner;
        context.DrawLine(PlayheadPen, new Point(edge, plot.Top), new Point(edge, plot.Top + 10));
    }

    static void DrawGrid(DrawingContext context, Plot plot)
    {
        for (int i = 0; i < 3; i++)
        {
            double t = i / 2.0;
            double y = plot.Top + (1 - t) * plot.PlotH;
            double x = plot.X0 + t * plot.Inner;
            context.DrawLine(GridPen, new Point(plot.X0, y), new Point(plot.X0 + plot.Inner, y));
            context.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Top + plot.PlotH));
        }
    }

    static void DrawAxes(DrawingContext context, JointGraphFrame frame, Plot plot, int moveCount, JointGraphTrace? sole)
    {
        double bottom = plot.Top + plot.PlotH;
        double right = plot.X0 + plot.Inner;
        context.DrawLine(AxisPen, new Point(plot.X0, plot.Top), new Point(plot.X0, bottom));
        context.DrawLine(AxisPen, new Point(plot.X0, bottom), new Point(right, bottom));

        for (int i = 0; i < 3; i++)
        {
            double t = i / 2.0;
            double y = plot.Top + (1 - t) * plot.PlotH;
            context.DrawLine(AxisPen, new Point(plot.X0 - 4, y), new Point(plot.X0, y));
            var label = AxisText(YLabel(frame.DataY(t), sole));
            double ty = Math.Clamp(y - label.Height / 2, 0, Math.Max(0, plot.Height - label.Height));
            context.DrawText(label, new Point(Math.Max(0, plot.X0 - 6 - label.Width), ty));

            double x = plot.X0 + t * plot.Inner;
            context.DrawLine(AxisPen, new Point(x, bottom), new Point(x, bottom + 4));
            var tick = AxisText(XLabel(frame.DataX(t), moveCount));
            double tx = i switch
            {
                0 => x,
                2 => x - tick.Width,
                _ => x - tick.Width / 2,
            };
            tx = Math.Clamp(tx, 0, Math.Max(0, plot.Width - tick.Width));
            context.DrawText(tick, new Point(tx, bottom + 3));
        }
    }

    static FormattedText AxisText(string text) => new(
        text,
        CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        AxisTypeface,
        9,
        AxisBrush);

    /// <summary>
    /// Several joints share a stop-range axis. One joint uses its own unit,
    /// so the ticks read degrees (or millimetres for a rail).
    /// </summary>
    static string YLabel(double data, JointGraphTrace? sole)
    {
        if (sole is { } axis)
        {
            double value = axis.StopMin + Math.Clamp(data, 0, 1) * (axis.StopMax - axis.StopMin);
            return value.ToString("0.#", CultureInfo.InvariantCulture) + axis.Unit;
        }
        if (data <= 0.02) return "min";
        if (data >= 0.98) return "max";
        return Math.Round(Math.Clamp(data, 0, 1) * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    static string XLabel(double data, int moveCount)
    {
        if (moveCount <= 0) return "0";
        double index = Math.Clamp(data, 0, 1) * moveCount;
        if (index >= 1_000_000)
            return (index / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        if (index >= 10_000)
            return (index / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return Math.Round(index).ToString("0", CultureInfo.InvariantCulture);
    }

    static double ScreenY(float fraction, JointGraphFrame frame, Plot plot)
    {
        float f = Math.Clamp(fraction, 0f, 1f);
        return plot.Top + (1d - frame.PlotY(f)) * plot.PlotH;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed || props.IsMiddleButtonPressed)
        {
            _panning = true;
            _lastPan = e.GetPosition(this);
            e.Pointer.Capture(this);
            Cursor = PanCursor;
            Focus();
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        Focus();
        Seek(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_panning)
        {
            var pos = e.GetPosition(this);
            PanByPixels(pos.X - _lastPan.X, pos.Y - _lastPan.Y);
            _lastPan = pos;
            e.Handled = true;
            return;
        }
        if (!_dragging) return;
        Seek(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (_panning && kind is PointerUpdateKind.RightButtonReleased or PointerUpdateKind.MiddleButtonReleased)
        {
            _panning = false;
            e.Pointer.Capture(null);
            Cursor = HandCursor;
            e.Handled = true;
            return;
        }
        if (!_dragging || kind != PointerUpdateKind.LeftButtonReleased) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (DataContext is not ViewportViewModel vm) return;
        var plot = PlotOf(Bounds.Size);
        var pos = e.GetPosition(this);
        double plotX = plot.Inner <= 0 ? 0.5 : Math.Clamp((pos.X - plot.X0) / plot.Inner, 0, 1);
        double plotY = plot.PlotH <= 0 ? 0.5 : Math.Clamp(1d - (pos.Y - plot.Top) / plot.PlotH, 0, 1);
        var frame = Frame;
        vm.ZoomJointGraph(frame.DataX(plotX), frame.DataY(plotY), e.Delta.Y);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if ((e.Key == Key.F || e.Key == Key.Home) && e.KeyModifiers == KeyModifiers.None && !IsTyping()
            && DataContext is ViewportViewModel vm)
        {
            vm.ResetJointGraphView();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    void Seek(double x)
    {
        if (DataContext is not ViewportViewModel vm) return;
        var plot = PlotOf(Bounds.Size);
        double plotX = plot.Inner <= 0 ? 0 : Math.Clamp((x - plot.X0) / plot.Inner, 0, 1);
        vm.SeekJointGraph(Frame.DataX(plotX));
    }

    void PanByPixels(double dx, double dy)
    {
        if (DataContext is not ViewportViewModel vm) return;
        var plot = PlotOf(Bounds.Size);
        var frame = Frame;
        double dataDx = plot.Inner <= 0 ? 0 : -(dx / plot.Inner) * frame.Width;
        double dataDy = plot.PlotH <= 0 ? 0 : (dy / plot.PlotH) * frame.Height;
        vm.PanJointGraph(dataDx, dataDy);
    }

    bool IsTyping()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not StyledElement focused)
            return false;
        string name = focused.GetType().Name;
        return name.Contains("TextBox", StringComparison.Ordinal)
            || name.Contains("TextPresenter", StringComparison.Ordinal);
    }

    static Plot PlotOf(Size size)
    {
        const double left = 52;
        const double right = 8;
        const double top = 4;
        const double bottom = 18;
        double inner = Math.Max(size.Width - left - right, 1);
        double plotH = Math.Max(size.Height - top - bottom, 1);
        return new Plot(left, top, inner, plotH, size.Width, size.Height);
    }

    readonly record struct Plot(double X0, double Top, double Inner, double PlotH, double Width, double Height);
}
