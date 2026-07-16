using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using MassiveSlicer.Core.Kinematics;
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
    public float ExtrusionStartWaitSec { get; init; }

    /// <summary>
    /// Pause (seconds) after each travel before the next extrusion move. 0 = disabled.
    /// Uses immediate <c>$ANOUT[4]</c> so the wait happens after RPM changes.
    /// Per-move <see cref="Models.ToolpathMove.ResumeWaitSec"/> overrides this when set.
    /// </summary>
    public float ExtrusionResumeWaitSec { get; init; }

    /// <summary>
    /// Digital Start/Stop (URM / ultra-responsive): Caracol Travel Editor injector pattern
    /// from the S&amp;S handover (<c>travel_ss-post.txt</c>).
    /// <list type="bullet">
    /// <item><c>$OUT[8]</c> (URM request) = TRUE only around travels (verified LFAM wiring
    /// 2026-07-16: OUT[8] -> DI_01_URM; the Caracol slide deck's OUT[9] numbering is WRONG
    /// for these machines)</item>
    /// <item><c>$OUT[9]</c> (robot-mode gate / MIO_req) = latched TRUE for the whole job in MAT</item>
    /// <item><c>$OUT[7]</c> = FALSE before travel, TRUE after travel (screw enable)</item>
    /// <item>Pre-travel WAIT + half print speed approach; post-travel resume WAIT</item>
    /// </list>
    /// </summary>
    public bool DigitalStartStopEnabled { get; init; }

    /// <summary>Caracol S&amp;S: wait after screw-off before travel motion (sec). Default 0.5.</summary>
    public float SsPreTravelWaitSec { get; init; } = 0.5f;

    /// <summary>
    /// Caracol S&amp;S: print-speed scale for the final approach after OUT[7]=FALSE
    /// (handover suggests ~50%). Default 0.5.
    /// </summary>
    public float SsApproachSpeedScale { get; init; } = 0.5f;

    public float[] HomePosition { get; init; } = [0f, -90f, 90f, 0f, 15f, 0f];
    /// <summary>LFAM 1 rail: E1 position (mm) emitted in the header HOME PTP. NaN = omit.</summary>
    public float HomeE1Mm { get; init; } = float.NaN;

    /// <summary>
    /// When true, LIN points use a planned E1 that tracks the path along the rail
    /// within <see cref="E1YPlusMm"/> / <see cref="E1YMinusMm"/> of <see cref="HomeE1Mm"/>.
    /// Reduces arm reach / singularity issues when the tool is tilted.
    /// </summary>
    public bool E1MotionEnabled { get; init; }

    /// <summary>Max E1 travel (mm) positive from home when <see cref="E1MotionEnabled"/>.</summary>
    public float E1YPlusMm { get; init; } = 500f;

    /// <summary>Max E1 travel (mm) negative from home when <see cref="E1MotionEnabled"/>.</summary>
    public float E1YMinusMm { get; init; } = 500f;

    /// <summary>Rail soft min (mm). Used with motion enable.</summary>
    public float RailMinMm { get; init; } = -4650f;

    /// <summary>Rail soft max (mm).</summary>
    public float RailMaxMm { get; init; } = 150f;

    /// <summary>Rail axis: X, Y, or Z (LFAM 1 is typically Y).</summary>
    public string RailAxis { get; init; } = "Y";

    /// <summary>Sign mapping E1 mm → scene translation along the rail axis.</summary>
    public float RailE1Sign { get; init; } = 1f;
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
    /// <summary>
    /// World Z of the slicer build plane (<c>SceneRenderer.BedZ</c>). When it differs from
    /// <c>RobrootWorldPos.Z + BaseDataOffset.Z</c> (KUKA BASE Z=0 surface), export lifts
    /// toolpath Z so BASE coordinates match the controller — e.g. LFAM 1 visual bed vs BASE_DATA.
    /// </summary>
    public float SliceBedWorldZ { get; init; } = float.NaN;

    /// <summary>Emit <c>$ANOUT[4] = 0</c> before travel moves instead of a TRIGGER idle pulse.</summary>
    public bool TravelSetAnout4Zero { get; init; } = true;

    // -- Milling (subtractive) --------------------------------------------------

    /// <summary>When true, export a relief-milling program (LIN cuts + rapids, no extruder/temperature).</summary>
    public bool IsMilling { get; init; }
    /// <summary>Spindle RPM (informational; used by the mill header template token).</summary>
    public float SpindleRpm { get; init; } = 12000f;
    /// <summary>Cutting feed (mm/min) for mill moves.</summary>
    public float CuttingFeedMmMin { get; init; } = 3000f;
    /// <summary>Plunge feed (mm/min) for downward mill approaches.</summary>
    public float PlungeFeedMmMin { get; init; } = 1000f;

    /// <summary>
    /// Custom header template. Null/empty uses the built-in default for the export mode.
    /// Ignored when <see cref="DigitalStartStopEnabled"/> (URM always uses
    /// <see cref="KrlExporter.DefaultUrmHeaderTemplate"/> so LFAM post-process
    /// <c>$ANOUT</c> MAT blocks cannot override Caracol <c>T1/T2/T3/RPM</c>).
    /// </summary>
    public string? HeaderTemplate { get; init; }

    /// <summary>
    /// Custom footer template. Null/empty uses the built-in default.
    /// Ignored when <see cref="DigitalStartStopEnabled"/> (URM uses
    /// <see cref="KrlExporter.DefaultUrmFooterTemplate"/>).
    /// </summary>
    public string? FooterTemplate { get; init; }
}

public static class KrlExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Default KRL header template with {{PLACEHOLDER}} tokens (LFAM ANOUT path).</summary>
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

    /// <summary>
    /// URM / Digital Start-Stop header matching Caracol Eidos
    /// <c>2026_0520 MTruck Wheel.src</c> (ASCII-only for PointLoader).
    /// Uses Caracol globals <c>T1/T2/T3/RPM</c> instead of <c>$ANOUT</c>.
    /// </summary>
    public const string DefaultUrmHeaderTemplate = """
        &ACCESS RVP
        DEF {{PROGRAM_NAME}}()

        ;MFM
        ;HF & HV SAFETY & FLANGE
        ;TURN ON EXTRUDER AT START
        ;TURN EXTRUDER & AIR OFF AT END

        ;FOLD INI
        ;FOLD BASISTECH INI
        GLOBAL INTERRUPT DECL 3 WHEN $STOPMESS==TRUE DO IR_STOPM ( )
        INTERRUPT ON 3

        ;FOLD CaracolSafety

        INTERRUPT DECL 1 WHEN $STOPMESS==TRUE DO STOPEXTRHF()
        INTERRUPT ON 1

        ;in 5 -> Antincendio
        ;in 6 -> PLC signal - extrusion enabled
        ;in 7 -> flangia anti caduta

        $CYCFLAG[2] = ($IN[5]==TRUE) OR ($IN[6]==FALSE) OR ($IN[7] == TRUE)
        INTERRUPT DECL 4 WHEN $CYCFLAG[2] DO FULLREMOTESTOPHF()
        INTERRUPT ON 4

        ;ENDFOLD(CaracolSafety)

        BAS (#INITMOV,0 )
        ;ENDFOLD (BASISTECH INI)

        ;ENDFOLD (INI)

        ; Digital outs idle — do NOT zero T1/T2/T3 (keeps extruder heaters on).
        ; OUT[8] (URM / ultra-responsive) is pulsed only around travels — not latched ON here.
        ; OUT[9] is the robot-mode gate (MIO_req) — latched ON in MAT below.
        $OUT[7] = FALSE
        $OUT[8] = FALSE
        RPM = 0.00
        WAIT SEC 1

        ;Extruder enable before Mat settings (no HALT — temps stay commanded on).
        $OUT[7]=TRUE

        ;FOLD MAT out of INI

        ;robot-mode gate (MIO_req) ON for the whole print - CARACOL obeys T/RPM only while high
        $OUT[9] = TRUE

        ;Re-latch guard: ANALOGHANDLER only writes $ANOUT when T changes. A prior program end
        ;can leave $ANOUT at 0 while T still reads the target, so setting the same target does
        ;nothing and the extruder stays cold. Command a slightly lower temp first (still hot if
        ;paused), then the target, forcing a change the converter must act on.
        T1 = {{TEMP1_NUDGE_C}}
        T2 = {{TEMP2_NUDGE_C}}
        T3 = {{TEMP3_NUDGE_C}}
        WAIT SEC 0.4
        T1 = {{TEMP1_C}}
        T2 = {{TEMP2_C}}
        T3 = {{TEMP3_C}}

        RPM = 1

        ;ENDFOLD MAT

        ;FOLD CheckFlange
        IF $IN[7]== TRUE THEN
        msgnotify("!!! The flange is currently detached - place it back in position and press play")

        WAIT FOR $IN[7]==FALSE
        HALT ;press ">" to go forward
        ENDIF

        ;ENDFOLD(CheckFlange)

        ;FOLD DISCLAIMER

        ;This is a part program generated by MassiveSLICER. It is intended for use on
        ;authorized industrial robot / LFAM cells under the operator's own process control.

        ;Review all motions, speeds, temperatures, and I/O before running. The operator is
        ;responsible for verifying reachability, tool/base data, safety interlocks, and
        ;material process parameters on the controller.

        ;Do not:
        ; - run this program on an uncommissioned or incorrectly configured cell;
        ; - bypass safety systems, interlocks, or flange / extrusion enable checks;
        ; - treat generated paths as verified without simulation and dry-run review.

        ;MassiveSLICER and its authors assume no liability for machine damage, material
        ;loss, or injury arising from use of this program. Use is at your own risk and
        ;subject to your site safety procedures and applicable law.

        ;ENDFOLD (DISCLAIMER)

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

        ;FOLD READYTOPRINT
        ;IF IN[6] DOES NOT CONNECT EXTRUDER WILL NOT START
        WAIT FOR $IN[6]==TRUE
        ;ENDFOLD (READYTOPRINT)
        BAS(#BASE,{{BASE_NO}})
        BAS(#VEL_PTP,10)
        {{HOME_PTP}}
        """;

    /// <summary>Default KRL footer template (LFAM).</summary>
    public const string DefaultFooterTemplate = """
        $OUT[7]=FALSE
        $OUT[8] = FALSE
        $OUT[9] = FALSE
        END
        """;

    /// <summary>
    /// Caracol S&amp;S footer (matches travel_ss-post): air off, URM off, screw off, timer stop.
    /// Temperatures (T1/T2/T3) are left as-is so the extruder stays hot between runs.
    /// </summary>
    public const string DefaultUrmFooterTemplate = """
        ;AIR COMMAND
        $OUT[5]=FALSE
        ;ULTRARESPONSIVE MODE OFF
        $OUT[8]=FALSE
        WAIT SEC 2
        ;EXTRUDER MOTOR COMMAND
        $OUT[7]=FALSE
        $TIMER_STOP[ 7 ]=TRUE
        ;ROBOT-MODE GATE OFF
        $OUT[9] = FALSE
        RPM = 0.00
        END
        """;

    /// <summary>Default milling header — no extruder/temperature. Spindle on/RPM is a placeholder
    /// the operator wires to the real channel (KUKA 0-10V -> ATV340 VFD).</summary>
    public const string DefaultMillHeaderTemplate = """
        &ACCESS RVP
        DEF {{PROGRAM_NAME}} ()

        ;FOLD INI
        ;FOLD BASISTECH INI
        GLOBAL INTERRUPT DECL 3 WHEN $STOPMESS==TRUE DO IR_STOPM ( )
        INTERRUPT ON 3
        BAS (#INITMOV,0 )
        ;ENDFOLD (BASISTECH INI)
        ;ENDFOLD (INI)

        ;FOLD PRESETS
        PDAT_ACT = {VEL 6,ACC 100,APO_DIST 50}
        FDAT_ACT = {TOOL_NO {{TOOL_NO}},BASE_NO {{BASE_NO}},IPO_FRAME #BASE}
        BAS (#PTP_PARAMS,6)
        $ADVANCE=5
        $APO.CVEL={{APO_CVEL}}
        $ACC.CP = 5.0
        $VEL.CP={{FEED_MPS}}
        ;ENDFOLD (PRESETS)

        ;FOLD SPINDLE ON -- wire to the real spindle channel (e.g. $ANOUT[n] = 0-10V for {{SPINDLE_RPM}} RPM)
        ;$ANOUT[n] = <0-10V for {{SPINDLE_RPM}} RPM>
        ;WAIT SEC 2 ; spindle spin-up
        ;ENDFOLD

        BAS(#BASE,{{BASE_NO}})
        BAS(#VEL_PTP,10)
        {{HOME_PTP}}
        """;

    /// <summary>
    /// Exports the toolpath as a KUKA KRL <c>.src</c> program. Output always uses CRLF line
    /// endings: KRL runs on the Windows-based KRC controller and its PointLoader tool, which
    /// mis-parse LF-only files (StringBuilder.AppendLine would otherwise emit LF on macOS/Linux).
    /// </summary>
    public static string Export(Toolpath toolpath, KrlExportSettings s)
        => ExportCore(toolpath, s).ReplaceLineEndings("\r\n");

    private static string ExportCore(Toolpath toolpath, KrlExportSettings s)
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

        if (s.IsMilling)
        {
            WriteMillBody(sb, toolpath, s);
            WriteFooter(sb, s);
            return sb.ToString();
        }

        var (firstMove, firstLayer) = firstEntry.Value;
        var (a0, b0, c0) = KukaAbc(firstLayer.PlaneNormal, s);
        var p0 = ToBase(firstMove.From, s);
        float lastE1 = float.IsNaN(s.HomeE1Mm) ? 0f : s.HomeE1Mm;

        // -- Initial approach -----------------------------------------------------
        sb.AppendLine($"$VEL.CP = {s.TravelSpeedMps.ToString("F6", Inv)}");
        float e1Approach = E1ForBase(p0, s, ref lastE1);
        sb.AppendLine(FormatLin(new Vector3(p0.X, p0.Y, p0.Z + s.ApproachZMm), a0, b0, c0, e1Approach));
        // Exact stop at the touch-down point so the RPM-on TRIGGER fires at the
        // correct physical position (not inside a C_VEL blend zone).
        sb.AppendLine(FormatLinExact(p0, a0, b0, c0, e1Approach));
        // Caracol URM (MTruck): approach ends with ";travel end" before first ";rpm change".
        if (s.DigitalStartStopEnabled)
            sb.AppendLine(";travel end");
        sb.AppendLine();

        Vector3 lastPos = p0;
        (float a, float b, float c) lastAbc = (a0, b0, c0);
        bool needsRpmOn = true;
        bool isFirstPrintStart = true;
        bool inZHopSequence = false;
        // Caracol S&S: emit OUT[9]/OUT[7] stop block once at the start of a wipe/z-hop/travel run.
        bool ssStopActive = false;
        float? pendingResumeWaitSec = null;
        float lastExtrudeSpeedMps = -1f;
        float lastExtrudeRpmScale = -1f;
        string? lastExtrudeAnoutText = null;
        string? lastExtrudeVelText = null;

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
                    if (move.ResumeWaitSec is { } tw)
                        pendingResumeWaitSec = tw;

                    if (move.IsZHop)
                    {
                        if (!inZHopSequence)
                        {
                            sb.AppendLine(";z-hop");
                            if (s.DigitalStartStopEnabled && !ssStopActive)
                            {
                                var (za0, zb0, zc0) = lastAbc;
                                EmitCaracolSsPreTravel(sb, s, lastPos, za0, zb0, zc0,
                                    E1ForMove(move, to, s, ref lastE1),
                                    lastExtrudeSpeedMps > 0f ? lastExtrudeSpeedMps : s.PrintSpeedMps);
                                ssStopActive = true;
                            }
                            else if (!s.DigitalStartStopEnabled)
                                sb.AppendLine(FormatExtruderOff(s, "extruder off"));
                            inZHopSequence = true;
                        }

                        var zHopSpeed = move.TravelSpeedMps ?? s.TravelSpeedMps;
                        sb.AppendLine($"$VEL.CP = {zHopSpeed.ToString("F6", Inv)}");
                        var (za, zb, zc) = lastAbc;
                        if (s.DigitalStartStopEnabled)
                            sb.AppendLine(FormatLin(to, za, zb, zc, E1ForMove(move, to, s, ref lastE1)));
                        else
                            sb.AppendLine(FormatLinExact(to, za, zb, zc, E1ForMove(move, to, s, ref lastE1)));
                        lastPos = to;
                        needsRpmOn = true;
                        continue;
                    }

                    inZHopSequence = false;
                    var (ta, tb, tc) = lastAbc;
                    float e1Travel = E1ForMove(move, to, s, ref lastE1);
                    var travelSpeed = move.TravelSpeedMps ?? s.TravelSpeedMps;

                    if (s.DigitalStartStopEnabled)
                    {
                        // Caracol S&S injector (travel_ss-post / Handover V2).
                        if (!ssStopActive)
                        {
                            EmitCaracolSsPreTravel(sb, s, lastPos, ta, tb, tc, e1Travel,
                                lastExtrudeSpeedMps > 0f ? lastExtrudeSpeedMps : s.PrintSpeedMps);
                            ssStopActive = true;
                        }
                        string tag = move.IsLayerChange ? ";layer change"
                            : move.IsMergeConnector ? ";merge travel" : ";travel start";
                        sb.AppendLine(tag);
                        sb.AppendLine($"$VEL.CP = {travelSpeed.ToString("F6", Inv)}");
                        sb.AppendLine(FormatLin(to, ta, tb, tc, e1Travel));
                        sb.AppendLine(";travel end");
                        sb.AppendLine();
                    }
                    else
                    {
                        sb.AppendLine(move.IsLayerChange ? ";layer change" : move.IsMergeConnector ? ";merge travel" : ";travel");
                        sb.AppendLine(FormatExtruderOff(s, "extruder off"));
                        sb.AppendLine($"$VEL.CP = {travelSpeed.ToString("F6", Inv)}");
                        sb.AppendLine(FormatLinExact(to, ta, tb, tc, e1Travel));
                        sb.AppendLine();
                    }
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
                            ? KukaAbc(effectiveNormal, s, move.TcpYawDeg)
                            : move.TcpYawDeg != 0f
                                ? KukaAbc(layer.PlaneNormal, s, move.TcpYawDeg)
                                : (la, lb, lc);

                    if (move.IsWipe)
                    {
                        // Wipe is Caracol S&S step 5 — ensure stop block already fired.
                        sb.AppendLine(";wipe");
                        if (move.ResumeWaitSec is { } ww)
                            pendingResumeWaitSec = ww;
                        if (s.DigitalStartStopEnabled && !ssStopActive)
                        {
                            EmitCaracolSsPreTravel(sb, s, lastPos, ma, mb, mc,
                                E1ForMove(move, to, s, ref lastE1),
                                lastExtrudeSpeedMps > 0f ? lastExtrudeSpeedMps : s.PrintSpeedMps);
                            ssStopActive = true;
                        }
                        else if (!s.DigitalStartStopEnabled)
                            sb.AppendLine(FormatExtruderOff(s, "extruder off (wipe)"));
                        sb.AppendLine($"$VEL.CP = {s.WipeSpeedMps.ToString("F6", Inv)}");
                        sb.AppendLine(FormatLin(to, ma, mb, mc, E1ForMove(move, to, s, ref lastE1)));
                        lastAbc = (ma, mb, mc);
                        lastPos = to;
                        needsRpmOn = true;
                        continue;
                    }

                    if (move.IsResumeRamp)
                    {
                        float rpmScale  = EffectiveRpmScale(move);
                        float speedMps  = EffectivePrintSpeedMps(move, s);
                        if (needsRpmOn)
                        {
                            float waitSec = ResolveResumeWaitSec(
                                isFirstPrintStart, s, pendingResumeWaitSec, move.ResumeWaitSec);
                            pendingResumeWaitSec = null;
                            if (s.DigitalStartStopEnabled)
                            {
                                EmitCaracolSsPostTravel(sb, s, waitSec, rpmScale, isFirstPrintStart);
                                ssStopActive = false;
                            }
                            else
                            {
                                sb.AppendLine(FormatExtruderOn(s, rpmScale, "RPM ramp", useTrigger: false));
                                if (waitSec > 0f)
                                    sb.AppendLine(FormatWaitSec(waitSec));
                            }
                            isFirstPrintStart = false;
                            needsRpmOn = false;
                        }
                        else if (!s.DigitalStartStopEnabled)
                        {
                            sb.AppendLine(FormatExtruderOn(s, rpmScale, "RPM ramp", useTrigger: false));
                        }

                        sb.AppendLine($"$VEL.CP = {speedMps.ToString("F6", Inv)}");
                        sb.AppendLine(FormatLin(to, ma, mb, mc, E1ForMove(move, to, s, ref lastE1)));
                        lastAbc = (ma, mb, mc);
                        lastPos = to;
                        lastExtrudeSpeedMps = speedMps;
                        lastExtrudeRpmScale = rpmScale;
                        continue;
                    }

                    float extrudeRpmScale = EffectiveRpmScale(move);
                    float extrudeSpeedMps = EffectivePrintSpeedMps(move, s);
                    // Compare the *emitted* KRL text, not raw floats. Multi-planar HeightScale
                    // and layer-speed scales jitter every bead; if ANOUT/VEL round to the same
                    // digits, re-writing them between every LIN kills $ADVANCE continuous path
                    // and makes the robot stutter on dense clusters.
                    // Key for change-detection (same rate → same command text).
                    string extrudeKey = FormatExtruderOn(s, extrudeRpmScale, "", useTrigger: false);
                    string velText = extrudeSpeedMps.ToString("F6", Inv);
                    bool anoutChanged = !string.Equals(extrudeKey, lastExtrudeAnoutText, StringComparison.Ordinal);
                    bool velChanged = !string.Equals(velText, lastExtrudeVelText, StringComparison.Ordinal);

                    if (needsRpmOn)
                    {
                        float waitSec = ResolveResumeWaitSec(
                            isFirstPrintStart, s, pendingResumeWaitSec, move.ResumeWaitSec);
                        pendingResumeWaitSec = null;
                        // Caracol S&S post-travel (steps 8-10) or first start; non-URM = ANOUT path.
                        if (s.DigitalStartStopEnabled)
                        {
                            EmitCaracolSsPostTravel(sb, s, waitSec, extrudeRpmScale, isFirstPrintStart);
                            ssStopActive = false;
                        }
                        else if (waitSec > 0f)
                        {
                            sb.AppendLine(FormatExtruderOn(s, extrudeRpmScale, "RPM on", useTrigger: false));
                            sb.AppendLine(FormatWaitSec(waitSec));
                        }
                        else
                            sb.AppendLine(FormatExtruderOn(s, extrudeRpmScale, "RPM on", useTrigger: true));

                        sb.AppendLine($"$VEL.CP = {velText}");
                        isFirstPrintStart = false;
                        needsRpmOn = false;
                        lastExtrudeAnoutText = extrudeKey;
                        lastExtrudeVelText = velText;
                    }
                    else if (anoutChanged || velChanged)
                    {
                        if (anoutChanged && !s.DigitalStartStopEnabled)
                            sb.AppendLine(FormatExtruderOn(s, extrudeRpmScale, "layer speed", useTrigger: false));
                        else if (anoutChanged && s.DigitalStartStopEnabled)
                            sb.AppendLine(FormatExtruderOn(s, extrudeRpmScale, "", useTrigger: false));
                        if (velChanged)
                            sb.AppendLine($"$VEL.CP = {velText}");
                        lastExtrudeAnoutText = extrudeKey;
                        lastExtrudeVelText = velText;
                    }

                    sb.AppendLine(FormatLin(to, ma, mb, mc, E1ForMove(move, to, s, ref lastE1)));
                    lastExtrudeSpeedMps = extrudeSpeedMps;
                    lastExtrudeRpmScale = extrudeRpmScale;
                    lastAbc = (ma, mb, mc);
                }

                lastPos = to;
            }
        }

        // -- Final retreat --------------------------------------------------------
        var (fa, fb, fc) = lastAbc;
        sb.AppendLine(";retreat");
        sb.AppendLine(FormatExtruderOff(s, "extruder off"));
        sb.AppendLine($"$VEL.CP = {s.TravelSpeedMps.ToString("F6", Inv)}");
        sb.AppendLine(FormatLinExact(new Vector3(lastPos.X, lastPos.Y, lastPos.Z + s.ApproachZMm), fa, fb, fc, E1ForBase(lastPos, s, ref lastE1)));
        sb.AppendLine();
        WriteFooter(sb, s);

        return sb.ToString();
    }

    // -- Helpers ---------------------------------------------------------------

    private static void WriteHeader(StringBuilder sb, string name, KrlExportSettings s)
    {
        // URM / Digital Start-Stop always uses the Caracol Eidos header (MTruck style).
        // Export-to-Robot post-process still stores the LFAM $ANOUT MAT template; that must
        // not win when URM is checked or the robot gets ANOUT instead of T1/T2/T3/RPM.
        string template;
        if (s.IsMilling)
            template = string.IsNullOrWhiteSpace(s.HeaderTemplate) ? DefaultMillHeaderTemplate : s.HeaderTemplate!;
        else if (s.DigitalStartStopEnabled)
            // Honor an edited URM header (gear menu) as long as it is still URM-shaped;
            // otherwise fall back so URM mode never exports an ANOUT header by mistake.
            template = !string.IsNullOrWhiteSpace(s.HeaderTemplate)
                       && s.HeaderTemplate!.Contains("CaracolSafety", System.StringComparison.Ordinal)
                ? s.HeaderTemplate!
                : DefaultUrmHeaderTemplate;
        else
            template = string.IsNullOrWhiteSpace(s.HeaderTemplate) ? DefaultHeaderTemplate : s.HeaderTemplate!;
        AppendRenderedTemplate(sb, RenderHeaderTemplate(template, name, s));
    }



    private static float ResolveResumeWaitSec(
        bool isFirstPrintStart,
        KrlExportSettings s,
        float? pendingFromTravel,
        float? onThisMove)
    {
        if (isFirstPrintStart)
            return s.ExtrusionStartWaitSec;
        if (onThisMove is { } m)
            return m;
        if (pendingFromTravel is { } p)
            return p;
        return s.ExtrusionResumeWaitSec;
    }

    /// <summary>
    /// Caracol S&amp;S steps 1–4 (travel_ss-post / Handover V2): enable URM, stop screw,
    /// slow approach at half print speed, wait before travel motion.
    /// OUT[8] (URM request) is only TRUE during this window — not for the whole job.
    /// </summary>
    private static void EmitCaracolSsPreTravel(
        StringBuilder sb,
        KrlExportSettings s,
        Vector3 lastPos,
        float a, float b, float c,
        float e1,
        float printSpeedMps)
    {
        sb.AppendLine(";digital start/stop - stop (Caracol URM)");
        // Fire TRIGGERs at current pose (DISTANCE=0 on next motion).
        sb.AppendLine("TRIGGER WHEN DISTANCE=0 DELAY=0 DO $OUT[8] = TRUE");
        sb.AppendLine("TRIGGER WHEN DISTANCE=0 DELAY=0 DO $OUT[7] = FALSE");
        float scale = s.SsApproachSpeedScale > 0.05f && s.SsApproachSpeedScale <= 1f
            ? s.SsApproachSpeedScale : 0.5f;
        float approach = Math.Max(0.005f, printSpeedMps * scale);
        sb.AppendLine($"$VEL.CP = {approach.ToString("F6", Inv)}");
        // Re-issue current pose so TRIGGERs execute, then dwell.
        sb.AppendLine(FormatLinExact(lastPos, a, b, c, e1));
        float preWait = s.SsPreTravelWaitSec >= 0f ? s.SsPreTravelWaitSec : 0.5f;
        if (preWait > 0f)
            sb.AppendLine(FormatWaitSec(preWait));
    }

    /// <summary>
    /// Caracol S&amp;S steps 8–10: re-enable screw, resume wait, disable URM, set RPM for print.
    /// </summary>
    private static void EmitCaracolSsPostTravel(
        StringBuilder sb,
        KrlExportSettings s,
        float waitSec,
        float rpmScale,
        bool isFirstPrintStart)
    {
        sb.AppendLine(";digital start/stop - start (Caracol URM)");
        sb.AppendLine("$OUT[7] = TRUE");
        if (waitSec > 0f)
            sb.AppendLine(FormatWaitSec(waitSec));
        sb.AppendLine("TRIGGER WHEN DISTANCE=0 DELAY=0 DO $OUT[8] = FALSE");
        // Keep screw speed command (material RPM%) for the print segment.
        sb.AppendLine(FormatExtruderOn(s, rpmScale, isFirstPrintStart ? "RPM on" : "", useTrigger: false));
    }

    /// <summary>Emits a relief-milling body: LIN cuts at the cutting feed, rapids/plunges for
    /// repositioning. No extruder/temperature/wipe logic. Top-down spindle (layer PlaneNormal).</summary>
    private static void WriteMillBody(StringBuilder sb, Toolpath toolpath, KrlExportSettings s)
    {
        string cutV    = (s.CuttingFeedMmMin / 60000f).ToString("F6", Inv);
        string plungeV = (s.PlungeFeedMmMin / 60000f).ToString("F6", Inv);
        string rapidV  = s.TravelSpeedMps.ToString("F6", Inv);

        // Rapid to safe Z above the first cut.
        var first = FindFirstExtrude(toolpath)!.Value;
        var (a0, b0, c0) = KukaAbc(first.layer.PlaneNormal, s);
        var p0 = ToBase(first.move.From, s);
        float lastE1 = float.IsNaN(s.HomeE1Mm) ? 0f : s.HomeE1Mm;
        sb.AppendLine($"$VEL.CP = {rapidV}");
        sb.AppendLine(FormatLinExact(new Vector3(p0.X, p0.Y, p0.Z + s.ApproachZMm), a0, b0, c0, E1ForBase(p0, s, ref lastE1)));
        sb.AppendLine();

        foreach (var layer in toolpath.Layers)
        {
            var (la, lb, lc) = KukaAbc(layer.PlaneNormal, s);
            foreach (var move in layer.Moves)
            {
                var to = ToBase(move.To, s);
                // Multi-axis: orient the spindle to the per-move tool axis (surface normal) when
                // present; fall back to the layer plane normal (top-down) otherwise.
                var (a, b, c) = move.Normal != Vector3.Zero ? KukaAbc(move.Normal, s, move.TcpYawDeg)
                    : move.TcpYawDeg != 0f ? KukaAbc(layer.PlaneNormal, s, move.TcpYawDeg)
                    : (la, lb, lc);
                if (move.Kind == MoveKind.Mill)
                {
                    sb.AppendLine($"$VEL.CP = {cutV}");
                    sb.AppendLine(FormatLin(to, a, b, c, E1ForMove(move, to, s, ref lastE1)));
                }
                else // Travel: rapid (up/over) or plunge (down)
                {
                    bool plunging = move.To.Z < move.From.Z - 1e-4f;
                    sb.AppendLine(plunging ? ";plunge" : ";rapid");
                    sb.AppendLine($"$VEL.CP = {(plunging ? plungeV : rapidV)}");
                    sb.AppendLine(FormatLinExact(to, a, b, c, E1ForMove(move, to, s, ref lastE1)));
                }
            }
        }
        sb.AppendLine();
    }

    private static void WriteFooter(StringBuilder sb, KrlExportSettings s)
    {
        // Same rule as header: URM ignores LFAM post-process footer ($OUT only) and uses
        // Caracol air / temp / RPM shutdown (MTruck footer).
        string template;
        if (s.DigitalStartStopEnabled && !s.IsMilling)
            template = !string.IsNullOrWhiteSpace(s.FooterTemplate)
                       && s.FooterTemplate!.Contains("EXTRUDER MOTOR COMMAND", System.StringComparison.Ordinal)
                ? s.FooterTemplate!
                : DefaultUrmFooterTemplate;
        else
            template = string.IsNullOrWhiteSpace(s.FooterTemplate) ? DefaultFooterTemplate : s.FooterTemplate!;
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
                      $"A5 {h[4].ToString("F3", Inv)}, A6 {h[5].ToString("F3", Inv)}";
        if (!float.IsNaN(s.HomeE1Mm))
            homePtp += $", E1 {s.HomeE1Mm.ToString("F3", Inv)}";
        homePtp += "}";

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
            .Replace("{{TEMP1_NUDGE_C}}", System.Math.Max(150f, s.Temperature1 - 5f).ToString("F0", Inv))
            .Replace("{{TEMP2_NUDGE_C}}", System.Math.Max(150f, s.Temperature2 - 5f).ToString("F0", Inv))
            .Replace("{{TEMP3_NUDGE_C}}", System.Math.Max(150f, s.Temperature3 - 5f).ToString("F0", Inv))
            .Replace("{{RPM_IDLE_V}}",    ResolveAnout4IdleText(s))
            .Replace("{{PRINT_SPEED}}",   s.PrintSpeedMps.ToString("F6", Inv))
            .Replace("{{FEED_MPS}}",      (s.CuttingFeedMmMin / 60000f).ToString("F6", Inv))
            .Replace("{{SPINDLE_RPM}}",   s.SpindleRpm.ToString("F0", Inv))
            .Replace("{{APO_CVEL}}",      s.ApoCvel.ToString(Inv))
            .Replace("{{HOME_PTP}}",      homePtp);
    }

    private static (ToolpathMove move, ToolpathLayer layer)? FindFirstExtrude(Toolpath tp)
    {
        foreach (var layer in tp.Layers)
            foreach (var move in layer.Moves)
                if (move.Kind is MoveKind.Extrude or MoveKind.Mill)
                    return (move, layer);
        return null;
    }

    // -- Coordinate transform --------------------------------------------------

    private static Vector3 ToBase(Vector3 stored, KrlExportSettings s)
    {
        // stored positions are in original slice world space;
        // (stored - origin) * wt maps them to current world space.
        var world = Vector3.Transform(stored - s.NodeOrigin, s.NodeWorldTransform);
        float kukaBedZ = s.RobrootWorldPos.Z + s.BaseDataOffset.Z;
        if (!float.IsNaN(s.SliceBedWorldZ) && MathF.Abs(s.SliceBedWorldZ - kukaBedZ) > 0.5f)
            world.Z += kukaBedZ - s.SliceBedWorldZ;
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
    private static (float a, float b, float c) KukaAbc(Vector3 normal, KrlExportSettings s, float tcpYawDeg = 0f)
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

        // Optional print-neutral spin about the nozzle (approach) axis — used by the
        // singularity-avoidance repair pass.  The nozzle is rotationally symmetric, so
        // this changes only the wrist configuration, never the deposited bead.
        if (MathF.Abs(tcpYawDeg) > 1e-4f)
        {
            float cy = MathF.Cos(tcpYawDeg * D2R), sy = MathF.Sin(tcpYawDeg * D2R);
            var y2 = yF * cy + zF * sy;
            var z2 = zF * cy - yF * sy;
            yF = y2; zF = z2;
        }

        // Step 3: extract ZYX Euler from R_final
        float bRad = MathF.Atan2(-xF.Z, MathF.Sqrt(xF.X * xF.X + xF.Y * xF.Y));
        float aRad, cRad;
        // Threshold 0.05 rad ≈ cos(87°). Near B = ±90° the A and C axes are collinear
        // (gimbal lock): tiny XY noise in the normal produces huge atan2 swings that force
        // the KUKA to interpolate through spurious wrist rotations at reduced speed.
        // In that regime only (A − C) is physical: derive it from the y-axis (stable —
        // no atan2 blowup) so a deliberate TCP yaw survives; without yaw this yields the
        // same A = 0, C = 0 as before for clean vertical normals.
        if (MathF.Abs(MathF.Abs(bRad) - MathF.PI / 2f) < 0.05f)
        {
            // Gimbal lock (tool ~vertical): A and C are collinear. Recover the true azimuth
            // from the (stable) y-axis so a deliberate toolhead offset / singularity-repair yaw
            // survives; for a clean vertical normal with no yaw this yields A = 0 as before.
            aRad = MathF.Atan2(-yF.X, yF.Y);
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

    /// <summary>
    /// Rail E1 (mm) for a move. Priority:
    /// 1) Per-move <see cref="ToolpathMove.E1Mm"/> from reachability planner
    /// 2) Geometric track within Y+/Y− when motion enabled
    /// 3) Hold home E1
    /// </summary>
    private static float E1ForMove(ToolpathMove? move, Vector3 basePt, KrlExportSettings s, ref float lastE1)
    {
        float home = float.IsNaN(s.HomeE1Mm) ? 0f : s.HomeE1Mm;

        if (move is not null && !float.IsNaN(move.E1Mm))
        {
            lastE1 = move.E1Mm;
            return move.E1Mm;
        }

        if (!s.E1MotionEnabled || float.IsNaN(s.HomeE1Mm))
        {
            lastE1 = home;
            return home;
        }

        // Reconstruct world position along the rail: BASE + ROBROOT + BASE_DATA.
        var world = new Vector3(
            basePt.X + s.RobrootWorldPos.X + s.BaseDataOffset.X,
            basePt.Y + s.RobrootWorldPos.Y + s.BaseDataOffset.Y,
            basePt.Z + s.RobrootWorldPos.Z + s.BaseDataOffset.Z);
        var homeWorld = s.RobrootWorldPos;
        var rail = new RobotRailCellConfig
        {
            Axis = s.RailAxis,
            MinMm = s.RailMinMm,
            MaxMm = s.RailMaxMm,
            E1Sign = s.RailE1Sign,
        };
        float pick = RailE1Planner.PickBestE1(
            world, homeWorld, rail, home, s.E1YPlusMm, s.E1YMinusMm,
            lastE1, preferredHorizReachMm: 900f, inWorkspace: null, gridCount: 9);
        float e1 = RailE1Planner.SmoothToward(lastE1, pick, blend: 0.4f);
        e1 = RailE1Planner.ClampToAllowance(
            e1, home, s.E1YPlusMm, s.E1YMinusMm, s.RailMinMm, s.RailMaxMm);
        lastE1 = e1;
        return e1;
    }

    private static float E1ForBase(Vector3 basePt, KrlExportSettings s, ref float lastE1)
        => E1ForMove(null, basePt, s, ref lastE1);

    // C_VEL: approximate (blended) positioning — used for extrude moves so the robot
    // never fully stops mid-bead and maintains a smooth velocity profile.
    private static string FormatLin(Vector3 p, float a, float b, float c, float e1)
        => $"LIN {{X {p.X.ToString("F2", Inv)}, Y {p.Y.ToString("F2", Inv)}, Z {p.Z.ToString("F2", Inv)}, " +
           $"A {a.ToString("F3", Inv)}, B {b.ToString("F3", Inv)}, C {c.ToString("F3", Inv)}, " +
           $"E1 {e1.ToString("F3", Inv)}, E2 0.000, E3 0.000, E4 0.000, E5 0.000, E6 0.000 }} C_VEL";

    // Exact stop — used for travel/approach/retreat moves so TRIGGER WHEN DISTANCE=0
    // fires at the precise physical waypoint rather than inside a C_VEL blend zone.
    // This is the fix for the $ADVANCE look-ahead timing issue: with exact stop the
    // "path switchover point" (DISTANCE=0) coincides with the actual robot position.
    private static string FormatLinExact(Vector3 p, float a, float b, float c, float e1)
        => $"LIN {{X {p.X.ToString("F2", Inv)}, Y {p.Y.ToString("F2", Inv)}, Z {p.Z.ToString("F2", Inv)}, " +
           $"A {a.ToString("F3", Inv)}, B {b.ToString("F3", Inv)}, C {c.ToString("F3", Inv)}, " +
           $"E1 {e1.ToString("F3", Inv)}, E2 0.000, E3 0.000, E4 0.000, E5 0.000, E6 0.000 }}";

    private static string FormatTriggerAnout4(string text, string comment)
        => string.IsNullOrEmpty(comment)
            ? $"TRIGGER WHEN DISTANCE=0 DELAY=0 DO $ANOUT[4]={text}"
            : $"TRIGGER WHEN DISTANCE=0 DELAY=0 DO $ANOUT[4]={text} ; {comment}";

    private static string FormatDirectAnout4(string text, string comment)
        => string.IsNullOrEmpty(comment)
            ? $"$ANOUT[4] = {text}"
            : $"$ANOUT[4] = {text} ; {comment}";

    /// <summary>
    /// Extruder off. Normal export: <c>$ANOUT[4] = 0</c>.
    /// URM / Digital Start-Stop: Caracol Eidos <c>RPM = 0.00</c> (MTruck Wheel style; used on wipe/retreat/footer).
    /// </summary>
    private static string FormatExtruderOff(KrlExportSettings s, string comment)
    {
        if (s.DigitalStartStopEnabled)
            return string.IsNullOrEmpty(comment) ? "RPM = 0.00" : $"RPM = 0.00 ; {comment}";
        return FormatDirectAnout4("0.000", comment);
    }

    /// <summary>
    /// Extruder on / speed. Normal: <c>$ANOUT[4]</c> (or TRIGGER).
    /// URM / Digital Start-Stop: Caracol Eidos <c>RPM = N</c> (percent value, not ANOUT fraction).
    /// </summary>
    private static string FormatExtruderOn(
        KrlExportSettings s, float rpmScale, string comment, bool useTrigger)
    {
        if (s.DigitalStartStopEnabled)
        {
            float rpmPercent = ResolveRpmPercent(s, rpmScale);
            string val = FormatCaracolRpm(rpmPercent);
            return string.IsNullOrEmpty(comment) ? $"RPM = {val}" : $"RPM = {val} ; {comment}";
        }

        string text = ResolveAnout4ExtrudeText(s, rpmScale);
        return useTrigger ? FormatTriggerAnout4(text, comment) : FormatDirectAnout4(text, comment);
    }

    /// <summary>Caracol-style RPM literal (e.g. <c>9.64115</c>, <c>0.00</c>, <c>18</c>).</summary>
    private static string FormatCaracolRpm(float rpmPercent)
    {
        if (rpmPercent <= 0f) return "0.00";
        // Trim trailing zeros but keep useful precision like the MTruck Wheel export.
        string s = rpmPercent.ToString("0.#####", Inv);
        return string.IsNullOrEmpty(s) ? "0.00" : s;
    }

    private static float ResolveRpmPercent(KrlExportSettings s, float rpmScale)
    {
        float rpmPercent = s.ExtrusionRpmPercent
            ?? KrlAnout.ComputeRpmPercent(s.BeadWidthMm, s.LayerHeightMm, s.PrintSpeedMps, s.FlowRate);
        return Math.Min(rpmPercent * Math.Max(rpmScale, 0f), 100f);
    }

    private static string FormatWaitSec(float seconds)
    {
        // Preserve ms-level resume waits (0.25, 0.5, …). F1 rounds 0.25 → 0.2.
        string text = seconds.ToString("0.###", Inv);
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

    private static float EffectivePrintSpeedMps(ToolpathMove move, KrlExportSettings s)
    {
        float scale = Math.Max(move.PrintSpeedScale, 1e-6f);
        if (move.IsResumeRamp)
            scale *= Math.Max(move.ResumeSpeedScale, 1e-6f);
        return s.PrintSpeedMps * scale;
    }

    private static float EffectiveRpmScale(ToolpathMove move)
    {
        if (move.IsWipe)
            return move.WipeRpmScale;
        float scale = Math.Max(move.PrintSpeedScale, 1e-6f);
        if (move.IsResumeRamp)
            scale *= Math.Max(move.ResumeRpmScale, 1e-6f);
        // Multi-Planar wedge layers: flow follows the local thickness.
        scale *= Math.Max(move.HeightScale, 1e-6f);
        return scale;
    }

    private static string ResolveAnout4ExtrudeText(KrlExportSettings s, float rpmScale = 1f)
    {
        if (!string.IsNullOrWhiteSpace(s.Anout4ExtrudeText) && Math.Abs(rpmScale - 1f) < 1e-4f)
        {
            if (float.TryParse(s.Anout4ExtrudeText.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float raw))
                return KrlAnout.FormatAnout4(raw);
            return s.Anout4ExtrudeText.Trim();
        }

        float rpmPercent = s.ExtrusionRpmPercent
            ?? KrlAnout.ComputeRpmPercent(s.BeadWidthMm, s.LayerHeightMm, s.PrintSpeedMps, s.FlowRate);
        rpmPercent = Math.Min(rpmPercent * Math.Max(rpmScale, 0f), 100f);
        return KrlAnout.RpmPercentToAnoutText(rpmPercent);
    }

    /// <summary>
    /// KRL module/routine name: letters, digits, underscore only; must start with a letter
    /// (CadCommands / PointLoader report "(" expected when DEF starts with a digit).
    /// </summary>
    private static string SafeName(string raw)
    {
        var name = Regex.Replace(raw.Trim(), @"[^A-Za-z0-9_]", "_");
        name = Regex.Replace(name, @"_+", "_").Trim('_');
        if (string.IsNullOrEmpty(name))
            name = "PrintJob";
        // KRL identifiers cannot start with a digit.
        if (char.IsDigit(name[0]))
            name = "MS_" + name;
        // Keep DEF names short for older KRC / PointLoader limits.
        if (name.Length > 32)
            name = name[..32].TrimEnd('_');
        return name;
    }
}
