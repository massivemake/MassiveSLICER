using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
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

                // Skip pure mill packages for pellet Drive unless caller wants them
                if (kind == "mill")
                    continue;

                float speed = kind == "travel"
                    ? (move.TravelSpeedMps is { } tsm ? tsm * 1000f : s.TravelSpeedMmS)
                    : s.PrintSpeedMmS * Math.Max(0.05f, move.PrintSpeedScale);

                if (move.IsWipe)
                    speed = Math.Max(speed * Math.Max(0.05f, move.WipeRpmScale), 1f);
                if (move.IsResumeRamp)
                    speed = Math.Max(speed * Math.Max(0.05f, move.ResumeSpeedScale), 1f);

                bool layerChange = move.IsLayerChange || (prevLayer >= 0 && layer.Index != prevLayer && kind == "print");
                prevLayer = layer.Index;

                // Pose: XYZ + default ABC for Z-up planar (B=90). Per-move normal can refine later.
                var from = PoseArray(move.From, layer.PlaneNormal, move.Normal, move.TcpYawDeg);
                var to = PoseArray(move.To, layer.PlaneNormal, move.Normal, move.TcpYawDeg);

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
                    seg["reverse"] = s.TravelReverse && !move.IsZHop;
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

        return new Dictionary<string, object?>
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
            ["defaults"] = new Dictionary<string, object?>
            {
                ["print_speed_mm_s"] = s.PrintSpeedMmS,
                ["travel_speed_mm_s"] = s.TravelSpeedMmS,
                ["reverse_ms"] = s.ReverseMs,
                ["reverse_percent"] = s.ReversePercent,
            },
            ["segments"] = segments,
        };
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
    /// Pose as [x,y,z,a,b,c]. ABC is a simple Z-up default (B=90) when normal ~ +Z;
    /// otherwise leaves A=0,B=90,C=0 as a stable Drive default (orientation polish later).
    /// </summary>
    static float[] PoseArray(Vector3 p, Vector3 layerNormal, Vector3 moveNormal, float tcpYawDeg)
    {
        // Drive primarily uses XYZ for path following today; ABC kept stable for planar LFAM.
        _ = layerNormal;
        _ = moveNormal;
        _ = tcpYawDeg;
        return
        [
            p.X, p.Y, p.Z,
            0f, 90f, 0f,
        ];
    }
}
