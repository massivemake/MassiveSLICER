using System.Numerics;
using MassiveSlicer.Core.Kinematics;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

public class IkDebugTest(ITestOutputHelper output)
{
    // LFAM2 values from lfam2.json + LoadCell bed-center calculation
    private static readonly Vector3 BedCenterRobot = new(2933.829f, 122.641f, -870f);
    private static readonly Vector3 TcpOffset      = new(677.94f, -163.20f, 314.32f);
    private static readonly float[] HomeSeed       = [0f, -90f, 90f, 0f, 15f, 0f];

    // ── Test 1: FK/IK round-trip at home ────────────────────────────────────────
    // NOTE: The DH ForwardKinematics is inconsistent with SolveAll's analytic OPW geometry.
    // The round-trip fails. This test documents the failure; it does NOT block the IK path
    // because the viewport uses GLTF FK, not ForwardKinematics.

    [Fact]
    public void FK_At_Home_Then_IK_RoundTrip()
    {
        output.WriteLine("=== FK at home position ===");
        var fkHome   = KukaIkSolver.ForwardKinematics(HomeSeed);
        var flangeH  = new Vector3(fkHome.M41, fkHome.M42, fkHome.M43);
        var (aH, bH, cH) = KukaIkSolver.MatrixToAbc(fkHome);

        output.WriteLine($"Flange pos (ROBROOT): {Fmt(flangeH)}");
        output.WriteLine($"Flange ABC: A={aH:F2}° B={bH:F2}° C={cH:F2}°");
        output.WriteLine($"FK rotation rows:");
        output.WriteLine($"  X-row: ({fkHome.M11:F3}, {fkHome.M12:F3}, {fkHome.M13:F3})");
        output.WriteLine($"  Y-row: ({fkHome.M21:F3}, {fkHome.M22:F3}, {fkHome.M23:F3})");
        output.WriteLine($"  Z-row: ({fkHome.M31:F3}, {fkHome.M32:F3}, {fkHome.M33:F3})");
        output.WriteLine("");

        output.WriteLine("=== IK round-trip: solve to reach that flange position ===");
        var all = KukaIkSolver.SolveAll(flangeH, fkHome);
        int inLim = 0;
        for (int i = 0; i < all.Length; i++)
        {
            var s   = all[i];
            string  tag = s.Unreachable ? "UNREACHABLE" : s.InLimits ? "IN_LIMITS" : "OOL";
            if (s.InLimits) inLim++;
            output.WriteLine($"  [{i}] {tag}: [{string.Join(", ", s.Krl.Select(v => $"{v:F2}"))}]");

            // Verify FK of each solution matches flangeH
            if (!s.Unreachable)
            {
                var fkSol  = KukaIkSolver.ForwardKinematics(s.Krl);
                var solPos = new Vector3(fkSol.M41, fkSol.M42, fkSol.M43);
                float posErr = (solPos - flangeH).Length();
                output.WriteLine($"       FK pos: {Fmt(solPos)}, err={posErr:F2} mm");
            }
        }
        output.WriteLine($"  => {inLim}/8 solutions in limits");
        output.WriteLine("");

        // Check best solution round-trip
        var best = KukaIkSolver.Solve(flangeH, aH, bH, cH, HomeSeed);
        if (best is null)
        {
            output.WriteLine("Solve() returned null.");
            Assert.Fail("Round-trip IK returned null.");
            return;
        }

        output.WriteLine($"Best solution: [{string.Join(", ", best.Select(v => $"{v:F2}"))}]");
        output.WriteLine($"Home seed:     [{string.Join(", ", HomeSeed.Select(v => $"{v:F2}"))}]");

        float diff = 0f;
        for (int i = 0; i < 6; i++) diff = Math.Max(diff, Math.Abs(best[i] - HomeSeed[i]));
        output.WriteLine($"Max joint diff from home: {diff:F2}°");
    }

    // ── Test 2: What ABC does the bed center target require? ────────────────────

    [Fact]
    public void BedCenter_WhatAbcWorksWithinLimits()
    {
        output.WriteLine("=== Bed center in ROBROOT ===");
        output.WriteLine($"Nozzle target: {Fmt(BedCenterRobot)}");
        output.WriteLine($"TCP offset:    {Fmt(TcpOffset)}");
        output.WriteLine("");

        // Try several candidate ABC orientations
        var candidates = new (string Label, float A, float B, float C)[]
        {
            ("A=0 B=0 C=0 (identity)",         0f,   0f,  0f),
            ("A=0 B=180 C=0 (flip-Z down)",     0f, 180f,  0f),
            ("A=180 B=180 C=0 (down+A180)",   180f, 180f,  0f),
            ("A=0 B=90 C=0 (Z=forward)",        0f,  90f,  0f),
            ("A=0 B=-90 C=0 (Z=backward)",      0f, -90f,  0f),
            ("A=180 B=0 C=0",                 180f,   0f,  0f),
        };

        foreach (var (label, a, b, c) in candidates)
        {
            var rotMat    = KukaIkSolver.AbcToMatrix(a, b, c);
            var flangePos = BedCenterRobot - Vector3.TransformNormal(TcpOffset, rotMat);
            var all       = KukaIkSolver.SolveAll(flangePos, rotMat);
            int inLim     = all.Count(s => s.InLimits && !s.Unreachable);

            output.WriteLine($"ABC=({label}):");
            output.WriteLine($"  Flange pos: {Fmt(flangePos)}");
            output.WriteLine($"  In-limits solutions: {inLim}/8");

            if (inLim > 0)
            {
                foreach (var s in all.Where(s => s.InLimits))
                    output.WriteLine($"    -> [{string.Join(", ", s.Krl.Select(v => $"{v:F2}"))}]");
            }
            output.WriteLine("");
        }
    }

    // ── Test 3: What is the FK-derived ABC at the nozzle target? ────────────────

    [Fact]
    public void Home_FK_TcpCheck()
    {
        output.WriteLine("=== FK TCP at home using ComputeTcpWorldPos ===");
        var tcp = KukaIkSolver.ComputeTcpWorldPos(HomeSeed, Vector3.Zero, TcpOffset);
        output.WriteLine($"TCP nozzle pos (ROBROOT): {Fmt(tcp)}");
        output.WriteLine($"Bed center     (ROBROOT): {Fmt(BedCenterRobot)}");
        output.WriteLine("");

        var fk = KukaIkSolver.ForwardKinematics(HomeSeed);
        output.WriteLine("FK tool-frame axes at home (row = flange-local axis → ROBROOT direction):");
        output.WriteLine($"  Flange X → ROBROOT: ({fk.M11:F3}, {fk.M12:F3}, {fk.M13:F3})");
        output.WriteLine($"  Flange Y → ROBROOT: ({fk.M21:F3}, {fk.M22:F3}, {fk.M23:F3})");
        output.WriteLine($"  Flange Z → ROBROOT: ({fk.M31:F3}, {fk.M32:F3}, {fk.M33:F3})");
    }

    private static string Fmt(Vector3 v) => $"({v.X:F2}, {v.Y:F2}, {v.Z:F2})";
}
