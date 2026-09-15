using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Export settings for MassiveDRIVE job packages (<c>massivedrive.job/v1</c> JSON
/// and <c>massivedrive.job/v2</c> on-disk). Drive executes the path over RSI +
/// ClearCore; no print KRL is loaded.
/// </summary>
public sealed record MassiveDriveExportSettings
{
    public required string Name { get; init; }
    public string CellId { get; init; } = "lfam3";
    public string? JobId { get; init; }
    public int Tool { get; init; } = 1;
    public int Base { get; init; } = 1;
    /// <summary>Print speed mm/s (default from app prefs / export).</summary>
    public float PrintSpeedMmS { get; init; } = 50f;
    /// <summary>Travel speed mm/s.</summary>
    public float TravelSpeedMmS { get; init; } = 120f;
    /// <summary>Wipe hop mm/s (WIPE card). 0 = <see cref="TravelSpeedMmS"/>.</summary>
    public float WipeSpeedMmS { get; init; }
    public float ReverseMs { get; init; } = 200f;
    public float ReversePercent { get; init; } = 40f;
    /// <summary>Slicer extrusion motor % (ClearCore SPEED). 0 = omit / Drive maps from mm/s.</summary>
    public float ExtrusionRpmPercent { get; init; }
    /// <summary>Layer 0 extrusion motor %. 0 = same as <see cref="ExtrusionRpmPercent"/>.</summary>
    public float FirstLayerRpmPercent { get; init; }
    /// <summary>Layer 0 print speed mm/s. 0 = <see cref="PrintSpeedMmS"/>.</summary>
    public float FirstLayerSpeedMmS { get; init; }
    public float BeadWidthMm { get; init; }
    public float LayerHeightMm { get; init; }
    public float FlowRate { get; init; }
    /// <summary>When true, travel segments request suck-back reverse on Drive.</summary>
    public bool TravelReverse { get; init; } = true;
    /// <summary>ATV setpoint for mill jobs. Drive rejects mill packages at 0.</summary>
    public float SpindleRpm { get; init; }
    /// <summary>When true, ABC uses mill/T12 convention (cutter Z into the work).</summary>
    public bool MillOrientation { get; init; }
    /// <summary>
    /// Package XYZ is print-bed BASE (same as SRC). Drive adds <see cref="BedOrigin"/>
    /// for RSI / $POS_ACT. Do not store world Z (~919) in the job file.
    /// </summary>
    public bool AbsolutePath { get; init; } = true;
    /// <summary>Safe-Z clearance (mm) above first mill pose for the sync lead-in.</summary>
    public float ApproachClearanceMm { get; init; } = 80f;
    /// <summary>Same as KRL: stored toolpath → current world.</summary>
    public System.Numerics.Matrix4x4 NodeWorldTransform { get; init; } = System.Numerics.Matrix4x4.Identity;
    public System.Numerics.Vector3 NodeOrigin { get; init; }
    public System.Numerics.Vector3 RobrootWorldPos { get; init; }
    public System.Numerics.Vector3 BaseDataOffset { get; init; }
    /// <summary>Scene bed Z (mm). Same lift as <see cref="KrlExporter.WorldToBase"/>.</summary>
    public float SliceBedWorldZ { get; init; } = float.NaN;
    /// <summary>Print-bed 0,0,0 in scene mm (Drive adds this to BASE XYZ at run).</summary>
    public Vector3 BedOrigin { get; init; }
    /// <summary>
    /// Toolhead orientation offsets (deg) applied in the approach frame — same as KRL export
    /// <see cref="KrlExportSettings.ToolheadOffsetA"/> / B / C (from additive settings / cell default).
    /// </summary>
    public float ToolheadOffsetA { get; init; }
    public float ToolheadOffsetB { get; init; }
    public float ToolheadOffsetC { get; init; }
    /// <summary>Optional workspace / provenance fields.</summary>
    public string? WorkspacePath { get; init; }
    public string? SourceNote { get; init; }
}

/// <summary>
/// Builds MassiveDRIVE job packages from a <see cref="Toolpath"/>.
/// </summary>
public static class MassiveDriveJobExporter
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Export toolpath to typed segments + envelope (v1 JSON or v2 directory).</summary>
    public static MassiveDriveJobBuild Export(Toolpath toolpath, MassiveDriveExportSettings s)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        ArgumentNullException.ThrowIfNull(s);

        if (!s.MillOrientation)
            toolpath = TravelMarkerPostProcessor.Apply(toolpath);

        var segments = new List<MassiveDriveSegment>();
        int i = 0;
        int prevLayer = -1;
        var rpmInputs = RpmInputs(s);

        foreach (var layer in toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                var kind = move.Kind switch
                {
                    MoveKind.Extrude => "print",
                    MoveKind.Travel => "travel",
                    MoveKind.Mill => "mill",
                    _ => "print",
                };

                // Same as KRL EffectivePrintSpeedMps: layer 0 uses FirstLayerSpeedMmS
                // (independent of first-layer RPM). Do not stamp job PrintSpeed on layer 0.
                float printMmS = s.PrintSpeedMmS;
                KrlExportSettings? layerS = null;
                if (kind != "mill" && rpmInputs is not null)
                {
                    layerS = ToolpathRpm.ForLayer(rpmInputs, layer.Index);
                    if (layerS.PrintSpeedMps > 1e-6f)
                        printMmS = layerS.PrintSpeedMps * 1000f;
                }
                else if (kind == "print" && layer.Index == 0 && s.FirstLayerSpeedMmS > 0.05f)
                    printMmS = s.FirstLayerSpeedMmS;

                float speed;
                if (move.IsWipe)
                {
                    // WIPE card mm/s (shop 600). Not print / first-layer, not WipeRpmScale.
                    float wipeMmS = s.WipeSpeedMmS > 0.05f ? s.WipeSpeedMmS : s.TravelSpeedMmS;
                    if (move.TravelSpeedMps is { } wtsm && wtsm > 1e-6f)
                        wipeMmS = wtsm * 1000f;
                    speed = Math.Max(wipeMmS, 1f);
                }
                else if (kind == "travel")
                    speed = move.TravelSpeedMps is { } tsm ? tsm * 1000f : s.TravelSpeedMmS;
                else if (kind == "mill")
                    speed = s.PrintSpeedMmS;
                else
                    speed = printMmS * Math.Max(0.05f, move.PrintSpeedScale);

                if (move.IsResumeRamp)
                    speed = Math.Max(speed * Math.Max(0.05f, move.ResumeSpeedScale), 1f);

                bool layerChange = move.IsLayerChange || (prevLayer >= 0 && layer.Index != prevLayer && kind == "print");
                prevLayer = layer.Index;

                var from = Pose(move.From, layer.PlaneNormal, move.Normal, move.TcpYawDeg, s);
                var to = Pose(move.To, layer.PlaneNormal, move.Normal, move.TcpYawDeg, s);

                string? comment = null;
                if (move.IsPreTravelStart && move.IsPostTravelEnd)
                    comment = TravelMarkerPostProcessor.PreTravelStartComment + " " + TravelMarkerPostProcessor.PostTravelStartComment;
                else if (move.IsPreTravelStart)
                    comment = TravelMarkerPostProcessor.PreTravelStartComment;
                else if (move.IsPostTravelEnd)
                    comment = TravelMarkerPostProcessor.PostTravelStartComment;

                int rpmPct = 0;
                if (kind == "print" && !move.IsWipe && layerS is not null)
                {
                    float rpm = ToolpathRpm.SteppedPercent(ToolpathRpm.MovePercent(move, layerS));
                    if (rpm > 0f)
                        rpmPct = (int)rpm;
                }

                segments.Add(new MassiveDriveSegment
                {
                    Index = i,
                    Kind = kind,
                    Layer = layer.Index,
                    From = from,
                    To = to,
                    SpeedMmS = Math.Round(speed, 3),
                    FlowScale = Math.Round(
                        (double)(move.IsWipe ? move.WipeRpmScale
                            : move.IsResumeRamp ? move.ResumeRpmScale
                            : move.PrintSpeedScale * Math.Max(0.05f, move.HeightScale)),
                        4),
                    Reverse = kind == "travel" && s.TravelReverse && !s.MillOrientation && !move.IsZHop,
                    LayerChange = layerChange,
                    RpmPct = rpmPct,
                    Wipe = move.IsWipe,
                    ResumeRamp = move.IsResumeRamp,
                    PreTravelStart = move.IsPreTravelStart,
                    PostTravelEnd = move.IsPostTravelEnd,
                    Comment = comment,
                });
                i++;
            }
        }

        if (segments.Count == 0)
            throw new InvalidOperationException("Toolpath has no exportable print/travel moves.");

        StitchContinuous(segments, s);

        var jobId = string.IsNullOrWhiteSpace(s.JobId)
            ? Guid.NewGuid().ToString("N")[..12]
            : s.JobId!;

        var source = new Dictionary<string, object?>
        {
            ["app"] = "MassiveSLICER",
            ["exported_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(s.WorkspacePath))
            source["workspace"] = s.WorkspacePath;
        if (!string.IsNullOrWhiteSpace(s.SourceNote))
            source["note"] = s.SourceNote;

        var defaults = new Dictionary<string, object?>
        {
            ["print_speed_mm_s"] = s.PrintSpeedMmS,
            ["travel_speed_mm_s"] = s.TravelSpeedMmS,
            ["reverse_ms"] = s.ReverseMs,
            ["reverse_percent"] = s.ReversePercent,
        };
        if (!s.MillOrientation)
        {
            float wipeDef = s.WipeSpeedMmS > 0.05f ? s.WipeSpeedMmS : s.TravelSpeedMmS;
            defaults["wipe_speed_mm_s"] = Math.Round((double)wipeDef, 3);
        }
        if (rpmInputs is not null)
        {
            defaults["print_rpm_pct"] = (int)ToolpathRpm.SteppedPercent(ToolpathRpm.BasePercent(rpmInputs));
            defaults["first_layer_rpm_pct"] = (int)ToolpathRpm.SteppedPercent(
                ToolpathRpm.BasePercent(ToolpathRpm.ForLayer(rpmInputs, 0)));
        }
        if (s.FirstLayerSpeedMmS > 0.05f)
            defaults["first_layer_speed_mm_s"] = Math.Round((double)s.FirstLayerSpeedMmS, 3);
        Dictionary<string, object?> meta = new()
        {
            ["absolute"] = true,
            ["ipo_frame"] = "#BASE",
            ["frame"] = "base",
            ["tool"] = s.Tool,
            ["base"] = s.Base,
        };
        var bed = BedOriginOrComputed(s);
        if (bed.LengthSquared() > 1f)
        {
            meta["bed_origin"] = new Dictionary<string, double>
            {
                ["x"] = Math.Round(bed.X, 3),
                ["y"] = Math.Round(bed.Y, 3),
                ["z"] = Math.Round(bed.Z, 3),
            };
        }
        if (s.MillOrientation || s.SpindleRpm > 0)
        {
            defaults["milling_speed_mm_s"] = s.PrintSpeedMmS;
            defaults["spindle_rpm"] = s.SpindleRpm;
            meta["spindle"] = true;
            meta["spindle_rpm"] = s.SpindleRpm;
            meta["tool"] = "spindle";
            meta["approach_clearance_mm"] = s.ApproachClearanceMm;
            meta["approach_waypoint"] = "Milling Start";
            defaults["approach_clearance_mm"] = s.ApproachClearanceMm;
        }
        if (!s.AbsolutePath)
            meta["absolute"] = false;

        return new MassiveDriveJobBuild
        {
            JobId = jobId,
            CellId = s.CellId,
            Name = s.Name,
            Segments = segments,
            Source = source,
            Defaults = defaults,
            Frames = new Dictionary<string, int>
            {
                ["tool"] = s.Tool,
                ["base"] = s.Base,
            },
            Meta = meta,
            Units = new Dictionary<string, string>
            {
                ["length"] = "mm",
                ["speed"] = "mm/s",
                ["angles"] = "deg",
            },
        };
    }

    /// <summary>Export toolpath to a dictionary matching massivedrive.job/v1.</summary>
    public static Dictionary<string, object?> ExportDict(Toolpath toolpath, MassiveDriveExportSettings s)
        => Export(toolpath, s).ToV1Dict();

    public static string ExportJson(Toolpath toolpath, MassiveDriveExportSettings s)
        => JsonSerializer.Serialize(ExportDict(toolpath, s), JsonOpts);

    public static void ExportToFile(Toolpath toolpath, MassiveDriveExportSettings s, string path)
    {
        var json = ExportJson(toolpath, s);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Same RPM inputs KRL export uses. Null for mill only.
    /// Print always stamps <c>rpm_pct</c> (UI % or bead×height×speed×flow).
    /// </summary>
    static KrlExportSettings? RpmInputs(MassiveDriveExportSettings s)
    {
        if (s.MillOrientation || s.SpindleRpm > 0)
            return null;
        return new KrlExportSettings
        {
            ProgramName = "drive",
            PrintSpeedMps = Math.Max(s.PrintSpeedMmS, 0f) / 1000f,
            BeadWidthMm = s.BeadWidthMm > 0.05f ? s.BeadWidthMm : 6f,
            LayerHeightMm = s.LayerHeightMm > 0.05f ? s.LayerHeightMm : 3f,
            FlowRate = s.FlowRate > 1e-6f ? s.FlowRate : 0.463f,
            ExtrusionRpmPercent = s.ExtrusionRpmPercent > 0.05f ? s.ExtrusionRpmPercent : null,
            FirstLayerRpmPercent = s.FirstLayerRpmPercent,
            FirstLayerSpeedMps = s.FirstLayerSpeedMmS > 0.05f ? s.FirstLayerSpeedMmS / 1000f : 0f,
        };
    }

    /// <summary>
    /// Pose as {x,y,z,a,b,c} in print-bed BASE (same <see cref="KrlExporter.WorldToBase"/> as SRC).
    /// Drive adds meta.bed_origin for RSI / $POS_ACT. File Z is layer height (~3), not bed world (~919).
    /// </summary>
    static MassiveDrivePose Pose(
        Vector3 p,
        Vector3 layerNormal,
        Vector3 moveNormal,
        float tcpYawDeg,
        MassiveDriveExportSettings s)
    {
        var n = moveNormal.LengthSquared() > 1e-12f ? moveNormal : layerNormal;
        if (n.LengthSquared() < 1e-12f)
            n = Vector3.UnitZ;

        var (a, b, c) = s.MillOrientation
            ? KukaOrientation.AbcFromMillNormal(
                n, tcpYawDeg, s.ToolheadOffsetA, s.ToolheadOffsetB, s.ToolheadOffsetC)
            : KukaOrientation.AbcFromNormal(
                n, s.ToolheadOffsetA, s.ToolheadOffsetB, s.ToolheadOffsetC, tcpYawDeg);

        var basePt = ToBase(p, s);
        return new MassiveDrivePose(
            Math.Round(basePt.X, 3),
            Math.Round(basePt.Y, 3),
            Math.Round(basePt.Z, 3),
            Math.Round(a, 3),
            Math.Round(b, 3),
            Math.Round(c, 3));
    }

    static Vector3 ToBase(Vector3 stored, MassiveDriveExportSettings s)
    {
        var world = stored;
        if (!(s.NodeWorldTransform.IsIdentity && s.NodeOrigin == default))
            world = Vector3.Transform(stored - s.NodeOrigin, s.NodeWorldTransform);
        return KrlExporter.WorldToBase(world, s.RobrootWorldPos, s.BaseDataOffset, s.SliceBedWorldZ);
    }

    static Vector3 BedOriginOrComputed(MassiveDriveExportSettings s)
    {
        if (s.BedOrigin.LengthSquared() > 1f)
            return s.BedOrigin;
        return KrlExporter.BaseToWorld(Vector3.Zero, s.RobrootWorldPos, s.BaseDataOffset, s.SliceBedWorldZ);
    }

    /// <summary>
    /// Robot can only start a move where the previous one ended. Island hops in the
    /// sliced path are often tagged at the last print's <c>From</c> (seam vertex)
    /// after that print already closed to <c>To</c>. Snap travel and wipe
    /// origins onto the live TCP so Drive's polyline does not reverse along the
    /// closing edge (MassiveDRIVE Issue 1).
    /// </summary>
    internal static void StitchContinuous(
        List<MassiveDriveSegment> segments, MassiveDriveExportSettings s)
    {
        const float gapMm = 0.5f;
        if (segments.Count < 2) return;

        var stitched = new List<MassiveDriveSegment>(segments.Count + 8);
        var prev = segments[0].From.Xyz;
        int i = 0;
        foreach (var seg in segments)
        {
            var from = seg.From.Xyz;
            var to = seg.To.Xyz;
            var kind = seg.Kind;
            if (Dist(prev, from) > gapMm)
            {
                if (kind == "travel" && IsVertical(from, to))
                {
                    float dz = to.Z - from.Z;
                    from = prev;
                    to = new Vector3(prev.X, prev.Y, prev.Z + dz);
                    seg.From = seg.From.WithXyz(from);
                    seg.To = seg.To.WithXyz(to);
                }
                else if (kind == "travel")
                {
                    from = prev;
                    seg.From = seg.From.WithXyz(from);
                }
                else if (seg.Wipe)
                {
                    var delta = to - from;
                    from = prev;
                    to = prev + delta;
                    seg.From = seg.From.WithXyz(from);
                    seg.To = seg.To.WithXyz(to);
                }
                else
                {
                    stitched.Add(MakeStitchTravel(prev, from, seg, s, i));
                    i++;
                }
            }

            seg.Index = i;
            stitched.Add(seg);
            i++;
            prev = seg.To.Xyz;
        }

        segments.Clear();
        segments.AddRange(stitched);
    }

    static MassiveDriveSegment MakeStitchTravel(
        Vector3 from,
        Vector3 to,
        MassiveDriveSegment next,
        MassiveDriveExportSettings s,
        int i)
    {
        return new MassiveDriveSegment
        {
            Index = i,
            Kind = "travel",
            Layer = next.Layer,
            From = next.From.WithXyz(from),
            To = next.From.WithXyz(to),
            SpeedMmS = Math.Round((double)s.TravelSpeedMmS, 3),
            FlowScale = 0.0,
            Reverse = s.TravelReverse && !s.MillOrientation,
        };
    }

    static bool IsVertical(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy < 0.25f && MathF.Abs(a.Z - b.Z) > 0.5f;
    }

    static float Dist(Vector3 a, Vector3 b) => Vector3.Distance(a, b);
}
