using System.Numerics;

namespace MassiveSlicer.Core.IO;

/// <summary>XYZABC pose in print-bed BASE (same as massivedrive.job/v1 segment from/to).</summary>
public readonly record struct MassiveDrivePose(
    double X, double Y, double Z, double A, double B, double C)
{
    public Vector3 Xyz => new((float)X, (float)Y, (float)Z);

    public MassiveDrivePose WithXyz(Vector3 p) => this with
    {
        X = Math.Round(p.X, 3),
        Y = Math.Round(p.Y, 3),
        Z = Math.Round(p.Z, 3),
    };

    public Dictionary<string, double> ToDict() => new()
    {
        ["x"] = Math.Round(X, 3),
        ["y"] = Math.Round(Y, 3),
        ["z"] = Math.Round(Z, 3),
        ["a"] = Math.Round(A, 3),
        ["b"] = Math.Round(B, 3),
        ["c"] = Math.Round(C, 3),
    };
}

/// <summary>
/// One Drive path segment. Shared by v1 JSON export and v2 <c>segments.bin</c>.
/// </summary>
public sealed class MassiveDriveSegment
{
    public int Index { get; set; }
    public string Kind { get; set; } = "print";
    public int Layer { get; set; }
    public MassiveDrivePose From { get; set; }
    public MassiveDrivePose To { get; set; }
    public double SpeedMmS { get; set; }
    public double FlowScale { get; set; }
    public bool Reverse { get; set; }
    public bool LayerChange { get; set; }
    public int RpmPct { get; set; }
    public bool Wipe { get; set; }
    public bool ResumeRamp { get; set; }
    public bool PreTravelStart { get; set; }
    public bool PostTravelEnd { get; set; }
    public string? Comment { get; set; }

    public Dictionary<string, object?> ToV1Dict()
    {
        var seg = new Dictionary<string, object?>
        {
            ["i"] = Index,
            ["kind"] = Kind,
            ["layer"] = Layer,
            ["from"] = From.ToDict(),
            ["to"] = To.ToDict(),
            ["speed_mm_s"] = Math.Round(SpeedMmS, 3),
            ["flow_scale"] = Math.Round(FlowScale, 4),
        };
        if (Kind == "travel")
            seg["reverse"] = Reverse;
        if (LayerChange)
            seg["layer_change"] = true;
        var meta = new Dictionary<string, object?>();
        if (Wipe) meta["wipe"] = true;
        if (ResumeRamp) meta["resume_ramp"] = true;
        if (PreTravelStart) meta["pre_travel_start"] = true;
        if (PostTravelEnd)
        {
            meta["post_travel_start"] = true;
            meta["post_travel_end"] = true;
        }
        if (!string.IsNullOrWhiteSpace(Comment))
            meta["comment"] = Comment;
        if (meta.Count > 0)
            seg["meta"] = meta;
        if (Kind == "print" && !Wipe && RpmPct > 0)
            seg["rpm_pct"] = RpmPct;
        return seg;
    }
}

/// <summary>Built Drive job (typed segments + v1-compatible envelope fields).</summary>
public sealed class MassiveDriveJobBuild
{
    public required string JobId { get; init; }
    public required string CellId { get; init; }
    public required string Name { get; init; }
    public required List<MassiveDriveSegment> Segments { get; init; }
    public required Dictionary<string, object?> Source { get; init; }
    public required Dictionary<string, object?> Defaults { get; init; }
    public required Dictionary<string, int> Frames { get; init; }
    public required Dictionary<string, object?> Meta { get; init; }
    public required Dictionary<string, string> Units { get; init; }

    public Dictionary<string, object?> ToV1Dict()
    {
        var dict = Envelope("massivedrive.job/v1");
        dict["segments"] = Segments.ConvertAll(s => s.ToV1Dict());
        return dict;
    }

    public Dictionary<string, object?> ToV2Manifest(int segmentCount, int stride)
    {
        var dict = Envelope(MassiveDriveJobV2.Format);
        dict["files"] = new Dictionary<string, string>
        {
            ["segments"] = MassiveDriveJobV2.SegmentsFileName,
            ["preview"] = MassiveDriveJobV2.PreviewFileName,
            ["summary"] = MassiveDriveJobV2.SummaryFileName,
        };
        dict["segment_file"] = new Dictionary<string, object?>
        {
            ["name"] = MassiveDriveJobV2.SegmentsFileName,
            ["count"] = segmentCount,
            ["stride"] = stride,
            ["encoding"] = "le-fixed",
            ["magic"] = MassiveDriveJobV2.SegmentsMagicAscii,
        };
        return dict;
    }

    Dictionary<string, object?> Envelope(string format) => new()
    {
        ["format"] = format,
        ["cell_id"] = CellId,
        ["job_id"] = JobId,
        ["name"] = Name,
        ["source"] = Source,
        ["units"] = Units,
        ["frames"] = Frames,
        ["defaults"] = Defaults,
        ["meta"] = Meta,
    };
}
