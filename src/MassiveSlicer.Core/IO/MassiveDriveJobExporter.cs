using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Export settings for MassiveDRIVE job packages (<c>massivedrive.job/v1</c>).
/// Drive executes the path over RSI + ClearCore; no print KRL is loaded.
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
    public float ReverseMs { get; init; } = 200f;
    public float ReversePercent { get; init; } = 40f;
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
/// Builds MassiveDRIVE job package JSON from a <see cref="Toolpath"/>.
/// </summary>
public static class MassiveDriveJobExporter
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Export toolpath to a dictionary matching massivedrive.job/v1.</summary>
    public static Dictionary<string, object?> ExportDict(Toolpath toolpath, MassiveDriveExportSettings s)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        ArgumentNullException.ThrowIfNull(s);

        var segments = new List<Dictionary<string, object?>>();
        int i = 0;
        int prevLayer = -1;

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

                float speed = kind == "travel"
                    ? (move.TravelSpeedMps is { } tsm ? tsm * 1000f : s.TravelSpeedMmS)
                    : kind == "mill"
                        ? s.PrintSpeedMmS
                        : s.PrintSpeedMmS * Math.Max(0.05f, move.PrintSpeedScale);

                if (move.IsWipe)
                    speed = Math.Max(speed * Math.Max(0.05f, move.WipeRpmScale), 1f);
                if (move.IsResumeRamp)
                    speed = Math.Max(speed * Math.Max(0.05f, move.ResumeSpeedScale), 1f);

                bool layerChange = move.IsLayerChange || (prevLayer >= 0 && layer.Index != prevLayer && kind == "print");
                prevLayer = layer.Index;

                // Pose: XYZ + KUKA ABC from surface normal (same math as KRL export / viewport)
                var from = PoseDict(move.From, layer.PlaneNormal, move.Normal, move.TcpYawDeg, s);
                var to = PoseDict(move.To, layer.PlaneNormal, move.Normal, move.TcpYawDeg, s);

                var seg = new Dictionary<string, object?>
                {
                    ["i"] = i,
                    ["kind"] = kind,
                    ["layer"] = layer.Index,
                    ["from"] = from,
                    ["to"] = to,
                    ["speed_mm_s"] = Math.Round(speed, 3),
                    ["flow_scale"] = Math.Round(
                        (double)(move.IsWipe ? move.WipeRpmScale
                            : move.IsResumeRamp ? move.ResumeRpmScale
                            : move.PrintSpeedScale * Math.Max(0.05f, move.HeightScale)),
                        4),
                };
                if (kind == "travel")
                    seg["reverse"] = s.TravelReverse && !s.MillOrientation && !move.IsZHop;
                if (layerChange)
                    seg["layer_change"] = true;
                if (move.IsWipe)
                    seg["meta"] = new Dictionary<string, object?> { ["wipe"] = true };
                if (move.IsResumeRamp)
                    seg["meta"] = new Dictionary<string, object?> { ["resume_ramp"] = true };

                segments.Add(seg);
                i++;
            }
        }

        if (segments.Count == 0)
            throw new InvalidOperationException("Toolpath has no exportable print/travel moves.");

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

        var dict = new Dictionary<string, object?>
        {
            ["format"] = "massivedrive.job/v1",
            ["cell_id"] = s.CellId,
            ["job_id"] = jobId,
            ["name"] = s.Name,
            ["source"] = source,
            ["units"] = new Dictionary<string, string>
            {
                ["length"] = "mm",
                ["speed"] = "mm/s",
                ["angles"] = "deg",
            },
            ["frames"] = new Dictionary<string, int>
            {
                ["tool"] = s.Tool,
                ["base"] = s.Base,
            },
            ["defaults"] = defaults,
            ["segments"] = segments,
            ["meta"] = meta,
        };
        return dict;
    }

    public static string ExportJson(Toolpath toolpath, MassiveDriveExportSettings s)
        => JsonSerializer.Serialize(ExportDict(toolpath, s), JsonOpts);

    public static void ExportToFile(Toolpath toolpath, MassiveDriveExportSettings s, string path)
    {
        var json = ExportJson(toolpath, s);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Pose as {x,y,z,a,b,c} in print-bed BASE (same <see cref="KrlExporter.WorldToBase"/> as SRC).
    /// Drive adds meta.bed_origin for RSI / $POS_ACT. File Z is layer height (~3), not bed world (~919).
    /// </summary>
    static Dictionary<string, double> PoseDict(
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
        return new Dictionary<string, double>
        {
            ["x"] = Math.Round(basePt.X, 3),
            ["y"] = Math.Round(basePt.Y, 3),
            ["z"] = Math.Round(basePt.Z, 3),
            ["a"] = Math.Round(a, 3),
            ["b"] = Math.Round(b, 3),
            ["c"] = Math.Round(c, 3),
        };
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
}
