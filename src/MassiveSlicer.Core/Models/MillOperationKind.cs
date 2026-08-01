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
/// Subtractive strategy selected in the Mill sidebar (step 2 OPERATION).
/// Each kind will grow its own parameter set and toolpath generator over time.
/// </summary>
public enum MillOperationKind
{
    /// <summary>Surface-following finish; tool axis tracks the surface normal.</summary>
    MultiAxisFinishing,

    /// <summary>Hole / pocket drilling (plunge or peck cycles).</summary>
    Drilling,

    /// <summary>Flat facing pass across a planar region.</summary>
    PlanarFacing,

    /// <summary>2.5D area clear / pocket roughing on a plane.</summary>
    PlanarClearing,

    /// <summary>Profile cutout along a closed boundary.</summary>
    Cutout,

    /// <summary>Contour / waterline finish along Z levels or a surface silhouette.</summary>
    Contouring,

    /// <summary>Swarf: side-of-tool contact along ruled / lofted walls.</summary>
    Swarf,
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
            Description = "Surface-following finish pass — tool axis tracks the surface normal (existing multi-axis path).",
        },
        new()
        {
            Kind = MillOperationKind.Drilling,
            DisplayName = "Drilling",
            Icon = "mdi-screw-flat-top",
            Description = "Hole and pocket drilling with plunge / peck cycles. Parameters coming next.",
        },
        new()
        {
            Kind = MillOperationKind.PlanarFacing,
            DisplayName = "Planar facing",
            Icon = "mdi-arrow-collapse-down",
            Description = "Face a planar region flat. Parameters coming next.",
        },
        new()
        {
            Kind = MillOperationKind.PlanarClearing,
            DisplayName = "Planar clearing",
            Icon = "mdi-arrow-expand-all",
            Description = "2.5D area clear / pocket roughing on a plane. Parameters coming next.",
        },
        new()
        {
            Kind = MillOperationKind.Cutout,
            DisplayName = "Cutout",
            Icon = "mdi-content-cut",
            Description = "Profile cutout along a closed boundary. Parameters coming next.",
        },
        new()
        {
            Kind = MillOperationKind.Contouring,
            DisplayName = "Contouring",
            Icon = "mdi-vector-polyline",
            Description = "Contour / waterline finish along Z levels or a surface silhouette. Parameters coming next.",
        },
        new()
        {
            Kind = MillOperationKind.Swarf,
            DisplayName = "Swarf",
            Icon = "mdi-rotate-3d-variant",
            Description = "Side-of-tool contact along ruled or lofted walls. Parameters coming next.",
        },
    ];

    public static MillOperationInfo? Find(MillOperationKind kind)
        => Catalog.FirstOrDefault(c => c.Kind == kind);
}
