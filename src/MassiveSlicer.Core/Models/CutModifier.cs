namespace MassiveSlicer.Core.Models;

/// <summary>Common surface for all modifier-stack entries. Cut is the first type; more are planned.</summary>
public interface IModifier
{
    string Name { get; set; }

    /// <summary>Whether this modifier participates when the stack is applied.</summary>
    bool Enabled { get; set; }

    /// <summary>Whether this modifier's viewport gizmo/preview is shown.</summary>
    bool PreviewVisible { get; set; }
}

/// <summary>
/// Horizontal = the flat, tabletop-like plane (normal along Z) — cuts a tall model into
/// shorter stacked (top/bottom) pieces. Vertical = the upright, flagpole-like plane
/// (normal along a horizontal axis) — cuts a wide model into side-by-side pieces.
/// </summary>
public enum CutOrientation { Horizontal, Vertical }

/// <summary>
/// A non-destructive Cut modifier: a plane that splits the original mesh and toolpath into two
/// pieces when the stack is applied. Always reads the original mesh/toolpath — never a piece
/// produced by another modifier. When <see cref="Cut"/> is false the plane is a reference marker
/// only and produces no pieces.
/// </summary>
public sealed class CutModifier : IModifier
{
    public string Name { get; set; } = "Cut";

    public bool Enabled { get; set; } = true;

    public bool PreviewVisible { get; set; } = true;

    /// <summary>When false, this plane marks a position for other modifiers to reference and cuts nothing.</summary>
    public bool Cut { get; set; } = true;

    public CutOrientation Orientation { get; set; } = CutOrientation.Horizontal;

    /// <summary>
    /// Which way a Vertical-orientation plane faces, in degrees around the vertical (Z) axis —
    /// 0° = facing +X, 90° = facing +Y, and any value between for a manually-dialed-in angle
    /// (a vertical cut can run any direction, not just axis-aligned). Ignored for Horizontal.
    /// Precision matters here more than for Horizontal: a vertical cut's exact line is often the
    /// reference other modifiers (e.g. a future Brace) use to know exactly where to work.
    /// </summary>
    public float RotationDegrees { get; set; }

    /// <summary>
    /// Plane position along its active (cutting) axis (mm), in world space — this modifier is a
    /// fully independent object, not attached to any mesh. For Horizontal this is height above
    /// the print bed's Z; <see cref="PositionX"/>/<see cref="PositionY"/> are the free in-plane
    /// position and don't affect the cut. For Vertical this is measured outward from the print
    /// bed's center along the current <see cref="RotationDegrees"/> direction — rotating the
    /// plane pivots around bed center, so the manually-dialed angle stays predictable/
    /// reproducible for whatever references it later.
    /// </summary>
    public float Offset { get; set; }

    /// <summary>
    /// Horizontal only: free X/Y position (mm, relative to bed center) — has no effect on the
    /// cut (only <see cref="Offset"/>'s Z height does), it's purely so the plane can be dragged
    /// out of the way to isolate/select a piece underneath it. Ignored for Vertical, which uses
    /// <see cref="PositionZ"/> for the equivalent free position instead.
    /// </summary>
    public float PositionX { get; set; }

    /// <summary>See <see cref="PositionX"/>.</summary>
    public float PositionY { get; set; }

    /// <summary>
    /// Vertical only: free height (mm, relative to bed center Z) — has no effect on the cut (a
    /// vertical plane cuts through every Z regardless of where it visually sits), it's purely so
    /// the plane can be dragged up/down out of the way. Ignored for Horizontal, which uses
    /// <see cref="PositionX"/>/<see cref="PositionY"/> for the equivalent free position instead.
    /// </summary>
    public float PositionZ { get; set; }

    /// <summary>
    /// Vertical only: free position (mm) along the in-plane direction perpendicular to
    /// <see cref="RotationDegrees"/> (i.e. sideways along the cut line itself) — has no effect on
    /// the cut, purely so the plane can be dragged out of the way. Together with
    /// <see cref="PositionZ"/> this gives Vertical the same 2 free + 1 meaningful (Offset)
    /// translation degrees of freedom Horizontal already has via PositionX/PositionY/Offset.
    /// Without this field, any drag component that wasn't purely along the normal or purely
    /// vertical had nowhere to go and was silently discarded every frame — visible as an axis
    /// that wouldn't move at all (0°, where the missing axis lines up with world Y) or as X and Y
    /// both collapsing onto the same diagonal (45°, where neither world axis lines up with either
    /// tracked direction). Ignored for Horizontal.
    /// </summary>
    public float PositionTangent { get; set; }

    /// <summary>When false, the plane's extent is limited to <see cref="SizeX"/>/<see cref="SizeY"/> instead of unbounded.</summary>
    public bool Infinite { get; set; } = true;

    /// <summary>Rectangular plane extent (mm) along its first in-plane axis, when <see cref="Infinite"/> is false.</summary>
    public float SizeX { get; set; } = 1500f;

    /// <summary>Rectangular plane extent (mm) along its second in-plane axis, when <see cref="Infinite"/> is false.</summary>
    public float SizeY { get; set; } = 1500f;
}
