using MassiveSlicer.Core.Models;

namespace MassiveSlicer.ViewModels;

/// <summary>One row in the mill TOOL AXIS dropdown (planar facing / clearing).</summary>
public sealed record MillPlanarAxisOption(MillPlanarAxisKind Kind, string Label)
{
    public override string ToString() => Label;

    public static IReadOnlyList<MillPlanarAxisOption> All { get; } =
    [
        new(MillPlanarAxisKind.WorldNegZ,   "World -Z (down)"),
        new(MillPlanarAxisKind.WorldPosZ,   "World +Z (up)"),
        new(MillPlanarAxisKind.WorldPosX,   "World +X"),
        new(MillPlanarAxisKind.WorldNegX,   "World -X"),
        new(MillPlanarAxisKind.WorldPosY,   "World +Y"),
        new(MillPlanarAxisKind.WorldNegY,   "World -Y"),
        new(MillPlanarAxisKind.PaintedFace, "Painted area"),
        new(MillPlanarAxisKind.Camera,      "Camera view"),
        new(MillPlanarAxisKind.Custom,      "Custom XYZ"),
    ];

    public static MillPlanarAxisOption Default => All[0];

    public static MillPlanarAxisOption Find(MillPlanarAxisKind kind)
        => All.FirstOrDefault(o => o.Kind == kind) ?? Default;
}
