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
/// MassiveSLICER / Eidos subtractive strategy selected in MILL → 2 OPERATION.
/// Fresh baseline — not the old PlanarCut / relief mill catalog.
/// </summary>
public enum MillOperationKind
{
    /// <summary>MassiveSLICER SURFACE_FINISHING: tool axis tracks the surface normal.</summary>
    MultiAxisFinishing,

    /// <summary>MassiveSLICER drilling: plunge or peck through selected holes.</summary>
    Drilling,

    /// <summary>MassiveSLICER planar facing: raster a plane.</summary>
    PlanarFacing,

    /// <summary>Projection finish from a helper shape. Kept the old name so saved mills still open.</summary>
    PlanarClearing,

    /// <summary>Same operation as <see cref="PlanarClearing"/>. The mill menu calls it Projection.</summary>
    Projection = PlanarClearing,

    /// <summary>MassiveSLICER cutout: closed outline, then step deeper each pass.</summary>
    Cutout,

    /// <summary>MassiveSLICER contouring: waterline loops at each Z.</summary>
    Contouring,

    /// <summary>MassiveSLICER swarf: side-of-tool contact on a guide surface.</summary>
    Swarf,

    /// <summary>MassiveSLICER morph: blend a top rail into a bottom rail.</summary>
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
            Description = "Geodesic offsets along the surface. The tool axis follows the surface normal.",
        },
        new()
        {
            Kind = MillOperationKind.Drilling,
            DisplayName = "Drilling",
            Icon = "mdi-screw-flat-top",
            Description = "MassiveSLICER drilling: plunge or peck cycle through selected holes, with clearance / feed / retract and breakthrough.",
        },
        new()
        {
            Kind = MillOperationKind.PlanarFacing,
            DisplayName = "Planar facing",
            Icon = "mdi-arrow-collapse-down",
            Description = "MassiveSLICER planar facing: raster a plane. Set the plane (TOOL AXIS) and surface; optional axial pass stack.",
        },
        new()
        {
            Kind = MillOperationKind.PlanarClearing,
            DisplayName = "Projection",
            Icon = "mdi-cylinder",
            Description = "Project a finish from a helper cylinder, sphere, or cube onto the part. Blend the shape angle with the surface normal.",
        },
        new()
        {
            Kind = MillOperationKind.Cutout,
            DisplayName = "Cutout",
            Icon = "mdi-content-cut",
            Description = "MassiveSLICER cutout: one closed outline, then step deeper each pass (cut depth / layer height). Toward-surface or from-surface.",
        },
        new()
        {
            Kind = MillOperationKind.Contouring,
            DisplayName = "Contouring",
            Icon = "mdi-vector-polyline",
            Description = "MassiveSLICER contouring: waterline loops at each Z. Waterfall links levels; max-depth optional.",
        },
        new()
        {
            Kind = MillOperationKind.Swarf,
            DisplayName = "Swarf",
            Icon = "mdi-rotate-3d-variant",
            Description = "MassiveSLICER swarf: side-of-tool contact on a guide surface, with lead and lean.",
        },
        new()
        {
            Kind = MillOperationKind.Morph,
            DisplayName = "Morph",
            Icon = "mdi-set-merge",
            Description = "MassiveSLICER morph: blend a top rail into a bottom rail over N steps.",
        },
    ];

    public static MillOperationInfo? Find(MillOperationKind kind)
        => Catalog.FirstOrDefault(c => c.Kind == kind);
}
