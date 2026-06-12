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
    /// <summary>Deposition print speed in m/s.</summary>
    public float PrintSpeedMps { get; init; } = 0.1f;
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
    /// How far ahead of each point (mm) to centre the Gaussian normal-smoothing kernel.
    /// At 60 mm/s print speed, 60 mm = 1 second of pre-rotation, so the robot begins
    /// transitioning before it reaches the peak orientation change.
    /// 0 = disabled (raw per-move normals used directly).
    /// </summary>
    /// <summary>KUKA $APO.CVEL value (0–100). Controls the minimum speed fraction at path corners.</summary>
    public int ApoCvel { get; init; } = 100;
    public float OrientationLookAheadMm { get; init; } = 0f;
    /// <summary>
    /// Standard deviation (mm) of the Gaussian kernel used to smooth normals before ABC
    /// conversion. Controls the width of the orientation transition ramp.
    /// Typically half of OrientationLookAheadMm. Only used when OrientationLookAheadMm > 0.
    /// </summary>
    public float OrientationSigmaMm { get; init; } = 30f;
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
            WriteFooter(sb);
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

        // Pre-smooth per-move normals along each contour with a forward-biased Gaussian
        // kernel before ABC conversion. This prevents the KRL exporter from producing
        // large A/C discontinuities even after the singularity guard, because near-vertical
        // normals that vary slightly between adjacent points still produce smooth transitions
        // when the normal vectors themselves are smoothed first.
        var smoothedByLayer = s.OrientationLookAheadMm > 0f
            ? PrecomputeSmoothedNormals(toolpath, s)
            : null;

        // -- Layer loop -----------------------------------------------------------
        for (int li = 0; li < toolpath.Layers.Count; li++)
        {
            var layer        = toolpath.Layers[li];
            var (la, lb, lc) = KukaAbc(layer.PlaneNormal, s);
            var smoothedLayer = smoothedByLayer?[li];

            for (int mi = 0; mi < layer.Moves.Count; mi++)
            {
                var move = layer.Moves[mi];
                var to   = ToBase(move.To, s);

                if (move.Kind == MoveKind.Travel)
                {
                    sb.AppendLine(move.IsLayerChange ? ";layer change" : ";travel");
                    // TRIGGER fires exactly when the preceding extrude ends (exact-stop means
                    // DISTANCE=0 coincides with the actual waypoint, not a C_VEL blend zone).
                    sb.AppendLine(FormatTriggerAnout4(0.100f, "RPM idle"));
                    sb.AppendLine($"$VEL.CP = {s.TravelSpeedMps.ToString("F6", Inv)}");
                    // Carry the last printed orientation through the travel — avoids a sudden
                    // ABC jump when per-move overhang normals differ from the layer plane normal.
                    // The first extrude of the next contour/layer smoothly transitions to its
                    // own orientation.
                    var (ta, tb, tc) = lastAbc;
                    sb.AppendLine(FormatLinExact(to, ta, tb, tc));
                    sb.AppendLine();
                    needsRpmOn = true;
                }
                else
                {
                    // Use pre-smoothed normal when available; fall back to raw move normal.
                    var effectiveNormal = smoothedLayer?[mi] ?? move.Normal;

                    // Layer stitch moves carry no surface normal — hold last orientation to avoid
                    // the same ABC snap as travel moves.  Per-move overhang normals use the full
                    // A/B formula so the tool tilts correctly in all directions (A orients the tilt
                    // heading, B sets the tilt magnitude).  Falls back to layer plane orientation.
                    var (ma, mb, mc) = move.IsLayerStitch
                        ? lastAbc
                        : effectiveNormal.LengthSquared() > 1e-6f
                            ? KukaAbc(effectiveNormal, s)
                            : (la, lb, lc);

                    if (needsRpmOn)
                    {
                        sb.AppendLine(FormatTriggerAnout4(RpmVoltage(s), "RPM on"));
                        sb.AppendLine($"$VEL.CP = {s.PrintSpeedMps.ToString("F6", Inv)}");
                        needsRpmOn = false;
                    }
                    sb.AppendLine(FormatLin(to, ma, mb, mc));
                    lastAbc = (ma, mb, mc);
                }

                lastPos = to;
            }
        }

        // -- Final retreat --------------------------------------------------------
        var (fa, fb, fc) = lastAbc;
        sb.AppendLine(";retreat");
        sb.AppendLine(FormatTriggerAnout4(0.100f, "RPM idle"));
        sb.AppendLine($"$VEL.CP = {s.TravelSpeedMps.ToString("F6", Inv)}");
        sb.AppendLine(FormatLinExact(new Vector3(lastPos.X, lastPos.Y, lastPos.Z + s.ApproachZMm), fa, fb, fc));
        sb.AppendLine();
        WriteFooter(sb);

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

        sb.AppendLine(";FOLD CheckFlange");
        sb.AppendLine("IF $IN[7]== TRUE THEN");
        sb.AppendLine("       msgnotify(\"!!! The flange is currently detached - place it back in position and press play\")");
        sb.AppendLine();
        sb.AppendLine("    WAIT FOR $IN[7]==FALSE");
        sb.AppendLine("       HALT ;press \">\" to go forward");
        sb.AppendLine("ENDIF");
        sb.AppendLine();
        sb.AppendLine(";ENDFOLD(CheckFlange)");
        sb.AppendLine();

        sb.AppendLine(";FOLD MAT");
        sb.AppendLine("$OUT[9] = TRUE");
        sb.AppendLine($"$ANOUT[1] = {TempToVoltage(s.Temperature1).ToString("F3", Inv)} ; T1 = {s.Temperature1:F0}C");
        sb.AppendLine($"$ANOUT[2] = {TempToVoltage(s.Temperature2).ToString("F3", Inv)} ; T2 = {s.Temperature2:F0}C");
        sb.AppendLine($"$ANOUT[3] = {TempToVoltage(s.Temperature3).ToString("F3", Inv)} ; T3 = {s.Temperature3:F0}C");
        sb.AppendLine("$ANOUT[4] = 0.100 ; RPM idle");
        sb.AppendLine(";ENDFOLD MAT");
        sb.AppendLine();

        sb.AppendLine(";FOLD READYTOPRINT");
        sb.AppendLine("$OUT[7]=TRUE");
        sb.AppendLine(";IF IN[6] DOES NOT CONNECT EXTRUDER WILL NOT START");
        sb.AppendLine("WAIT FOR $IN[6]==TRUE");
        sb.AppendLine(";ENDFOLD");
        sb.AppendLine();

        sb.AppendLine(";FOLD PRESETS");
        sb.AppendLine("$BWDSTART = FALSE");
        sb.AppendLine("PDAT_ACT = {VEL 6,ACC 100,APO_DIST 50}");
        sb.AppendLine($"FDAT_ACT = {{TOOL_NO {s.ToolDataIndex},BASE_NO {s.BaseDataIndex},IPO_FRAME #BASE}}");
        sb.AppendLine("BAS (#PTP_PARAMS,6)");
        sb.AppendLine("$ADVANCE=5");
        sb.AppendLine($"$APO.CVEL={s.ApoCvel}");
        sb.AppendLine("$ACC.CP = 5.0");
        sb.AppendLine($"$VEL.CP={s.PrintSpeedMps.ToString("F6", Inv)}");
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

    private static void WriteFooter(StringBuilder sb)
    {
        sb.AppendLine("$OUT[7]=FALSE");
        sb.AppendLine("$OUT[8] = FALSE");
        sb.AppendLine("$OUT[9] = FALSE");
        sb.AppendLine("END");
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

    private const float D2R = MathF.PI / 180f;
    private const float R2D = 180f / MathF.PI;

    /// <summary>Rodrigues rotation of v around a unit axis.</summary>
    private static Vector3 Rodrigues(Vector3 v, Vector3 axis, float sinTheta, float cosTheta)
        => v * cosTheta + Vector3.Cross(axis, v) * sinTheta + axis * Vector3.Dot(axis, v) * (1f - cosTheta);

    // Builds a per-layer, per-move table of Gaussian-smoothed normals for contour runs
    // that have per-move normals (OverhangOrientation). Null entries mean "use raw normal".
    private static Vector3?[][] PrecomputeSmoothedNormals(Toolpath tp, KrlExportSettings s)
    {
        var result = new Vector3?[tp.Layers.Count][];
        for (int li = 0; li < tp.Layers.Count; li++)
        {
            var moves = tp.Layers[li].Moves;
            result[li] = new Vector3?[moves.Count];

            int i = 0;
            while (i < moves.Count)
            {
                if (moves[i].Kind == MoveKind.Travel || moves[i].IsLayerStitch) { i++; continue; }

                int runStart = i;
                while (i < moves.Count && moves[i].Kind != MoveKind.Travel && !moves[i].IsLayerStitch)
                    i++;
                int runEnd = i;

                bool hasNormals = false;
                for (int j = runStart; j < runEnd; j++)
                    if (moves[j].Normal.LengthSquared() > 1e-6f) { hasNormals = true; break; }
                if (!hasNormals) continue;

                var smoothed = GaussianSmoothNormals(moves, runStart, runEnd,
                                                     s.OrientationLookAheadMm, s.OrientationSigmaMm);
                for (int j = runStart; j < runEnd; j++)
                    result[li][j] = smoothed[j - runStart];
            }
        }
        return result;
    }

    // Arc-length-parameterised Gaussian kernel with a forward look-ahead offset.
    // The kernel centre is placed lookAheadMm ahead of each point so the robot begins
    // rotating before it arrives at the peak orientation, preventing axis overspeed.
    private static Vector3[] GaussianSmoothNormals(
        List<ToolpathMove> moves, int start, int end, float lookAheadMm, float sigmaMm)
    {
        int n = end - start;

        var arcLen = new float[n];
        for (int i = 1; i < n; i++)
        {
            var delta = moves[start + i].From - moves[start + i - 1].From;
            arcLen[i] = arcLen[i - 1] + delta.Length();
        }

        var normals = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var nrm = moves[start + i].Normal;
            normals[i] = nrm.LengthSquared() > 1e-6f ? Vector3.Normalize(nrm) : Vector3.UnitZ;
        }

        var result          = new Vector3[n];
        float invTwoSigmaSq = 1f / (2f * sigmaMm * sigmaMm);

        for (int i = 0; i < n; i++)
        {
            float centre    = arcLen[i] + lookAheadMm;
            float weightSum = 0f;
            var   acc       = Vector3.Zero;

            for (int j = 0; j < n; j++)
            {
                float d = arcLen[j] - centre;
                float w = MathF.Exp(-d * d * invTwoSigmaSq);
                acc += normals[j] * w;
                weightSum += w;
            }

            var avg = weightSum > 1e-6f ? acc / weightSum : normals[i];
            float len = avg.Length();
            result[i] = len > 1e-6f ? avg / len : normals[i];
        }

        return result;
    }

    /// <summary>
    /// Computes KUKA A/B/C (ZYX Euler, degrees) for the given slice plane normal.
    ///
    /// Algorithm:
    ///   1. Build base perpendicular frame: xBase = −normal (nozzle into surface).
    ///      yBase/zBase come from Rodrigues-rotating the canonical (0,1,0)/(1,0,0)
    ///      by the same rotation that takes (0,0,−1) onto xBase.
    ///   2. Apply the toolhead offsets as a local KUKA ZYX rotation in that base frame:
    ///      R_final = R_base · Rz(A)·Ry(B)·Rx(C).
    ///      Zero offset → pure perpendicular pose (same output as before).
    ///      Non-zero offset → physically tilts/rolls the nozzle from perpendicular.
    ///   3. Extract ZYX Euler from R_final.
    /// </summary>
    private static (float a, float b, float c) KukaAbc(Vector3 normal, KrlExportSettings s)
    {
        normal = Vector3.Normalize(normal);

        // Step 1: base perpendicular frame via Rodrigues (0,0,−1) → xBase = −normal
        var xBase = -normal;
        var xDef  = new Vector3(0f, 0f, -1f);
        float cosT = Math.Clamp(Vector3.Dot(xDef, xBase), -1f, 1f);
        Vector3 yBase, zBase;
        if (MathF.Abs(cosT - 1f) < 1e-6f)
        {
            yBase = new Vector3(0f, 1f, 0f); zBase = new Vector3(1f, 0f, 0f);
        }
        else if (MathF.Abs(cosT + 1f) < 1e-6f)
        {
            yBase = new Vector3(0f, 1f, 0f); zBase = new Vector3(-1f, 0f, 0f);
        }
        else
        {
            var   axis = Vector3.Normalize(Vector3.Cross(xDef, xBase));
            float sinT = MathF.Sqrt(1f - cosT * cosT);
            yBase = Rodrigues(new Vector3(0f, 1f, 0f), axis, sinT, cosT);
            zBase = Rodrigues(new Vector3(1f, 0f, 0f), axis, sinT, cosT);
        }

        // Step 2: local KUKA ZYX offset applied in the base frame
        // R_final = R_base · Rz(A)·Ry(B)·Rx(C)
        float ca = MathF.Cos(s.ToolheadOffsetA * D2R), sa = MathF.Sin(s.ToolheadOffsetA * D2R);
        float cb = MathF.Cos(s.ToolheadOffsetB * D2R), sb = MathF.Sin(s.ToolheadOffsetB * D2R);
        float cc = MathF.Cos(s.ToolheadOffsetC * D2R), sc = MathF.Sin(s.ToolheadOffsetC * D2R);

        var xF = xBase * (ca * cb)                 + yBase * (sa * cb)                 + zBase * (-sb);
        var yF = xBase * (ca * sb * sc - sa * cc)   + yBase * (sa * sb * sc + ca * cc)   + zBase * (cb * sc);
        var zF = xBase * (ca * sb * cc + sa * sc)   + yBase * (sa * sb * cc - ca * sc)   + zBase * (cb * cc);

        // Step 3: extract ZYX Euler from R_final
        float bRad = MathF.Atan2(-xF.Z, MathF.Sqrt(xF.X * xF.X + xF.Y * xF.Y));
        float aRad, cRad;
        // Threshold 0.05 rad ≈ cos(87°). Near B = ±90° the A and C axes are collinear
        // (gimbal lock): tiny XY noise in the normal produces huge atan2 swings that force
        // the KUKA to interpolate through spurious wrist rotations at reduced speed.
        // Zero A and C when we're within ~3° of the singularity — the physical difference
        // in tool orientation is negligible, and the robot interpolates at full $VEL.CP.
        if (MathF.Abs(MathF.Abs(bRad) - MathF.PI / 2f) < 0.05f)
        {
            aRad = 0f;
            cRad = 0f;
        }
        else
        {
            aRad = MathF.Atan2(xF.Y, xF.X);
            cRad = MathF.Atan2(yF.Z, zF.Z);
        }
        return (aRad * R2D, bRad * R2D, cRad * R2D);
    }


    // -- Voltage conversions ---------------------------------------------------

    private static float TempToVoltage(float tempC) => (tempC - 149f) * 0.032f;

    // volume_cm³/s = beadWidth_mm × layerHeight_mm × feedMps  (mm × mm × m/s -> cm³/s, units cancel)
    // rpm_percent  = volume × flowRate [rev/cm³] × 60
    // voltage      = rpm_percent × 0.1  (PLC convention: 0.1 V per 1%)
    private static float RpmVoltage(KrlExportSettings s)
        => s.BeadWidthMm * s.LayerHeightMm * s.PrintSpeedMps * s.FlowRate * 6f;

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
