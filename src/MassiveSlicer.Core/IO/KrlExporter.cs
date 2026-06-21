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
    /// <summary>Wipe extrusion move speed in m/s.</summary>
    public float WipeSpeedMps { get; init; } = 0.12f;
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
    /// Material <see cref="Models.MaterialPreset.FlowRate"/> in rev/cm³.
    /// Export uses <see cref="KrlAnout"/> for on-cell <c>$ANOUT</c> scaling unless overrides are set.
    /// </summary>
    public float FlowRate { get; init; } = 0.463f;

    /// <summary>Optional KRL literal for header <c>$ANOUT[1]</c>. Null/empty = compute from <see cref="Temperature1"/>.</summary>
    public string? Anout1Text { get; init; }

    /// <summary>Optional KRL literal for header <c>$ANOUT[2]</c>. Null/empty = compute from <see cref="Temperature2"/>.</summary>
    public string? Anout2Text { get; init; }

    /// <summary>Optional KRL literal for header <c>$ANOUT[3]</c>. Null/empty = compute from <see cref="Temperature3"/>.</summary>
    public string? Anout3Text { get; init; }

    /// <summary>Optional KRL literal for idle <c>$ANOUT[4]</c>. Null/empty = <see cref="KrlAnout.RpmIdleAnoutText"/>.</summary>
    public string? Anout4IdleText { get; init; }

    /// <summary>Optional KRL literal for extrusion TRIGGER <c>$ANOUT[4]</c>. Null/empty = compute from <see cref="ExtrusionRpmPercent"/> or bead geometry.</summary>
    public string? Anout4ExtrudeText { get; init; }

    /// <summary>Optional extrusion motor speed (%). When set, overrides geometry-based RPM before writing <c>$ANOUT[4]</c>.</summary>
    public float? ExtrusionRpmPercent { get; init; }

    /// <summary>
    /// Pause (seconds) after the first extrusion RPM-on before the first print move. 0 = disabled.
    /// When &gt; 0, emits an immediate <c>$ANOUT[4]</c> (not TRIGGER) so the wait happens after RPM changes.
    /// </summary>
    public float ExtrusionStartWaitSec { get; init; } = 1f;

    /// <summary>
    /// Pause (seconds) after each travel before the next extrusion move. 0 = disabled.
    /// Uses immediate <c>$ANOUT[4]</c> so the wait happens after RPM changes.
    /// </summary>
    public float ExtrusionResumeWaitSec { get; init; }
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

    /// <summary>Emit <c>$ANOUT[4] = 0</c> before travel moves instead of a TRIGGER idle pulse.</summary>
    public bool TravelSetAnout4Zero { get; init; } = true;

    /// <summary>Custom header template. Null/empty uses <see cref="KrlExporter.DefaultHeaderTemplate"/>.</summary>
    public string? HeaderTemplate { get; init; }

    /// <summary>Custom footer template. Null/empty uses <see cref="KrlExporter.DefaultFooterTemplate"/>.</summary>
    public string? FooterTemplate { get; init; }
}

public static class KrlExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Default KRL header template with {{PLACEHOLDER}} tokens.</summary>
    public const string DefaultHeaderTemplate = """
        &ACCESS RVP
        DEF {{PROGRAM_NAME}} ()

        ;FOLD INI
        ;FOLD BASISTECH INI
        GLOBAL INTERRUPT DECL 3 WHEN $STOPMESS==TRUE DO IR_STOPM ( )
        INTERRUPT ON 3
        BAS (#INITMOV,0 )
        ;ENDFOLD (BASISTECH INI)
        ;ENDFOLD (INI)

        ;FOLD CheckFlange
        IF $IN[7]== TRUE THEN
               msgnotify("!!! The flange is currently detached - place it back in position and press play")

            WAIT FOR $IN[7]==FALSE
               HALT ;press ">" to go forward
        ENDIF

        ;ENDFOLD(CheckFlange)

        ;FOLD MAT
        $OUT[9] = TRUE
        $ANOUT[1] = {{TEMP1_V}} ; T1 = {{TEMP1_C}}C
        $ANOUT[2] = {{TEMP2_V}} ; T2 = {{TEMP2_C}}C
        $ANOUT[3] = {{TEMP3_V}} ; T3 = {{TEMP3_C}}C
        $ANOUT[4] = {{RPM_IDLE_V}} ; RPM idle
        ;ENDFOLD MAT

        ;FOLD READYTOPRINT
        $OUT[7]=TRUE
        ;IF IN[6] DOES NOT CONNECT EXTRUDER WILL NOT START
        WAIT FOR $IN[6]==TRUE
        ;ENDFOLD

        ;FOLD PRESETS
        $BWDSTART = FALSE
        PDAT_ACT = {VEL 6,ACC 100,APO_DIST 50}
        FDAT_ACT = {TOOL_NO {{TOOL_NO}},BASE_NO {{BASE_NO}},IPO_FRAME #BASE}
        BAS (#PTP_PARAMS,6)
        $ADVANCE=5
        $APO.CVEL={{APO_CVEL}}
        $ACC.CP = 5.0
        $VEL.CP={{PRINT_SPEED}}
        ;ENDFOLD (PRESETS)

        ;FOLD TIMER
        WAIT SEC 0
        $TIMER_STOP[7] = TRUE
        $TIMER[7] = 0
        $TIMER_STOP[7] = FALSE
        ;ENDFOLD

        BAS(#BASE,{{BASE_NO}})
        BAS(#VEL_PTP,10)
        {{HOME_PTP}}
        """;

    /// <summary>Default KRL footer template.</summary>
    public const string DefaultFooterTemplate = """
        $OUT[7]=FALSE
        $OUT[8] = FALSE
        $OUT[9] = FALSE
        END
        """;

    public static string Export(Toolpath toolpath, KrlExportSettings s)
    {
        var sb   = new StringBuilder(64 * 1024);
        var name = SafeName(s.ProgramName);

        WriteHeader(sb, name, s);

        var firstEntry = FindFirstExtrude(toolpath);
        if (firstEntry is null)
        {
            WriteFooter(sb, s);
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
        bool isFirstPrintStart = true;
        bool inZHopSequence = false;

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
                    if (move.IsZHop)
                    {
                        if (!inZHopSequence)
                        {
                            sb.AppendLine(";z-hop");
                            if (s.TravelSetAnout4Zero)
                                sb.AppendLine("$ANOUT[4] = 0.000 ; extruder off");
                            else
                                sb.AppendLine(FormatTriggerAnout4(ResolveAnout4IdleText(s), "RPM idle"));
                            inZHopSequence = true;
                        }

                        var zHopSpeed = move.TravelSpeedMps ?? s.TravelSpeedMps;
                        sb.AppendLine($"$VEL.CP = {zHopSpeed.ToString("F6", Inv)}");
                        var (za, zb, zc) = lastAbc;
                        sb.AppendLine(FormatLinExact(to, za, zb, zc));
                        lastPos = to;
                        needsRpmOn = true;
                        continue;
                    }

                    inZHopSequence = false;
                    sb.AppendLine(move.IsLayerChange ? ";layer change" : move.IsMergeConnector ? ";merge travel" : ";travel");
                    if (s.TravelSetAnout4Zero)
                        sb.AppendLine("$ANOUT[4] = 0.000 ; extruder off");
                    else
                        sb.AppendLine(FormatTriggerAnout4(ResolveAnout4IdleText(s), "RPM idle"));
                    var travelSpeed = move.TravelSpeedMps ?? s.TravelSpeedMps;
                    sb.AppendLine($"$VEL.CP = {travelSpeed.ToString("F6", Inv)}");
                    var (ta, tb, tc) = lastAbc;
                    sb.AppendLine(FormatLinExact(to, ta, tb, tc));
                    sb.AppendLine();
                    needsRpmOn = true;
                }
                else
                {
                    inZHopSequence = false;
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

                    if (move.IsWipe)
                    {
                        sb.AppendLine(";wipe");
                        sb.AppendLine(FormatDirectAnout4(
                            ResolveAnout4ExtrudeText(s, move.WipeRpmScale), "wipe"));
                        sb.AppendLine($"$VEL.CP = {s.WipeSpeedMps.ToString("F6", Inv)}");
                        sb.AppendLine(FormatLin(to, ma, mb, mc));
                        lastAbc = (ma, mb, mc);
                        lastPos = to;
                        continue;
                    }

                    if (move.IsResumeRamp)
                    {
                        if (needsRpmOn)
                        {
                            float waitSec = isFirstPrintStart
                                ? s.ExtrusionStartWaitSec
                                : s.ExtrusionResumeWaitSec;
                            if (waitSec > 0f)
                            {
                                sb.AppendLine(FormatDirectAnout4(
                                    ResolveAnout4ExtrudeText(s, move.ResumeRpmScale), "RPM ramp"));
                                sb.AppendLine(FormatWaitSec(waitSec));
                            }
                            else
                            {
                                sb.AppendLine(FormatDirectAnout4(
                                    ResolveAnout4ExtrudeText(s, move.ResumeRpmScale), "RPM ramp"));
                            }

                            isFirstPrintStart = false;
                            needsRpmOn = false;
                        }
                        else
                        {
                            sb.AppendLine(FormatDirectAnout4(
                                ResolveAnout4ExtrudeText(s, move.ResumeRpmScale), "RPM ramp"));
                        }

                        float rampSpeed = s.PrintSpeedMps * move.ResumeSpeedScale;
                        sb.AppendLine($"$VEL.CP = {rampSpeed.ToString("F6", Inv)}");
                        sb.AppendLine(FormatLin(to, ma, mb, mc));
                        lastAbc = (ma, mb, mc);
                        lastPos = to;
                        continue;
                    }

                    if (needsRpmOn)
                    {
                        float waitSec = isFirstPrintStart
                            ? s.ExtrusionStartWaitSec
                            : s.ExtrusionResumeWaitSec;
                        if (waitSec > 0f)
                        {
                            sb.AppendLine(FormatDirectAnout4(ResolveAnout4ExtrudeText(s), "RPM on"));
                            sb.AppendLine(FormatWaitSec(waitSec));
                        }
                        else
                            sb.AppendLine(FormatTriggerAnout4(ResolveAnout4ExtrudeText(s), "RPM on"));

                        sb.AppendLine($"$VEL.CP = {s.PrintSpeedMps.ToString("F6", Inv)}");
                        isFirstPrintStart = false;
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
        if (s.TravelSetAnout4Zero)
            sb.AppendLine("$ANOUT[4] = 0.000 ; extruder off");
        else
            sb.AppendLine(FormatTriggerAnout4(ResolveAnout4IdleText(s), "RPM idle"));
        sb.AppendLine($"$VEL.CP = {s.TravelSpeedMps.ToString("F6", Inv)}");
        sb.AppendLine(FormatLinExact(new Vector3(lastPos.X, lastPos.Y, lastPos.Z + s.ApproachZMm), fa, fb, fc));
        sb.AppendLine();
        WriteFooter(sb, s);

        return sb.ToString();
    }

    // -- Helpers ---------------------------------------------------------------

    private static void WriteHeader(StringBuilder sb, string name, KrlExportSettings s)
    {
        var template = string.IsNullOrWhiteSpace(s.HeaderTemplate) ? DefaultHeaderTemplate : s.HeaderTemplate!;
        AppendRenderedTemplate(sb, RenderHeaderTemplate(template, name, s));
    }

    private static void WriteFooter(StringBuilder sb, KrlExportSettings s)
    {
        var template = string.IsNullOrWhiteSpace(s.FooterTemplate) ? DefaultFooterTemplate : s.FooterTemplate!;
        AppendRenderedTemplate(sb, template.TrimEnd());
    }

    private static void AppendRenderedTemplate(StringBuilder sb, string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            sb.AppendLine(line);
    }

    internal static string RenderHeaderTemplate(string template, string programName, KrlExportSettings s)
    {
        var h = s.HomePosition;
        var homePtp = $"PTP {{A1 {h[0].ToString("F3", Inv)}, A2 {h[1].ToString("F3", Inv)}, " +
                      $"A3 {h[2].ToString("F3", Inv)}, A4 {h[3].ToString("F3", Inv)}, " +
                      $"A5 {h[4].ToString("F3", Inv)}, A6 {h[5].ToString("F3", Inv)}}}";

        return template
            .Replace("{{PROGRAM_NAME}}", programName)
            .Replace("{{TOOL_NO}}",      s.ToolDataIndex.ToString(Inv))
            .Replace("{{BASE_NO}}",      s.BaseDataIndex.ToString(Inv))
            .Replace("{{TEMP1_V}}",       ResolveTempAnoutText(s, 1))
            .Replace("{{TEMP2_V}}",       ResolveTempAnoutText(s, 2))
            .Replace("{{TEMP3_V}}",       ResolveTempAnoutText(s, 3))
            .Replace("{{TEMP1_C}}",       s.Temperature1.ToString("F0", Inv))
            .Replace("{{TEMP2_C}}",       s.Temperature2.ToString("F0", Inv))
            .Replace("{{TEMP3_C}}",       s.Temperature3.ToString("F0", Inv))
            .Replace("{{RPM_IDLE_V}}",    ResolveAnout4IdleText(s))
            .Replace("{{PRINT_SPEED}}",   s.PrintSpeedMps.ToString("F6", Inv))
            .Replace("{{APO_CVEL}}",      s.ApoCvel.ToString(Inv))
            .Replace("{{HOME_PTP}}",      homePtp);
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

    private static string FormatTriggerAnout4(string text, string comment)
        => $"TRIGGER WHEN DISTANCE=0 DELAY=0 DO $ANOUT[4]={text} ; {comment}";

    private static string FormatDirectAnout4(string text, string comment)
        => $"$ANOUT[4] = {text} ; {comment}";

    private static string FormatWaitSec(float seconds)
    {
        string text = seconds.ToString(seconds % 1f == 0f ? "F0" : "F1", Inv);
        return $"WAIT SEC {text}";
    }

    private static string ResolveTempAnoutText(KrlExportSettings s, int channel)
    {
        string? o = channel switch
        {
            1 => s.Anout1Text,
            2 => s.Anout2Text,
            3 => s.Anout3Text,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(o))
            return o.Trim();

        return channel switch
        {
            1 => KrlAnout.TempToAnoutText(s.Temperature1),
            2 => KrlAnout.TempToAnoutText(s.Temperature2),
            3 => KrlAnout.TempToAnoutText(s.Temperature3),
            _ => "0",
        };
    }

    private static string ResolveAnout4IdleText(KrlExportSettings s)
        => string.IsNullOrWhiteSpace(s.Anout4IdleText)
            ? KrlAnout.RpmIdleAnoutText
            : s.Anout4IdleText.Trim();

    private static string ResolveAnout4ExtrudeText(KrlExportSettings s, float rpmScale = 1f)
    {
        if (!string.IsNullOrWhiteSpace(s.Anout4ExtrudeText) && Math.Abs(rpmScale - 1f) < 1e-4f)
            return s.Anout4ExtrudeText.Trim();

        float rpmPercent = s.ExtrusionRpmPercent
            ?? KrlAnout.ComputeRpmPercent(s.BeadWidthMm, s.LayerHeightMm, s.PrintSpeedMps, s.FlowRate);
        rpmPercent *= Math.Clamp(rpmScale, 0f, 1f);
        return KrlAnout.RpmPercentToAnoutText(rpmPercent);
    }

    private static string SafeName(string raw)
        => Regex.Replace(raw.Trim(), @"[^A-Za-z0-9_]", "_");
}
