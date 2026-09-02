namespace MassiveSlicer.Core.Models;

/// <summary>
/// Snapshot of the MILL right sidebar (operation, area tool, bit, planar axis, feeds).
/// Stored on <see cref="AppPreferences"/> so a .mass file reopens the same mill setup.
/// Null on workspaces saved before this type existed — leave the live mill panel alone.
/// </summary>
public sealed class MillSidebarSettings
{
    public string SelectedOperation { get; set; } = nameof(MillOperationKind.MultiAxisFinishing);
    public string AreaSelectTool { get; set; } = nameof(MillAreaSelectTool.WholeModel);
    public string? SelectedBitId { get; set; }
    public string? SelectedCuttingPresetName { get; set; }

    public string PlanarToolAxis { get; set; } = nameof(MillPlanarAxisKind.WorldNegZ);
    public double PlanarTiltDeg { get; set; }
    public double PlanarAzimuthDeg { get; set; }
    public double PlanarCustomX { get; set; }
    public double PlanarCustomY { get; set; }
    public double PlanarCustomZ { get; set; } = -1;
    public double PlanarCapturedX { get; set; }
    public double PlanarCapturedY { get; set; }
    public double PlanarCapturedZ { get; set; } = -1;

    public double ToolheadA { get; set; }
    public double ToolheadB { get; set; }
    public double ToolheadC { get; set; }

    public double ToolDiameterMm { get; set; } = 76.2;
    public bool BallEnd { get; set; }
    public double MaxDepthMm { get; set; }

    /// <summary>Mill path offset along the surface normal (mm). + out, − into the work.</summary>
    public double OffsetDistanceMm { get; set; }
    public int NumberOfDepthCuts { get; set; } = 1;
    public double StepoverMm { get; set; } = 4;
    public double StepdownMm { get; set; } = 2;
    public double FinishAllowanceMm { get; set; }
    public double PassAngleDeg { get; set; }
    public string PassStrategy { get; set; } = "Linear";
    public string CuttingDirection { get; set; } = "Both ways";
    public bool KeepToolWithinSurface { get; set; } = true;
    public bool ClipPath { get; set; }
    public bool EnableAntiGouging { get; set; }
    public double ApproachClearanceMm { get; set; } = 100;
    public double RapidZMm { get; set; } = 50;
    public double FeedRateMmMin { get; set; } = 10.44 * 60;
    /// <summary>Robot travel / rapid (mm/s). Separate from print Additive.TravelSpeed.</summary>
    public double TravelSpeedMmS { get; set; } = 80;
    public double SkimFeedMmS { get; set; } = 60;
    public double PlungeFeedMmMin { get; set; } = 400;
    public double SpindleRpm { get; set; } = 2088;
    public string SpindleDirection { get; set; } = "Clockwise";

    public string HeightmapPath { get; set; } = "";
    public double HeightScaleMm { get; set; } = 5;
    public bool InvertHeightmap { get; set; }
    public double DisplacementDistanceMm { get; set; } = 3;
    public double AnalysisToleranceMm { get; set; } = 0.1;
    public bool AutoReferenceFromTop { get; set; } = true;
    public double ReferencePlaneZ { get; set; }
    public bool AutoFootprint { get; set; } = true;
    public double FootprintOriginX { get; set; }
    public double FootprintOriginY { get; set; }
    public double FootprintWidthMm { get; set; } = 100;
    public double FootprintLengthMm { get; set; } = 100;

    public string HeaderTemplate { get; set; } = "";
    public string FooterTemplate { get; set; } = "";

    // AdaOne per-op cards
    public double FeedHeightMm { get; set; } = 5;
    public string CutoutMillingDirection { get; set; } = nameof(AdaMillingDirection.TowardSurface);
    public double CutoutCutDepthMm { get; set; } = 2;
    public double CutoutLayerHeightMm { get; set; } = 2;
    public string CutoutOrientationMode { get; set; } = nameof(AdaCutoutOrientationMode.Auto);
    public double DrillingBreakthroughMm { get; set; } = 5;
    public bool DrillingPeck { get; set; }
    public double DrillingPeckDepthMm { get; set; } = 2;
    public bool WaterfallMill { get; set; }
    public bool AllAroundMill { get; set; }
    public bool FlickEnds { get; set; }
    public bool ClearingInfill { get; set; } = true;
    public bool ContouringWaterfall { get; set; }
    public bool ContouringMaxDepthEnabled { get; set; }
    public double SwarfLeadDeg { get; set; }
    public double SwarfLeanDeg { get; set; }
    public bool StabilizeHeadRotation { get; set; } = true;
    public double SurfaceFinishingCutDepthMm { get; set; }
    public int MorphSteps { get; set; } = 8;
    public double LeadInMm { get; set; }
    public double LeadOutMm { get; set; }
    public string ToolCompensation { get; set; } = nameof(AdaToolCompensation.Off);
    public double TopHeightMm { get; set; }
    public double BottomHeightMm { get; set; }
}
