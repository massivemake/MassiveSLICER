#pragma warning disable CA1416  // Windows-only app
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Viewport.Validation;
using MassiveSlicer.ViewModels;
using NVec3 = System.Numerics.Vector3;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace MassiveSlicer.App.Views;

public partial class ViewportView
{
    /// <summary>LFAM 1 KR120 R3900 rated axis speeds, deg/s ($VEL_AXIS_MA), same table as feature/axis-speed-limits.</summary>
    static readonly float[] RatedDegPerSec = [104.93f, 101.01f, 110.05f, 190.04f, 180.05f, 260.15f];

    static readonly Regex LinRx = new(
        @"^\s*LIN\s*\{X\s*(-?[\d.]+),\s*Y\s*(-?[\d.]+),\s*Z\s*(-?[\d.]+),\s*A\s*(-?[\d.]+),\s*B\s*(-?[\d.]+),\s*C\s*(-?[\d.]+).*?E1\s*(-?[\d.]+)",
        RegexOptions.Compiled);
    static readonly Regex VelRx = new(@"^\s*\$VEL\.CP\s*=\s*(-?[\d.]+)", RegexOptions.Compiled);

    /// <summary>
    /// Offline joint-speed check of an exported .src on the active cell: solve every LIN
    /// continuously (warm seed, print orientation, same solve as validation), time each move
    /// at its programmed $VEL.CP, and report joint rates against the rated axis speeds.
    /// Writes &lt;src&gt;.speed.txt (and &lt;src&gt;.joints.csv) and returns immediately.
    /// </summary>
    private string StartSpeedCheckSrc(ViewportViewModel vm, string path)
    {
        var cell = vm.ActiveCell;
        var solver = _ikSolver;
        if (cell is null || solver is null) return "[speed] no cell / IK solver";
        if (!File.Exists(path)) return $"[speed] file not found: {path}";
        RefreshIkSceneKinematics();
        var home = new NVec3(cell.Robot.WorldPosition.X, cell.Robot.WorldPosition.Y, cell.Robot.WorldPosition.Z);
        var baseData = new NVec3(cell.Bed.BaseData.X, cell.Bed.BaseData.Y, cell.Bed.BaseData.Z);
        var rail = cell.RobotRail;
        var joints = cell.Robot.Joints;
        var outPath = path + ".speed.txt";
        var csvPath = path + ".joints.csv";
        if (File.Exists(outPath)) File.Delete(outPath);
        _ = Task.Run(() =>
        {
            try { File.WriteAllText(outPath, RunSpeedCheck(path, solver, home, baseData, rail, joints, csvPath)); }
            catch (Exception ex) { File.WriteAllText(outPath, "FAILED: " + ex); }
        });
        return $"[speed] started on {Path.GetFileName(path)} -> {outPath}";
    }

    static string RunSpeedCheck(string path, MassiveSlicer.Viewport.FK.GltfNumericalIkSolver solver,
        NVec3 home, NVec3 baseData, MassiveSlicer.Core.Models.RobotRailCellConfig? rail,
        IReadOnlyList<MassiveSlicer.Core.Kinematics.JointConfig>? joints, string csvPath)
    {
        var pts = new List<(NVec3 P, float A, float B, float C, float E1, float Vel, int Line)>();
        float vel = 0.1f;
        int lineNo = 0;
        foreach (var raw in File.ReadLines(path))
        {
            lineNo++;
            var vm = VelRx.Match(raw);
            if (vm.Success) { vel = float.Parse(vm.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
            var m = LinRx.Match(raw);
            if (!m.Success) continue;
            float F(int g) => float.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
            pts.Add((new NVec3(F(1), F(2), F(3)), F(4), F(5), F(6), F(7), vel, lineNo));
        }
        int n = pts.Count;
        var sols = new float[n][];
        var seed = new float[] { 0f, -90f, 90f, 0f, 15f, 0f };
        int unsolved = 0;
        for (int i = 0; i < n; i++)
        {
            var (p, a, b, c, e1, _, _) = pts[i];
            var off = rail is not null ? RailE1Planner.BaseWorld(home, rail, e1) - home : NVec3.Zero;
            // SPEEDCHECK_FRAME=world: BASE_DATA measured from WORLD, ROBROOT sits at `home` in WORLD.
            var rel = Environment.GetEnvironmentVariable("SPEEDCHECK_FRAME") == "world"
                ? p + baseData - home - off
                : p + baseData - off;                           // ROBROOT frame of the carriage
            var tgt = new TkVector3(rel.X, rel.Y, rel.Z);
            var rot = solver.TargetRotFromKukaAbc(a, b, c);
            var sol = ToolpathFeasibilityEvaluator.SolvePose(solver, tgt, seed, rot, joints, millPath: false, maxIterations: 40)
                   ?? ToolpathFeasibilityEvaluator.SolveWithPrintFallback(seed,
                        w => ToolpathFeasibilityEvaluator.SolvePose(solver, tgt, w, rot, joints, false, 80),
                        ToolpathFeasibilityEvaluator.PrintIkFallbackSeeds(tgt.X, tgt.Y));
            if (sol is null) { unsolved++; sols[i] = (float[])seed.Clone(); continue; }
            for (int j = 0; j < 6; j++)                         // keep A4/A6 etc. continuous
            {
                float d = sol[j] - seed[j];
                while (d > 180f) { sol[j] -= 360f; d -= 360f; }
                while (d < -180f) { sol[j] += 360f; d += 360f; }
            }
            sols[i] = sol;
            seed = sol;
        }

        var csv = new StringBuilder("idx,line,x,y,z,e1,a1,a2,a3,a4,a5,a6\n");
        for (int i = 0; i < n; i += 50)
            csv.Append(FormattableString.Invariant(
                $"{i},{pts[i].Line},{pts[i].P.X:0.##},{pts[i].P.Y:0.##},{pts[i].P.Z:0.##},{pts[i].E1:0.##},{sols[i][0]:0.##},{sols[i][1]:0.##},{sols[i][2]:0.##},{sols[i][3]:0.##},{sols[i][4]:0.##},{sols[i][5]:0.##}\n"));
        File.WriteAllText(csvPath, csv.ToString());

        // Rates per move at programmed speed. A sharp corner (cos < 0.3) is blended by C_VEL,
        // so its endpoint-to-endpoint rate overstates the real axis speed: reported separately.
        var maxRate = new float[6];
        var maxAt = new int[6];
        int[] over100 = new int[6], over85 = new int[6], over100Straight = new int[6];
        var worst = new List<(float Ratio, int I, int Axis, float Rate, bool Corner)>();
        for (int i = 1; i < n; i++)
        {
            var d = pts[i].P - pts[i - 1].P;
            float dist = d.Length();
            if (dist < 0.05f) continue;
            float v = MathF.Max(pts[i].Vel, 1e-4f) * 1000f;      // m/s -> mm/s
            float dt = dist / v;
            bool corner = false;
            if (i >= 2)
            {
                var d0 = pts[i - 1].P - pts[i - 2].P;
                if (d0.Length() > 0.05f) corner = NVec3.Dot(NVec3.Normalize(d0), NVec3.Normalize(d)) < 0.3f;
            }
            for (int j = 0; j < 6; j++)
            {
                float rate = MathF.Abs(sols[i][j] - sols[i - 1][j]) / dt;
                if (rate > maxRate[j]) { maxRate[j] = rate; maxAt[j] = i; }
                float ratio = rate / RatedDegPerSec[j];
                if (ratio > 0.85f) over85[j]++;
                if (ratio > 1f) { over100[j]++; if (!corner) over100Straight[j]++; worst.Add((ratio, i, j, rate, corner)); }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"speed check: {Path.GetFileName(path)}");
        sb.AppendLine($"LIN points {n:N0}, unsolved {unsolved:N0}");
        for (int j = 0; j < 6; j++)
            sb.AppendLine(FormattableString.Invariant(
                $"A{j + 1}: max {maxRate[j]:0.0} deg/s of rated {RatedDegPerSec[j]:0} ({100 * maxRate[j] / RatedDegPerSec[j]:0}%) at point {maxAt[j]:N0} Z {pts[maxAt[j]].P.Z:0} | over rated: {over100[j]} ({over100Straight[j]} not at sharp corners) | over 85%: {over85[j]}"));
        sb.AppendLine("worst over-rated moves:");
        foreach (var w in worst.OrderByDescending(w => w.Ratio).Take(15))
            sb.AppendLine(FormattableString.Invariant(
                $"  point {w.I:N0} (src line {pts[w.I].Line}) Z {pts[w.I].P.Z:0.#} A{w.Axis + 1} {w.Rate:0} deg/s = {100 * w.Ratio:0}% of rated, |A5| {MathF.Abs(sols[w.I][4]):0.0}{(w.Corner ? "  [sharp corner, blended]" : "")}"));
        return sb.ToString();
    }

    /// <summary>Every 50th validated solution of the active toolpath, for comparing with speed-check-src.</summary>
    private string DumpValidationJoints(string csvPath)
    {
        var sb = new StringBuilder("idx,a1,a2,a3,a4,a5,a6\n");
        foreach (var (_, sols) in _ikSolutionsByNode)
        {
            for (int i = 0; i < sols.Length; i += 50)
                sb.Append(FormattableString.Invariant(
                    $"{i},{sols[i][0]:0.##},{sols[i][1]:0.##},{sols[i][2]:0.##},{sols[i][3]:0.##},{sols[i][4]:0.##},{sols[i][5]:0.##}\n"));
            break;
        }
        File.WriteAllText(csvPath, sb.ToString());
        return $"[speed] wrote {csvPath}";
    }
}
