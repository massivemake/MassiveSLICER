using System.Numerics;

namespace MassiveSlicer.Core.Models;

/// <summary>AdaOne cutout milling direction (Toward surface when the proto value is 1).</summary>
public enum AdaMillingDirection
{
    FromSurface = 0,
    TowardSurface = 1,
}

/// <summary>AdaOne cutting-tool compensation (left/right of the programmed path).</summary>
public enum AdaToolCompensation
{
    Off,
    Left,
    Right,
}

/// <summary>AdaOne cutout orientation — auto, picked edges, or a sketch plane.</summary>
public enum AdaCutoutOrientationMode
{
    Auto,
    SelectedEdges,
    Sketch,
}

/// <summary>
/// AdaOne machining card (MachiningSettings + TravelParameters + per-op extras).
/// Bit geometry still lives on <see cref="MillBitTool"/>; this is the operation.
/// </summary>
public sealed class AdaMachiningSettings
{
    public MillOperationKind Operation { get; set; } = MillOperationKind.MultiAxisFinishing;

    public float StockToLeaveMm { get; set; }
    public int PassCount { get; set; } = 1;
    public float PassExtensionMm { get; set; }
    public string CuttingDirection { get; set; } = "Both ways";
    public bool AvoidGouging { get; set; }
    public bool ClipPath { get; set; }
    public bool KeepToolWithinSurface { get; set; } = true;
    public float OffsetDistanceMm { get; set; }

    public float ClearanceMm { get; set; } = 100;
    public float FeedHeightMm { get; set; } = 5;
    public float RetractHeightMm { get; set; } = 50;
    public float CuttingFeedMmS { get; set; } = 50;
    public float SkimFeedMmS { get; set; } = 60;
    public float TravelSpeedMmS { get; set; } = 80;
    public float PlungeFeedMmMin { get; set; } = 400;
    public float SpindleRpm { get; set; } = 10000;
    public SpindleDirection SpindleDirection { get; set; } = SpindleDirection.Clockwise;

    public float ToolDiameterMm { get; set; } = 6;
    public bool BallEnd { get; set; } = true;
    public float StepoverMm { get; set; } = 3;
    public float StepdownMm { get; set; } = 2;
    public float MaxDepthMm { get; set; }

    public float TopHeightMm { get; set; }
    public float BottomHeightMm { get; set; }
    public int AxialPassCount { get; set; } = 1;
    public int FinishingPassCount { get; set; }
    public float FinishingStepdownMm { get; set; }

    public AdaMillingDirection CutoutMillingDirection { get; set; } = AdaMillingDirection.TowardSurface;
    public float CutoutCutDepthMm { get; set; } = 2;
    public float CutoutLayerHeightMm { get; set; } = 2;
    public AdaCutoutOrientationMode CutoutOrientationMode { get; set; } = AdaCutoutOrientationMode.Auto;

    public float DrillingBreakthroughMm { get; set; } = 5;
    public bool DrillingPeck { get; set; }
    public float DrillingPeckDepthMm { get; set; } = 2;

    public bool WaterfallMill { get; set; }
    public bool AllAroundMill { get; set; }
    public bool FlickEnds { get; set; }
    public bool ClearingInfill { get; set; } = true;
    public string PlanarPattern { get; set; } = "Linear";
    public float PlanarLinearPassAngleDeg { get; set; }
    public Vector3 PlanarFacingNormal { get; set; } = Vector3.UnitZ;

    public bool ContouringWaterfall { get; set; }
    public bool ContouringMaxDepthEnabled { get; set; }
    public float ContouringPassAngleDeg { get; set; }

    public float SwarfLeadDeg { get; set; }
    public float SwarfLeanDeg { get; set; }

    public bool StabilizeHeadRotation { get; set; } = true;
    public float SurfaceFinishingCutDepthMm { get; set; }

    public int MorphSteps { get; set; } = 8;

    public float LeadInMm { get; set; }
    public float LeadOutMm { get; set; }
    public AdaToolCompensation ToolCompensation { get; set; } = AdaToolCompensation.Off;

    public MillSettings ToMillSettings() => new()
    {
        ToolDiameterMm    = ToolDiameterMm,
        ToolEnd           = BallEnd ? ToolEndType.Ball : ToolEndType.Flat,
        StepoverMm        = StepoverMm,
        StepdownMm        = StepdownMm,
        FinishAllowanceMm = StockToLeaveMm,
        OffsetDistanceMm  = OffsetDistanceMm,
        FeedRateMmMin     = CuttingFeedMmS * 60f,
        PlungeFeedMmMin   = PlungeFeedMmMin,
        RapidZMm          = RetractHeightMm,
        SpindleRpm        = SpindleRpm,
        MaxDepthMm        = MaxDepthMm > 0 ? MaxDepthMm : float.PositiveInfinity,
    };
}

/// <summary>Input mesh + AdaOne machining card for <c>AdaMillPlanner</c>.</summary>
public sealed class AdaMillRequest
{
    public required AdaMachiningSettings Settings { get; init; }
    public required IReadOnlyList<Vector3> Positions { get; init; }
    public required IReadOnlyList<Vector3> Normals { get; init; }
    public required IReadOnlyList<int> Indices { get; init; }
    public Vector3? ApproachAxis { get; init; }
    public IReadOnlyList<Vector3>? DrillHoles { get; init; }
}
