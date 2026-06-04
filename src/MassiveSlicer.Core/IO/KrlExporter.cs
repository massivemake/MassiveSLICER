using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

public sealed record KrlExportSettings
{
    public required string ProgramName { get; init; }
    public int ToolDataIndex { get; init; } = 1;
    public int BaseDataIndex { get; init; } = 1;
    /// <summary>Deposition feed rate in m/s.</summary>
    public float FeedRateMps { get; init; } = 0.1f;
    /// <summary>Travel (non-extrusion) move speed in m/s.</summary>
    public float TravelSpeedMps { get; init; } = 0.5f;
    public int AccelerationPercent { get; init; } = 100;
    /// <summary>World Z lift above the first/last print position for approach and retreat.</summary>
    public float ApproachZMm { get; init; } = 50f;
    public float ToolheadOffsetA { get; init; }
    public float ToolheadOffsetB { get; init; }
    public float ToolheadOffsetC { get; init; }
    public float Temperature1 { get; init; } = 230f;
    public float Temperature2 { get; init; } = 230f;
    public float Temperature3 { get; init; } = 230f;
    /// <summary>Deposited bead width in mm.</summary>
    public float BeadWidthMm { get; init; } = 6f;
    /// <summary>Deposited layer height in mm.</summary>
    public float LayerHeightMm { get; init; } = 3f;
    /// <summary>
    /// Material extrusion constant in rev/cm³ -- motor revolutions per cubic centimetre deposited.
    /// Combined with bead geometry and feed rate to give volumetric flow, then RPM percentage:
    ///   volume_cm³/s = beadWidth_mm × layerHeight_mm × feedMps
    ///   rpm_percent  = volume × FlowRate × 60
    ///   $ANOUT[4]    = rpm_percent × 0.1
    /// Defaults to 1.0 (full speed) when no material preset is active.
    /// </summary>
    public float FlowRate { get; init; } = 1.0f;
    public float[] HomePosition { get; init; } = [0f, -90f, 90f, 0f, 15f, 0f];
    /// <summary>
    /// Node world transform (System.Numerics, row-vector convention).
    /// Converts stored Toolpath positions (original slice world space) to current world space.
    /// </summary>
    public Matrix4x4 NodeWorldTransform { get; init; } = Matrix4x4.Identity;
    /// <summary>Translation component of the node's initial LocalTransform (its centroid in world space).</summary>
    public Vector3 NodeOrigin { get; init; }
    /// <summary>ROBROOT origin in scene/world space (mm).</summary>
    public Vector3 RobrootWorldPos { get; init; }
    /// <summary>BASE_DATA offset relative to ROBROOT (mm). Assumes zero BASE rotation.</summary>
    public Vector3 BaseDataOffset { get; init; }
}

public static class KrlExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Export(Toolpath toolpath, KrlExportSettings s)
    {
        var sb   = new StringBuilder(64 * 1024);
        var name = SafeName(s.ProgramName);

        WriteHeader(sb, name, s);

        var firstEntry = FindFirstExtrude(toolpath);
        if (firstEntry is null)
        {
            sb.AppendLine("END");
            return sb.ToString();
        }

        var (firstMove, firstLayer) = firstEntry.Value;
        var (a0, b0, c0) = KukaAbc(firstLayer.PlaneNormal, s);
        var p0 = ToBase(firstMove.From, s);

        // -- Initial approach -----------------------------------------------------
        sb.AppendLine($"$VEL.CP = {s.TravelSpeedMps.ToString("F6", Inv)}");
        sb.AppendLine(FormatLin(new Vector3(p0.X, p0.Y, p0.Z + s.ApproachZMm), a0, b0, c0));
        // Exact stop at the touch-down point so the RPM-on TRIGGER fires at the
        // correct physical position (not inside a C_VEL blend zone).
        sb.AppendLine(FormatLinExact(p0, a0, b0, c0));
        sb.AppendLine();

        Vector3 lastPos = p0;
        (float a, float b, float c) lastAbc = (a0, b0, c0);
        bool needsRpmOn = true;

        // -- Layer loop -----------------------------------------------------------
        for (int li = 0; li < toolpath.Layers.Count; li++)
        {
            var layer        = toolpath.Layers[li];
            var (la, lb, lc) = KukaAbc(layer.PlaneNormal, s);

            foreach (var move in layer.Moves)
            {
                var to = ToBase(move.To, s);

                if (move.Kind == MoveKind.Travel)
                {
                    sb.AppendLine(";travel");
                    // TRIGGER fires exactly when the preceding extrude ends (exact-stop means
                    // DISTANCE=0 coincides with the actual waypoint, not a C_VEL blend zone).
                    sb.AppendLine(FormatTriggerAnout4(0.100f, "RPM idle"));
                    sb.AppendLine($"$VEL.CP = {s.TravelSpeedMps.ToString("F6", Inv)}");
                    sb.AppendLine(FormatLinExact(to, la, lb, lc));
                    sb.AppendLine();
                    needsRpmOn = true;
                }
                else
                {
                    if (needsRpmOn)
                    {
                        sb.AppendLine(FormatTriggerAnout4(RpmVoltage(s), "RPM on"));
                        sb.AppendLine($"$VEL.CP = {s.FeedRateMps.ToString("F6", Inv)}");
                        needsRpmOn = false;
                    }
                    sb.AppendLine(FormatLin(to, la, lb, lc));
                }

                lastPos = to;
            }

            lastAbc = (la, lb, lc);

            // -- Inter-layer travel -----------------------------------------------
            if (li >= toolpath.Layers.Count - 1) continue;

            var nextLayer = toolpath.Layers[li + 1];
            if (nextLayer.Moves.Count == 0) continue;

            var (na, nb, nc) = KukaAbc(nextLayer.PlaneNormal, s);
            var nextStart    = ToBase(nextLayer.Moves[0].From, s);

            sb.AppendLine(";travel");
            sb.AppendLine(FormatTriggerAnout4(0.100f, "RPM idle"));
            sb.AppendLine($"$VEL.CP = {s.TravelSpeedMps.ToString("F6", Inv)}");
            sb.AppendLine(FormatLinExact(nextStart, na, nb, nc));
            sb.AppendLine();
            needsRpmOn = true;
        }

        // -- Final retreat --------------------------------------------------------
        var (fa, fb, fc) = lastAbc;
        sb.AppendLine(";retreat");
        sb.AppendLine(FormatTriggerAnout4(0.100f, "RPM idle"));
        sb.AppendLine($"$VEL.CP = {s.TravelSpeedMps.ToString("F6", Inv)}");
        sb.AppendLine(FormatLinExact(new Vector3(lastPos.X, lastPos.Y, lastPos.Z + s.ApproachZMm), fa, fb, fc));
        sb.AppendLine();
        sb.AppendLine("END");

        return sb.ToString();
    }

    // -- Helpers ---------------------------------------------------------------

    private static void WriteHeader(StringBuilder sb, string name, KrlExportSettings s)
    {
        sb.AppendLine("&ACCESS RVP");
        sb.AppendLine($"DEF {name} ()");
        sb.AppendLine();

        sb.AppendLine(";FOLD INI");
        sb.AppendLine(";FOLD BASISTECH INI");
        sb.AppendLine("GLOBAL INTERRUPT DECL 3 WHEN $STOPMESS==TRUE DO IR_STOPM ( )");
        sb.AppendLine("INTERRUPT ON 3");
        sb.AppendLine("BAS (#INITMOV,0 )");
        sb.AppendLine(";ENDFOLD (BASISTECH INI)");
        sb.AppendLine(";ENDFOLD (INI)");
        sb.AppendLine();

        sb.AppendLine(";FOLD MAT");
        sb.AppendLine($"$ANOUT[1] = {TempToVoltage(s.Temperature1).ToString("F3", Inv)} ; T1 = {s.Temperature1:F0}C");
        sb.AppendLine($"$ANOUT[2] = {TempToVoltage(s.Temperature2).ToString("F3", Inv)} ; T2 = {s.Temperature2:F0}C");
        sb.AppendLine($"$ANOUT[3] = {TempToVoltage(s.Temperature3).ToString("F3", Inv)} ; T3 = {s.Temperature3:F0}C");
        sb.AppendLine("$ANOUT[4] = 0.100 ; RPM idle");
        sb.AppendLine(";ENDFOLD MAT");
        sb.AppendLine();

        sb.AppendLine(";FOLD PRESETS");
        sb.AppendLine("$BWDSTART = FALSE");
        sb.AppendLine("PDAT_ACT = {VEL 6,ACC 100,APO_DIST 50}");
        sb.AppendLine($"FDAT_ACT = {{TOOL_NO {s.ToolDataIndex},BASE_NO {s.BaseDataIndex},IPO_FRAME #BASE}}");
        sb.AppendLine("BAS (#PTP_PARAMS,6)");
        sb.AppendLine("$ADVANCE=3");
        sb.AppendLine("$APO.CVEL=50");
        sb.AppendLine($"$VEL.CP={s.FeedRateMps.ToString("F6", Inv)}");
        sb.AppendLine(";ENDFOLD (PRESETS)");
        sb.AppendLine();

        sb.AppendLine(";FOLD TIMER");
        sb.AppendLine("WAIT SEC 0");
        sb.AppendLine("$TIMER_STOP[7] = TRUE");
        sb.AppendLine("$TIMER[7] = 0");
        sb.AppendLine("$TIMER_STOP[7] = FALSE");
        sb.AppendLine(";ENDFOLD");
        sb.AppendLine();

        sb.AppendLine($"BAS(#BASE,{s.BaseDataIndex})");
        sb.AppendLine("BAS(#VEL_PTP,10)");
        var h = s.HomePosition;
        sb.AppendLine($"PTP {{A1 {h[0].ToString("F3", Inv)}, A2 {h[1].ToString("F3", Inv)}, A3 {h[2].ToString("F3", Inv)}, A4 {h[3].ToString("F3", Inv)}, A5 {h[4].ToString("F3", Inv)}, A6 {h[5].ToString("F3", Inv)}}}");
    }

    private static (ToolpathMove move, ToolpathLayer layer)? FindFirstExtrude(Toolpath tp)
    {
        foreach (var layer in tp.Layers)
            foreach (var move in layer.Moves)
                if (move.Kind == MoveKind.Extrude)
                    return (move, layer);
        return null;
    }

    // -- Coordinate transform --------------------------------------------------

    private static Vector3 ToBase(Vector3 stored, KrlExportSettings s)
    {
        // stored positions are in original slice world space;
        // (stored - origin) * wt maps them to current world space.
        var world = Vector3.Transform(stored - s.NodeOrigin, s.NodeWorldTransform);
        return world - s.RobrootWorldPos - s.BaseDataOffset;
    }

    // -- ABC orientation -------------------------------------------------------

    private static (float a, float b, float c) KukaAbc(Vector3 normal, KrlExportSettings s)
    {
        // Approach along -normal: tool X = -normal in ROBROOT.
        // KUKA ZYX Euler: B = asin(normal.Z), A = atan2(-normal.Y, -normal.X), C = 0
        // then add user offsets.
        float b    = MathF.Asin(Math.Clamp(normal.Z, -1f, 1f)) * (180f / MathF.PI);
        float cosB = MathF.Cos(b * (MathF.PI / 180f));
        float a    = MathF.Abs(cosB) > 1e-6f
            ? MathF.Atan2(-normal.Y, -normal.X) * (180f / MathF.PI)
            : 0f;
        return (a + s.ToolheadOffsetA, b + s.ToolheadOffsetB, s.ToolheadOffsetC);
    }

    // -- Voltage conversions ---------------------------------------------------

    private static float TempToVoltage(float tempC) => (tempC - 149f) * 0.032f;

    // volume_cm³/s = beadWidth_mm × layerHeight_mm × feedMps  (mm × mm × m/s -> cm³/s, units cancel)
    // rpm_percent  = volume × flowRate [rev/cm³] × 60
    // voltage      = rpm_percent × 0.1  (PLC convention: 0.1 V per 1%)
    private static float RpmVoltage(KrlExportSettings s)
        => s.BeadWidthMm * s.LayerHeightMm * s.FeedRateMps * s.FlowRate * 6f;

    // -- Line formatting -------------------------------------------------------

    // C_VEL: approximate (blended) positioning — used for extrude moves so the robot
    // never fully stops mid-bead and maintains a smooth velocity profile.
    private static string FormatLin(Vector3 p, float a, float b, float c)
        => $"LIN {{X {p.X.ToString("F2", Inv)}, Y {p.Y.ToString("F2", Inv)}, Z {p.Z.ToString("F2", Inv)}, " +
           $"A {a.ToString("F3", Inv)}, B {b.ToString("F3", Inv)}, C {c.ToString("F3", Inv)}, " +
           $"E1 0.000, E2 0.000, E3 0.000, E4 0.000, E5 0.000, E6 0.000 }} C_VEL";

    // Exact stop — used for travel/approach/retreat moves so TRIGGER WHEN DISTANCE=0
    // fires at the precise physical waypoint rather than inside a C_VEL blend zone.
    // This is the fix for the $ADVANCE look-ahead timing issue: with exact stop the
    // "path switchover point" (DISTANCE=0) coincides with the actual robot position.
    private static string FormatLinExact(Vector3 p, float a, float b, float c)
        => $"LIN {{X {p.X.ToString("F2", Inv)}, Y {p.Y.ToString("F2", Inv)}, Z {p.Z.ToString("F2", Inv)}, " +
           $"A {a.ToString("F3", Inv)}, B {b.ToString("F3", Inv)}, C {c.ToString("F3", Inv)}, " +
           $"E1 0.000, E2 0.000, E3 0.000, E4 0.000, E5 0.000, E6 0.000 }}";

    private static string FormatTriggerAnout4(float v, string comment)
        => $"TRIGGER WHEN DISTANCE=0 DELAY=0 DO $ANOUT[4]={v.ToString("F3", Inv)} ; {comment}";

    private static string SafeName(string raw)
        => Regex.Replace(raw.Trim(), @"[^A-Za-z0-9_]", "_");
}
