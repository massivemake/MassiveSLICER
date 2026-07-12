using System.Globalization;
using System.Numerics;
using System.Text;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Builds a purge-and-weigh KRL / CAD stream for <b>PointLoader</b>.
/// Motions are absolute PTP/LIN aggregates only (no variables / <c>$POS_ACT</c>),
/// because PointLoader cannot parse <c>LIN purgePos</c>.
/// </summary>
public static class MaterialCalibrationKrl
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Default standoff above the BASE Z=0 bed surface: 1 foot.</summary>
    public const float DefaultPurgeHeightMm = 304.8f;

    /// <summary>Approach / retract path speed (m/s).</summary>
    public const float DefaultTravelMps = 0.050f;

    public sealed class Settings
    {
        public string ProgramName { get; init; } = "MATERIAL_CAL";
        public string MaterialName { get; init; } = "";
        public float Temperature1 { get; init; } = 220f;
        public float Temperature2 { get; init; } = 220f;
        public float Temperature3 { get; init; } = 220f;
        /// <summary>Motor speed percent for <c>$ANOUT[4]</c> (e.g. 50 → 0.5).</summary>
        public float MotorPercent { get; init; } = 50f;
        /// <summary>How long to run the screw at <see cref="MotorPercent"/>.</summary>
        public float RunTimeSec { get; init; } = 60f;
        public float MaterialDensity { get; init; } = 1.05f;
        public int ToolDataIndex { get; init; } = 1;
        public int BaseDataIndex { get; init; } = 1;
        /// <summary>Six joint home angles (deg). Null → LFAM1 defaults.</summary>
        public float[]? HomePosition { get; init; }
        /// <summary>Rail E1 (mm) held for home + purge LINs.</summary>
        public float HomeE1Mm { get; init; }
        /// <summary>BASE-frame Z height for the purge (mm above bed surface).</summary>
        public float PurgeHeightMm { get; init; } = DefaultPurgeHeightMm;
        public float TravelMps { get; init; } = DefaultTravelMps;
        /// <summary>Extra lift after purge before home (mm along BASE Z).</summary>
        public float RetractMm { get; init; } = 50f;
        /// <summary>
        /// Absolute TCP at home in BASE frame (XY + ABC). When null, computed from
        /// home joints + tool via FK. Z is ignored (replaced by <see cref="PurgeHeightMm"/>).
        /// </summary>
        public CartesianPose? HomeTcpBase { get; init; }
    }

    public readonly record struct CartesianPose(float X, float Y, float Z, float A, float B, float C);

    /// <summary>Suggested program / file stem from a material display name.</summary>
    public static string SuggestProgramName(string? materialName)
    {
        string raw = string.IsNullOrWhiteSpace(materialName) ? "MATERIAL_CAL" : materialName.Trim();
        var sb = new StringBuilder(raw.Length + 8);
        sb.Append("MatCal_");
        foreach (char c in raw)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is ' ' or '-' or '_') sb.Append('_');
        }
        string s = sb.ToString().Trim('_');
        while (s.Contains("__", StringComparison.Ordinal))
            s = s.Replace("__", "_", StringComparison.Ordinal);
        if (s.Length <= 8) s = "MatCal_Material";
        if (s.Length > 48) s = s[..48];
        return s;
    }

    /// <summary>
    /// Estimates home TCP in BASE frame from joint home + tool + E1 rail (PointLoader needs absolute XYZABC).
    /// </summary>
    public static CartesianPose EstimateHomeTcpInBase(
        float[] homeKrlDeg,
        float homeE1Mm,
        CellConfig cell,
        ToolCellConfig? tool = null)
    {
        tool ??= cell.EffectiveTools.FirstOrDefault(t => t.Default)
              ?? cell.EffectiveTools.FirstOrDefault();

        var rob = cell.Robot.WorldPosition;
        var bas = cell.Bed.BaseData;
        var robroot = new Vector3(rob.X, rob.Y, rob.Z);
        var tcpOff = tool is null
            ? Vector3.Zero
            : new Vector3(tool.TcpX, tool.TcpY, tool.TcpZ);

        // Arm FK is relative to ROBROOT at E1 = 0; shift by rail for current E1.
        var tcpWorld = KukaIkSolver.ComputeTcpWorldPos(homeKrlDeg, robroot, tcpOff);
        if (cell.RobotRail is { } rail)
        {
            var d = rail.SceneOffsetMm(homeE1Mm);
            tcpWorld += new Vector3(d.X, d.Y, d.Z);
        }

        // World → BASE (same as KrlExporter.ToBase without mesh origin).
        float bx = tcpWorld.X - rob.X - bas.X;
        float by = tcpWorld.Y - rob.Y - bas.Y;
        float bz = tcpWorld.Z - rob.Z - bas.Z;

        float a = 0f, b = 90f, c = 0f; // nozzle-down fallback
        try
        {
            var fk = KukaIkSolver.ForwardKinematics(homeKrlDeg);
            var flangeR = new Matrix4x4(
                fk.M11, fk.M12, fk.M13, 0,
                fk.M21, fk.M22, fk.M23, 0,
                fk.M31, fk.M32, fk.M33, 0,
                0, 0, 0, 1);
            var toolR = tool is null
                ? Matrix4x4.Identity
                : KukaIkSolver.AbcToMatrix(tool.TcpA, tool.TcpB, tool.TcpC);
            if (tool is { ToolFrameRoll: not 0 })
            {
                float roll = tool.ToolFrameRoll * MathF.PI / 180f;
                toolR = Matrix4x4.CreateRotationZ(roll) * toolR;
            }
            // Row-vector compose: apply flange, then tool.
            var combined = toolR * flangeR;
            (a, b, c) = KukaIkSolver.MatrixToAbc(combined);
        }
        catch
        {
            // keep nozzle-down fallback
        }

        return new CartesianPose(bx, by, bz, a, b, c);
    }

    public static string Generate(Settings s)
    {
        float motor = Math.Clamp(s.MotorPercent, 0.1f, 100f);
        float time  = Math.Max(1f, s.RunTimeSec);
        float zPurge = Math.Max(1f, s.PurgeHeightMm);
        float zRetract = zPurge + Math.Max(0f, s.RetractMm);
        float[] home = s.HomePosition is { Length: >= 6 } h
            ? h
            : [0f, -90f, 90f, 0f, 15f, 0f];
        float e1 = s.HomeE1Mm;

        var tcp = s.HomeTcpBase ?? new CartesianPose(0f, 0f, zPurge, 0f, 90f, 0f);
        // Keep home XY + ABC; only Z is the purge / retract height.
        float x = tcp.X, y = tcp.Y, a = tcp.A, b = tcp.B, c = tcp.C;

        string rpmAnout  = KrlAnout.RpmPercentToAnoutText(motor);
        string idleAnout = KrlAnout.RpmIdleAnoutText;
        string t1 = KrlAnout.TempToAnoutText(s.Temperature1);
        string t2 = KrlAnout.TempToAnoutText(s.Temperature2);
        string t3 = KrlAnout.TempToAnoutText(s.Temperature3);
        string prog = SanitizeProgramName(s.ProgramName);
        string vel = s.TravelMps.ToString("F6", Inv);

        var sb = new StringBuilder(4096);
        // PointLoader ignores DEF/END/header noise and streams the body.
        // Absolute PTP/LIN only — no DECL / $POS_ACT / named E6POS.
        sb.AppendLine("&ACCESS RVP");
        sb.AppendLine($"DEF {prog} ( )");
        sb.AppendLine();
        sb.AppendLine("; ============================================================");
        sb.AppendLine("; MassiveSLICER — Material Calibration (PointLoader stream)");
        sb.AppendLine("; Load this file with PointLoader → Load → Start RunPointLoader.");
        sb.AppendLine($"; Material : {(string.IsNullOrWhiteSpace(s.MaterialName) ? "(unnamed)" : s.MaterialName)}");
        sb.AppendLine($"; Temps    : T1={s.Temperature1:0}  T2={s.Temperature2:0}  T3={s.Temperature3:0} °C");
        sb.AppendLine($"; Motor    : {motor.ToString("0.#", Inv)} %  →  $ANOUT[4] = {rpmAnout}");
        sb.AppendLine($"; Run time : {time.ToString("0.#", Inv)} s");
        sb.AppendLine($"; Density  : {s.MaterialDensity.ToString("0.###", Inv)} g/cm³");
        sb.AppendLine($"; Motion   : PTP home joints → LIN home XY @ Z={zPurge.ToString("0.#", Inv)} mm (1 ft)");
        sb.AppendLine($"; Home TCP : X={x.ToString("0.##", Inv)} Y={y.ToString("0.##", Inv)} A={a.ToString("0.##", Inv)} B={b.ToString("0.##", Inv)} C={c.ToString("0.##", Inv)} E1={e1.ToString("0.##", Inv)}");
        sb.AppendLine("; (XY/ABC estimated from cell home + tool FK — verify in T1 before AUT)");
        sb.AppendLine("; Place a catch pan under the nozzle. Do not leave unattended.");
        sb.AppendLine("; ============================================================");
        sb.AppendLine();

        // Process / handshake — PointLoader maps these into CadCommands
        sb.AppendLine("$OUT[9] = TRUE");
        sb.AppendLine($"$ANOUT[1] = {t1} ; T1 = {s.Temperature1.ToString("F0", Inv)}C");
        sb.AppendLine($"$ANOUT[2] = {t2} ; T2 = {s.Temperature2.ToString("F0", Inv)}C");
        sb.AppendLine($"$ANOUT[3] = {t3} ; T3 = {s.Temperature3.ToString("F0", Inv)}C");
        sb.AppendLine($"$ANOUT[4] = {idleAnout} ; RPM idle");
        sb.AppendLine("$OUT[7] = TRUE");
        sb.AppendLine("WAIT FOR $IN[6]==TRUE");
        sb.AppendLine($"BAS(#TOOL,{s.ToolDataIndex})");
        sb.AppendLine($"BAS(#BASE,{s.BaseDataIndex})");
        sb.AppendLine($"$VEL.CP = {vel}");
        sb.AppendLine("$ADVANCE = 3");
        sb.AppendLine();

        // Home as axis PTP (PointLoader-supported)
        sb.AppendLine("; --- Robot home (joint) ---");
        sb.AppendLine(FormatPtpAxis(home, e1));
        sb.AppendLine();

        // Lower only Z to 1 ft — absolute LIN (PointLoader-supported)
        sb.AppendLine("; --- Home XY / ABC / E1, Z = 1 ft above bed ---");
        sb.AppendLine(FormatLin(x, y, zPurge, a, b, c, e1));
        sb.AppendLine();

        sb.AppendLine("; --- Purge (stationary) ---");
        sb.AppendLine($"$ANOUT[4] = {rpmAnout} ; motor {motor.ToString("0.#", Inv)} %");
        sb.AppendLine(FormatWaitSec(time));
        sb.AppendLine("$ANOUT[4] = 0.000 ; extruder off");
        sb.AppendLine("WAIT SEC 1");
        sb.AppendLine();

        sb.AppendLine("; --- Retract Z, return home joints, clear outs ---");
        sb.AppendLine(FormatLin(x, y, zRetract, a, b, c, e1));
        sb.AppendLine(FormatPtpAxis(home, e1));
        sb.AppendLine("$OUT[7] = FALSE");
        sb.AppendLine("$OUT[8] = FALSE");
        sb.AppendLine("$OUT[9] = FALSE");
        sb.AppendLine("END");
        return sb.ToString();
    }

    /// <summary>
    /// Build settings from material + cell. Computes absolute home TCP for PointLoader.
    /// </summary>
    public static Settings FromPreset(
        MaterialPreset preset,
        float motorPercent,
        float runTimeSec,
        CellConfig? cell = null,
        float[]? homeAngles = null,
        float homeE1Mm = float.NaN,
        int? toolIndex = null,
        int? baseIndex = null,
        float purgeHeightMm = DefaultPurgeHeightMm)
    {
        float[] home = homeAngles is { Length: >= 6 }
            ? homeAngles
            : cell?.Robot.HomePosition is { Length: >= 6 } hp
                ? hp
                : [0f, -90f, 90f, 0f, 15f, 0f];

        float e1 = homeE1Mm;
        if (float.IsNaN(e1))
            e1 = 0f;

        var tools = cell?.EffectiveTools;
        ToolCellConfig? tool = null;
        if (tools is { Count: > 0 })
        {
            if (toolIndex is int ti)
                tool = tools.FirstOrDefault(t => t.KrlIndex == ti) ?? tools.FirstOrDefault(t => t.Default) ?? tools[0];
            else
                tool = tools.FirstOrDefault(t => t.Default) ?? tools[0];
        }

        int toolNo = toolIndex ?? tool?.KrlIndex ?? 1;
        if (toolNo <= 0) toolNo = 1;
        int bas = baseIndex ?? cell?.KrlBases.FirstOrDefault()?.Index ?? 1;

        CartesianPose? homeTcp = null;
        if (cell is not null)
            homeTcp = EstimateHomeTcpInBase(home, e1, cell, tool);

        return new Settings
        {
            ProgramName     = SuggestProgramName(preset.Name),
            MaterialName    = preset.Name,
            Temperature1    = (float)preset.Temperature1,
            Temperature2    = (float)preset.Temperature2,
            Temperature3    = (float)preset.Temperature3,
            MotorPercent    = motorPercent,
            RunTimeSec      = runTimeSec,
            MaterialDensity = (float)preset.MaterialDensity,
            ToolDataIndex   = toolNo,
            BaseDataIndex   = bas,
            HomePosition    = home,
            HomeE1Mm        = e1,
            PurgeHeightMm   = purgeHeightMm,
            HomeTcpBase     = homeTcp,
        };
    }

    private static string SanitizeProgramName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "MATERIAL_CAL";
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            else if (c is ' ' or '-') sb.Append('_');
        }
        string s = sb.ToString();
        while (s.Contains("__", StringComparison.Ordinal))
            s = s.Replace("__", "_", StringComparison.Ordinal);
        if (s.Length == 0 || char.IsDigit(s[0])) s = "M_" + s;
        return s.Length > 48 ? s[..48] : s;
    }

    private static string FormatPtpAxis(float[] h, float e1)
        => $"PTP {{A1 {h[0].ToString("F3", Inv)}, A2 {h[1].ToString("F3", Inv)}, " +
           $"A3 {h[2].ToString("F3", Inv)}, A4 {h[3].ToString("F3", Inv)}, " +
           $"A5 {h[4].ToString("F3", Inv)}, A6 {h[5].ToString("F3", Inv)}, " +
           $"E1 {e1.ToString("F3", Inv)}}}";

    private static string FormatLin(float x, float y, float z, float a, float b, float c, float e1)
        => $"LIN {{X {x.ToString("F2", Inv)}, Y {y.ToString("F2", Inv)}, Z {z.ToString("F2", Inv)}, " +
           $"A {a.ToString("F3", Inv)}, B {b.ToString("F3", Inv)}, C {c.ToString("F3", Inv)}, " +
           $"E1 {e1.ToString("F3", Inv)}, E2 0.000, E3 0.000, E4 0.000, E5 0.000, E6 0.000}}";

    private static string FormatWaitSec(float seconds)
    {
        string text = seconds.ToString(seconds % 1f == 0f ? "F0" : "F1", Inv);
        return $"WAIT SEC {text}";
    }
}
