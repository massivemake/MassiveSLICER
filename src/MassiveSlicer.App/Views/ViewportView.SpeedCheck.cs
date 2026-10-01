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
    /// <summary>KR120 R3900 PA rated axis speeds deg/s ($VEL_AXIS_MA).</summary>
    static readonly float[] RatedDegPerSec = [104.93f, 101.01f, 110.05f, 190.04f, 180.05f, 260.15f];

    static readonly Regex LinRx = new(
        @"^\s*LIN\s*\{X\s*(-?[\d.]+),\s*Y\s*(-?[\d.]+),\s*Z\s*(-?[\d.]+),\s*A\s*(-?[\d.]+),\s*B\s*(-?[\d.]+),\s*C\s*(-?[\d.]+).*?E1\s*(-?[\d.]+)",
        RegexOptions.Compiled);
    static readonly Regex VelRx = new(@"^\s*\$VEL\.CP\s*=\s*(-?[\d.]+)", RegexOptions.Compiled);

    /// <summary>
    /// Offline joint-speed check of an exported .src on the active cell.
    /// Solves every LIN with the active cell IK (warm seed, print orientation),
    /// times each move at its $VEL.CP, and reports joint rates vs rated limits.
    /// Writes &lt;src&gt;.speed.txt and &lt;src&gt;.joints.csv; returns immediately.
    /// NOTE: assumes tool/base from cell JSON — same caveat as Slicer validation.
    /// </summary>
    private string StartSpeedCheckSrc(ViewportViewModel vm, string path)
    {
        var cell   = vm.ActiveCell;
        var solver = _ikSolver;
        if (cell is null || solver is null) return "[speed] no cell / IK solver";
        if (!File.Exists(path)) return $"[speed] file not found: {path}";
        RefreshIkSceneKinematics();
        var home     = new NVec3(cell.Robot.WorldPosition.X, cell.Robot.WorldPosition.Y, cell.Robot.WorldPosition.Z);
        var baseData = new NVec3(cell.Bed.BaseData.X, cell.Bed.BaseData.Y, cell.Bed.BaseData.Z);
        var rail     = cell.RobotRail;
        var joints   = cell.Robot.Joints;
        var outPath  = path + ".speed.txt";
        var csvPath  = path + ".joints.csv";
        if (File.Exists(outPath)) File.Delete(outPath);
        _ = Task.Run(() =>
        {
            try   { File.WriteAllText(outPath, RunSpeedCheck(path, solver, home, baseData, rail, joints, csvPath)); }
            catch (Exception ex) { File.WriteAllText(outPath, "FAILED: " + ex); }
        });
        return $"[speed] started → {outPath}";
    }

    static string RunSpeedCheck(
        string path,
        MassiveSlicer.Viewport.FK.GltfNumericalIkSolver solver,
        NVec3 home, NVec3 baseData,
        MassiveSlicer.Core.Models.RobotRailCellConfig? rail,
        IReadOnlyList<MassiveSlicer.Core.Kinematics.JointConfig>? joints,
        string csvPath)
    {
        // ── Parse ──────────────────────────────────────────────────────────────
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

        // ── Solve IK ───────────────────────────────────────────────────────────
        var sols    = new float[n][];
        var seed    = new float[] { 0f, -90f, 90f, 0f, 15f, 0f };
        int unsolved = 0;
        for (int i = 0; i < n; i++)
        {
            var (p, a, b, c, e1, _, _) = pts[i];
            var off = rail is not null ? RailE1Planner.BaseWorld(home, rail, e1) - home : NVec3.Zero;
            var rel = p + baseData - off;
            var tgt = new TkVector3(rel.X, rel.Y, rel.Z);
            var rot = solver.TargetRotFromKukaAbc(a, b, c);

            // Position-only solve, then orientation-constrained refine — same two
            // steps as Evaluate(), inlined to avoid touching ToolpathFeasibilityEvaluator.
            var pos = solver.Solve(tgt, seed, maxIterations: 40);
            float[]? sol = null;
            if (pos is not null)
            {
                var oriented = solver.Solve(tgt, pos, rot, maxIterations: 80, requireOrientation: true);
                sol = oriented ?? pos;
            }
            sol ??= ToolpathFeasibilityEvaluator.SolveWithPrintFallback(
                seed,
                w => { var p2 = solver.Solve(tgt, w, maxIterations: 80); if (p2 is null) return null; return solver.Solve(tgt, p2, rot, maxIterations: 80, requireOrientation: true) ?? p2; },
                ToolpathFeasibilityEvaluator.PrintIkFallbackSeeds(tgt.X, tgt.Y));

            if (sol is null) { unsolved++; sols[i] = (float[])seed.Clone(); continue; }
            // Unwrap A4/A6 to keep solution continuous
            for (int j = 0; j < 6; j++)
            {
                float d = sol[j] - seed[j];
                while (d >  180f) { sol[j] -= 360f; d -= 360f; }
                while (d < -180f) { sol[j] += 360f; d += 360f; }
            }
            sols[i] = sol;
            seed = sol;
        }

        // ── CSV (every 50th) ───────────────────────────────────────────────────
        var csv = new StringBuilder("idx,line,x,y,z,e1,a1,a2,a3,a4,a5,a6\n");
        for (int i = 0; i < n; i += 50)
            csv.Append(FormattableString.Invariant(
                $"{i},{pts[i].Line},{pts[i].P.X:0.##},{pts[i].P.Y:0.##},{pts[i].P.Z:0.##},{pts[i].E1:0.##},{sols[i][0]:0.##},{sols[i][1]:0.##},{sols[i][2]:0.##},{sols[i][3]:0.##},{sols[i][4]:0.##},{sols[i][5]:0.##}\n"));
        File.WriteAllText(csvPath, csv.ToString());

        // ── Speed analysis ─────────────────────────────────────────────────────
        var maxRate       = new float[6];
        var maxAt         = new int[6];
        int[] over100     = new int[6];
        int[] over85      = new int[6];
        int[] over100Str  = new int[6];   // not at a sharp C_VEL corner
        var worst = new List<(float Ratio, int I, int Axis, float Rate, bool Corner)>();

        for (int i = 1; i < n; i++)
        {
            var d = pts[i].P - pts[i - 1].P;
            float dist = d.Length();
            if (dist < 0.05f) continue;
            float v  = MathF.Max(pts[i].Vel, 1e-4f) * 1000f;   // m/s → mm/s
            float dt = dist / v;
            bool corner = false;
            if (i >= 2)
            {
                var d0 = pts[i - 1].P - pts[i - 2].P;
                if (d0.Length() > 0.05f)
                    corner = NVec3.Dot(NVec3.Normalize(d0), NVec3.Normalize(d)) < 0.3f;
            }
            for (int j = 0; j < 6; j++)
            {
                float rate  = MathF.Abs(sols[i][j] - sols[i - 1][j]) / dt;
                float ratio = rate / RatedDegPerSec[j];
                if (rate > maxRate[j]) { maxRate[j] = rate; maxAt[j] = i; }
                if (ratio > 0.85f) over85[j]++;
                if (ratio > 1f)
                {
                    over100[j]++;
                    if (!corner) over100Str[j]++;
                    worst.Add((ratio, i, j, rate, corner));
                }
            }
        }

        // ── Report ─────────────────────────────────────────────────────────────
        var sb = new StringBuilder();
        sb.AppendLine("UNVALIDATED — do not use as a print go/no-go.");
        sb.AppendLine("SRC→joint conversion does not yet match the controller.");
        sb.AppendLine("See commit c10244b message for validation steps before trusting these numbers.");
        sb.AppendLine();
        sb.AppendLine($"speed check: {Path.GetFileName(path)}");
        sb.AppendLine($"LIN points {n:N0}, unsolved {unsolved:N0}");
        for (int j = 0; j < 6; j++)
            sb.AppendLine(FormattableString.Invariant(
                $"A{j+1}: max {maxRate[j]:0.0} deg/s of rated {RatedDegPerSec[j]:0} ({100 * maxRate[j] / RatedDegPerSec[j]:0}%) at point {maxAt[j]:N0} Z {pts[maxAt[j]].P.Z:0} | over rated: {over100[j]} ({over100Str[j]} not at sharp corners) | over 85%: {over85[j]}"));
        sb.AppendLine("worst over-rated moves:");
        foreach (var w in worst.OrderByDescending(w => w.Ratio).Take(15))
            sb.AppendLine(FormattableString.Invariant(
                $"  point {w.I:N0} (src line {pts[w.I].Line}) Z {pts[w.I].P.Z:0.#} A{w.Axis+1} {w.Rate:0} deg/s = {100*w.Ratio:0}% of rated, |A5| {MathF.Abs(sols[w.I][4]):0.0}{(w.Corner ? "  [sharp corner, blended]" : "")}" ));
        return sb.ToString();
    }

    /// <summary>Every 50th validated joint solution of the active toolpath → CSV (console <c>ik-dump</c>).</summary>
    private string DumpValidationJoints(string csvPath)
    {
        var sb = new StringBuilder("idx,a1,a2,a3,a4,a5,a6\n");
        foreach (var (_, ikSols) in _ikSolutionsByNode)
        {
            for (int i = 0; i < ikSols.Length; i += 50)
                sb.Append(FormattableString.Invariant(
                    $"{i},{ikSols[i][0]:0.##},{ikSols[i][1]:0.##},{ikSols[i][2]:0.##},{ikSols[i][3]:0.##},{ikSols[i][4]:0.##},{ikSols[i][5]:0.##}\n"));
            break;
        }
        File.WriteAllText(csvPath, sb.ToString());
        return $"[speed] wrote {csvPath}";
    }
}
