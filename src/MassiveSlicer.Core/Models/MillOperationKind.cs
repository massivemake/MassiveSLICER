namespace MassiveSlicer.Core.Models;

/// <summary>
/// How the user picks the region of the model to machine under OPERATION → SELECT AREA.
/// </summary>
public enum MillAreaSelectTool
{
    /// <summary>Use the entire selected model / stock mesh.</summary>
    WholeModel,
    /// <summary>Click mesh faces in the viewport.</summary>
    Face,
    /// <summary>Drag a rectangular marquee over the model.</summary>
    Box,
    /// <summary>Freehand lasso over the model.</summary>
    Lasso,
    /// <summary>Brush-paint faces on the model.</summary>
    Brush,
}

/// <summary>
/// AdaOne / Eidos subtractive strategy selected in MILL → 2 OPERATION.
/// Fresh baseline — not the old PlanarCut / relief mill catalog.
/// </summary>
public enum MillOperationKind
{
    /// <summary>AdaOne SURFACE_FINISHING: tool axis tracks the surface normal.</summary>
    MultiAxisFinishing,

    /// <summary>AdaOne drilling: plunge or peck through selected holes.</summary>
    Drilling,

    /// <summary>AdaOne planar facing: raster a plane.</summary>
    PlanarFacing,

    /// <summary>AdaOne HORIZONTAL_CLEARING: 2.5D area clear / pocket roughing.</summary>
    PlanarClearing,

    /// <summary>AdaOne cutout: closed outline, then step deeper each pass.</summary>
    Cutout,

    /// <summary>AdaOne contouring: waterline loops at each Z.</summary>
    Contouring,

    /// <summary>AdaOne swarf: side-of-tool contact on a guide surface.</summary>
    Swarf,

    /// <summary>AdaOne morph: blend a top rail into a bottom rail.</summary>
    Morph,
}

/// <summary>UI catalog entry for a <see cref="MillOperationKind"/>.</summary>
public sealed class MillOperationInfo
{
    public required MillOperationKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public required string Icon { get; init; }
    public required string Description { get; init; }

    public static IReadOnlyList<MillOperationInfo> Catalog { get; } =
    [
        new()
        {
            Kind = MillOperationKind.MultiAxisFinishing,
            DisplayName = "Multi-axis finishing",
            Icon = "mdi-axis-arrow",
            Description = "AdaOne multi-axis finishing (SURFACE_FINISHING): tool axis tracks the surface normal. Stabilize head rotation smooths ABC between moves.",
        },
        new()
        {
            Kind = MillOperationKind.Drilling,
            DisplayName = "Drilling",
            Icon = "mdi-screw-flat-top",
            Description = "AdaOne drilling: plunge or peck cycle through selected holes, with clearance / feed / retract and breakthrough.",
        },
        new()
        {
            Kind = MillOperationKind.PlanarFacing,
            DisplayName = "Planar facing",
            Icon = "mdi-arrow-collapse-down",
            Description = "AdaOne planar facing: raster a plane. Set the plane (TOOL AXIS) and surface; optional axial pass stack.",
        },
        new()
        {
            Kind = MillOperationKind.PlanarClearing,
            DisplayName = "Planar clearing",
            Icon = "mdi-arrow-expand-all",
            Description = "AdaOne planar clearing (HORIZONTAL_CLEARING): 2.5D area clear with waterfall / all-around / flick-ends / infill.",
        },
        new()
        {
            Kind = MillOperationKind.Cutout,
            DisplayName = "Cutout",
            Icon = "mdi-content-cut",
            Description = "AdaOne cutout: one closed outline, then step deeper each pass (cut depth / layer height). Toward-surface or from-surface.",
        },
        new()
        {
            Kind = MillOperationKind.Contouring,
            DisplayName = "Contouring",
            Icon = "mdi-vector-polyline",
            Description = "AdaOne contouring: waterline loops at each Z. Waterfall links levels; max-depth optional.",
        },
        new()
        {
            Kind = MillOperationKind.Swarf,
            DisplayName = "Swarf",
            Icon = "mdi-rotate-3d-variant",
            Description = "AdaOne swarf: side-of-tool contact on a guide surface, with lead and lean.",
        },
        new()
        {
            Kind = MillOperationKind.Morph,
            DisplayName = "Morph",
            Icon = "mdi-set-merge",
            Description = "AdaOne morph: blend a top rail into a bottom rail over N steps.",
        },
    ];

    public static MillOperationInfo? Find(MillOperationKind kind)
        => Catalog.FirstOrDefault(c => c.Kind == kind);
}
