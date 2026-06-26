namespace MassiveSlicer.Core.Models;

/// <summary>Cutter end geometry for relief milling.</summary>
public enum ToolEndType
{
    /// <summary>Ball-nose: anti-gouge offset adds a spherical cap term.</summary>
    Ball,
    /// <summary>Flat end-mill: anti-gouge offset is a plain max-filter (no cap).</summary>
    Flat,
}

/// <summary>
/// Parameters for <see cref="Slicing.ReliefMillSlicer"/>. Kept separate from <c>SliceSettings</c>
/// so additive (wave/infill/flow) fields never leak into the milling path and vice-versa.
/// All distances in mm; feeds in mm/min.
/// </summary>
public sealed class MillSettings
{
    public float ToolDiameterMm { get; init; } = 6f;
    public ToolEndType ToolEnd { get; init; } = ToolEndType.Ball;

    /// <summary>Finish raster spacing (mm) — sets toolpath fidelity (NOT the image resolution).</summary>
    public float StepoverMm { get; init; } = 3f;
    /// <summary>Roughing axial step (mm) per Z-level.</summary>
    public float StepdownMm { get; init; } = 2f;
    /// <summary>Stock left above the final surface during roughing; the finish pass removes it.</summary>
    public float FinishAllowanceMm { get; init; } = 0.3f;

    public float FeedRateMmMin { get; init; } = 3000f;
    public float PlungeFeedMmMin { get; init; } = 1000f;

    /// <summary>Safe retract height above <c>ReferencePlaneZ</c> for rapids (mm).</summary>
    public float RapidZMm { get; init; } = 50f;
    public float SpindleRpm { get; init; } = 12000f;

    /// <summary>Hard floor: never cut deeper than this many mm below the reference plane
    /// (guards against carving through the blank). Default unlimited.</summary>
    public float MaxDepthMm { get; init; } = float.PositiveInfinity;

    public float ToolRadiusMm => ToolDiameterMm * 0.5f;
}
