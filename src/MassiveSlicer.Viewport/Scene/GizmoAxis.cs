namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Which handle of the gizmo is active during a drag: one of the three axes, one of the three
/// planar bands between them, or all of them at once.
/// </summary>
public enum GizmoAxis
{
    None,
    X,
    Y,
    Z,
    /// <summary>Band spanning the X and Y arrows — drags in that plane, leaving Z alone.</summary>
    XY,
    /// <summary>Band spanning the Y and Z arrows — drags in that plane, leaving X alone.</summary>
    YZ,
    /// <summary>Band spanning the X and Z arrows — drags in that plane, leaving Y alone.</summary>
    XZ,
    All,
}

/// <summary>Helpers for classifying a <see cref="GizmoAxis"/>.</summary>
public static class GizmoAxisExtensions
{
    /// <summary>True for the three planar bands.</summary>
    public static bool IsPlane(this GizmoAxis axis)
        => axis is GizmoAxis.XY or GizmoAxis.YZ or GizmoAxis.XZ;

    /// <summary>
    /// The two axis indices a band spans, and the index of the axis it leaves alone (its normal).
    /// </summary>
    public static (int A, int B, int Normal) PlaneAxes(this GizmoAxis axis) => axis switch
    {
        GizmoAxis.XY => (0, 1, 2),
        GizmoAxis.YZ => (1, 2, 0),
        _            => (0, 2, 1),   // XZ
    };
}
