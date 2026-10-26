namespace MassiveSlicer.Core.Kinematics;

/// <summary>
/// The visible window of the joint chart, in fractions of the full program and the
/// full joint range. (0, 0) is the start of the program at the software minimum.
/// Width and height are the visible spans. Home is the whole chart.
/// </summary>
public readonly record struct JointGraphFrame(double Left, double Bottom, double Width, double Height)
{
    /// <summary>One downsampled column. Zooming tighter than this shows a single slice.</summary>
    public const double MinWidth = 1.0 / JointTimeline.Buckets;

    /// <summary>Tightest vertical zoom: 5% of a joint's software range.</summary>
    public const double MinHeight = 0.05;

    /// <summary>
    /// How far a horizontal pan may pass the first or last move, as a fraction of
    /// the visible width, so an endpoint can sit in from the edge of the plot.
    /// </summary>
    public const double HorizontalSlack = 0.06;

    public static JointGraphFrame Home => new(0, 0, 1, 1);

    public bool IsHome =>
        Left >= -1e-6 && Left <= 1e-6 && Bottom <= 1e-6 && Width >= 1 - 1e-6 && Height >= 1 - 1e-6;

    /// <summary>
    /// Scale the window by <paramref name="factor"/> around a data point.
    /// A factor below 1 zooms in. The anchor stays under the cursor.
    /// </summary>
    public JointGraphFrame Zoom(double anchorX, double anchorY, double factor)
    {
        if (factor <= 0 || double.IsNaN(factor) || double.IsInfinity(factor)) return this;
        double w = Math.Clamp(Width * factor, MinWidth, 1);
        double h = Math.Clamp(Height * factor, MinHeight, 1);
        double rx = Width <= 1e-12 ? 0.5 : (anchorX - Left) / Width;
        double ry = Height <= 1e-12 ? 0.5 : (anchorY - Bottom) / Height;
        rx = Math.Clamp(rx, 0, 1);
        ry = Math.Clamp(ry, 0, 1);
        return Fit(anchorX - rx * w, anchorY - ry * h, w, h);
    }

    /// <summary>
    /// Move the window by a data-space delta. Vertical pan stays inside the joint range.
    /// Horizontal pan may pass either end by <see cref="HorizontalSlack"/> of the visible width.
    /// </summary>
    public JointGraphFrame Pan(double dataDx, double dataDy)
    {
        if (double.IsNaN(dataDx) || double.IsNaN(dataDy)) return this;
        return Fit(Left + dataDx, Bottom + dataDy, Width, Height);
    }

    /// <summary>0 at the left of the window, 1 at the right. Outside the window falls outside 0..1.</summary>
    public double PlotX(double dataX) => Width <= 1e-12 ? 0 : (dataX - Left) / Width;

    /// <summary>0 at the bottom of the window, 1 at the top.</summary>
    public double PlotY(double dataY) => Height <= 1e-12 ? 0 : (dataY - Bottom) / Height;

    public double DataX(double plotX) => Left + plotX * Width;

    public double DataY(double plotY) => Bottom + plotY * Height;

    static JointGraphFrame Fit(double left, double bottom, double width, double height)
    {
        width = Math.Clamp(width, MinWidth, 1);
        height = Math.Clamp(height, MinHeight, 1);
        double slack = width * HorizontalSlack;
        left = Math.Clamp(left, -slack, 1 - width + slack);
        bottom = Math.Clamp(bottom, 0, 1 - height);
        if (Math.Abs(left) <= 1e-6 && bottom <= 1e-6
            && width >= 1 - 1e-6 && height >= 1 - 1e-6)
            return Home;
        return new JointGraphFrame(left, bottom, width, height);
    }
}
