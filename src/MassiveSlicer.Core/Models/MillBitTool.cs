using System.Text.Json.Serialization;

namespace MassiveSlicer.Core.Models;

/// <summary>Cutter geometry family for a library bit.</summary>
public enum MillBitType
{
    BallEndMill,
    FlatEndMill,
    BullNose,
    Drill,
    Other,
}

/// <summary>Spindle rotation for cutting-data presets.</summary>
public enum SpindleDirection
{
    Clockwise,
    CounterClockwise,
}

/// <summary>
/// One cutting-data preset on a bit (CAM-style "Default" / material-specific sets).
/// Cutting feed is stored as mm/s in the library (matches Eidos-style UI); the mill panel
/// still consumes mm/min via <see cref="CuttingFeedMmMin"/>.
/// </summary>
public sealed class MillBitCuttingPreset
{
    public string Name { get; set; } = "Default";
    public double SpindleRpm { get; set; } = 12000;
    /// <summary>Surface speed (m/min).</summary>
    public double SurfaceSpeedMPerMin { get; set; }
    /// <summary>Cutting feed rate (mm/s) — primary library unit (matches shop CAM cards).</summary>
    public double CuttingFeedMmS { get; set; } = 50;

    /// <summary>Panel / toolpath unit (mm/min). Not serialized — derived from <see cref="CuttingFeedMmS"/>.</summary>
    [JsonIgnore]
    public double CuttingFeedMmMin
    {
        get => CuttingFeedMmS * 60.0;
        set => CuttingFeedMmS = value / 60.0;
    }

    /// <summary>Legacy JSON field from early mill_tools.json — maps into mm/s on load.</summary>
    [JsonPropertyName("CuttingFeedMmMin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double CuttingFeedMmMinLegacy
    {
        get => 0;
        set
        {
            if (value > 0)
                CuttingFeedMmS = value / 60.0;
        }
    }
    /// <summary>Feed per tooth (mm).</summary>
    public double FeedPerToothMm { get; set; }
    public double PlungeFeedMmMin { get; set; } = 1000;
    public double StepoverMm { get; set; } = 3;
    public double StepdownMm { get; set; } = 2;
    public double FinishAllowanceMm { get; set; } = 0.3;
    public double RapidZMm { get; set; } = 50;
    public SpindleDirection SpindleDirection { get; set; } = SpindleDirection.Clockwise;
}

/// <summary>One holder stack segment (height + top/bottom diameters).</summary>
public sealed class MillBitHolderSegment
{
    public double HeightMm { get; set; }
    public double TopDiameterMm { get; set; }
    public double BottomDiameterMm { get; set; }
}

/// <summary>
/// A spindle bit / end-mill entry in the mill tool library (BITS step).
/// Geometry + holder stack + one or more cutting-data presets.
/// </summary>
public sealed class MillBitTool
{
    /// <summary>Stable id for the LFAM 3 face mill currently on the spindle.</summary>
    public const string DefaultSpindleBitId = "lfam3-ap90-flat-3in";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Display / list name.</summary>
    public string Name { get; set; } = "New bit";
    /// <summary>Identifier string (e.g. AP90 FLAT 3in End Mill).</summary>
    public string Identifier { get; set; } = "";
    /// <summary>KUKA / library tool number.</summary>
    public int ToolNumber { get; set; }
    public MillBitType Type { get; set; } = MillBitType.BallEndMill;
    public double DiameterMm { get; set; } = 6;
    public double ShaftDiameterMm { get; set; }
    /// <summary>Corner / ball radius (mm). Ball end typically = diameter/2.</summary>
    public double CornerRadiusMm { get; set; }
    /// <summary>Total cutter length (mm).</summary>
    public double TotalLengthMm { get; set; }
    public double FluteLengthMm { get; set; }
    public double ShoulderLengthMm { get; set; }
    public double LengthBelowHolderMm { get; set; }
    public int FluteCount { get; set; } = 2;
    public double MaxDepthMm { get; set; }
    /// <summary>When true, this bit is preferred on cold start (mounted on spindle).</summary>
    public bool IsDefaultSpindleBit { get; set; }

    /// <summary>
    /// Draw a preview cylinder on the LFAM 3 spindle, origin at the
    /// <c>SpindleBit</c> disc centre, length along the disc-face normal.
    /// </summary>
    public bool ShowSpindleCylinder { get; set; } = true;

    /// <summary>
    /// Cylinder stick-out (mm) from the disc centre along the face normal. 0 = use
    /// <see cref="TotalLengthMm"/>, then <see cref="FluteLengthMm"/>, then 50 mm.
    /// </summary>
    public double CylinderLengthMm { get; set; }

    /// <summary>Reverse the cylinder so it grows from the opposite disc face.</summary>
    public bool CylinderFlip { get; set; }

    /// <summary>Resolved preview length in mm (never 0).</summary>
    [JsonIgnore]
    public double EffectiveCylinderLengthMm
    {
        get
        {
            if (CylinderLengthMm > 0.05) return CylinderLengthMm;
            if (TotalLengthMm > 0.05) return TotalLengthMm;
            if (FluteLengthMm > 0.05) return FluteLengthMm;
            return 50;
        }
    }
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;

    public List<MillBitHolderSegment> HolderSegments { get; set; } = [];
    public List<MillBitCuttingPreset> CuttingPresets { get; set; } = [new()];

    public string TypeDisplayName => Type switch
    {
        MillBitType.BallEndMill => "Ball end mill",
        MillBitType.FlatEndMill => "Flat end",
        MillBitType.BullNose    => "Bull nose",
        MillBitType.Drill       => "Drill",
        _                       => "Other",
    };

    public bool IsBallEnd => Type is MillBitType.BallEndMill;

    public MillBitCuttingPreset DefaultPreset =>
        CuttingPresets is { Count: > 0 } ? CuttingPresets[0] : new MillBitCuttingPreset();

    /// <summary>AP90 3″ face mill — currently mounted on LFAM 3 spindle (from shop CAM card).</summary>
    public static MillBitTool CreateLfam3DefaultFlat3In() => new()
    {
        Id = DefaultSpindleBitId,
        Name = "Flat end D76.2 (AP90 FLAT 3in End Mill)",
        Identifier = "AP90 FLAT 3in End Mill",
        ToolNumber = 10,
        Type = MillBitType.FlatEndMill,
        DiameterMm = 76.20,
        ShaftDiameterMm = 76.20,
        CornerRadiusMm = 0,
        TotalLengthMm = 0,
        FluteLengthMm = 6.25,
        ShoulderLengthMm = 0,
        LengthBelowHolderMm = 0,
        FluteCount = 1,
        MaxDepthMm = 0,
        IsDefaultSpindleBit = true,
        HolderSegments =
        [
            new() { HeightMm = 0, TopDiameterMm = 0, BottomDiameterMm = 0 },
        ],
        CuttingPresets =
        [
            new()
            {
                Name = "Default",
                SpindleRpm = 2088,
                SurfaceSpeedMPerMin = 499.845,
                CuttingFeedMmS = 10.440,
                FeedPerToothMm = 0.300,
                PlungeFeedMmMin = 400,
                StepoverMm = 20,
                StepdownMm = 2,
                FinishAllowanceMm = 0.3,
                RapidZMm = 50,
                SpindleDirection = SpindleDirection.Clockwise,
            },
        ],
    };

    public static List<MillBitTool> CreateSeedLibrary() =>
    [
        CreateLfam3DefaultFlat3In(),
        new()
        {
            Name = "Ball end D16 (sphere)",
            Identifier = "Ball end D16",
            ToolNumber = 16,
            Type = MillBitType.BallEndMill,
            DiameterMm = 16,
            ShaftDiameterMm = 16,
            CornerRadiusMm = 8,
            TotalLengthMm = 80,
            FluteLengthMm = 30,
            FluteCount = 2,
            CuttingPresets =
            [
                new()
                {
                    Name = "Default",
                    SpindleRpm = 8000,
                    CuttingFeedMmS = 2500 / 60.0,
                    PlungeFeedMmMin = 800,
                    StepoverMm = 4,
                    StepdownMm = 2,
                },
            ],
        },
        new()
        {
            Name = "Ball end D6 (sphere)",
            Identifier = "Ball end D6",
            ToolNumber = 6,
            Type = MillBitType.BallEndMill,
            DiameterMm = 6,
            ShaftDiameterMm = 6,
            CornerRadiusMm = 3,
            TotalLengthMm = 50,
            FluteLengthMm = 20,
            FluteCount = 2,
            CuttingPresets =
            [
                new()
                {
                    Name = "Default",
                    SpindleRpm = 12000,
                    CuttingFeedMmS = 50,
                    PlungeFeedMmMin = 1000,
                    StepoverMm = 1.5,
                    StepdownMm = 1,
                    FinishAllowanceMm = 0.2,
                },
            ],
        },
        new()
        {
            Name = "Ball end D4 (sphere)",
            Identifier = "Ball end D4",
            ToolNumber = 4,
            Type = MillBitType.BallEndMill,
            DiameterMm = 4,
            ShaftDiameterMm = 4,
            CornerRadiusMm = 2,
            TotalLengthMm = 40,
            FluteLengthMm = 15,
            FluteCount = 2,
            CuttingPresets =
            [
                new()
                {
                    Name = "Default",
                    SpindleRpm = 16000,
                    CuttingFeedMmS = 2000 / 60.0,
                    PlungeFeedMmMin = 600,
                    StepoverMm = 0.8,
                    StepdownMm = 0.5,
                    FinishAllowanceMm = 0.15,
                },
            ],
        },
    ];
}
