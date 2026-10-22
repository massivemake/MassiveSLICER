using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.FK;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using MassiveSlicer.Viewport.Validation;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

/// <summary>
/// Newton is scored against the glTF chain. The damped solver is the ruler:
/// it certifies the target, and it is not what <c>RebuildIkSolver</c> builds.
/// LFAM 1, 2, and 3 share that chain, so each case runs on all three.
/// </summary>
public class NewtonPrimaryIkTest
{
    static readonly float[] MillStart = [0f, -90f, 90f, 0f, 45f, 105f];

    public static IEnumerable<object[]> Cells()
    {
        yield return ["LFAM1", "lfam1.json"];
        yield return ["LFAM2", "lfam2.json"];
        yield return ["LFAM3", "lfam3.json"];
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Print_nozzle_up_bed_tcp_is_within_1mm_and_1deg(string folder, string file)
    {
        var cell = LoadCell(folder, file, out var newton, out var ruler, out var robotWorld, out var joints);
        AssertJacobian(newton, Home(cell), folder);

        var bed = BedInSolverRobroot(cell, robotWorld);
        var rot = ruler.TargetRotFromGlobalOrientation(Vector3.UnitZ, 0f, 0f, 0f);
        var pose = ClosestNozzleUp(ruler, rot, bed, robotWorld, joints);
        float poseOriDeg = ruler.OrientationError(pose, rot) * (180f / MathF.PI);
        Assert.True(poseOriDeg <= 1f,
            $"{folder} has no nozzle-up pose within 1° of the bed (best {poseOriDeg:0.00}°)");

        var target = ruler.ComputeTcpPosScene(pose) - robotWorld;
        var seeds = PrintSeeds(cell, target, pose);
        float[]? seedUsed = null;
        foreach (var seed in seeds)
        {
            if (ruler.Solve(target, seed, rot, maxIterations: 300, requireOrientation: true) is not null)
            {
                seedUsed = seed;
                break;
            }
        }
        Assert.True(seedUsed is not null,
            $"{folder} glTF solver did not accept the nozzle-up bed TCP {Fmt(target)} (in workspace {ruler.IsInWorkspace(target)})");

        var solved = newton.Solve(target, seedUsed!, rot, maxIterations: 300, requireOrientation: true);
        Assert.True(solved is not null, $"{folder} Newton missed a print TCP the glTF solver accepts");
        float mm = PosMm(newton, solved!, target, robotWorld);
        float deg = newton.OrientationError(solved, rot) * (180f / MathF.PI);
        Assert.True(mm <= 1f, $"{folder} print TCP error {mm:0.00} mm");
        Assert.True(deg <= 1f, $"{folder} print orientation error {deg:0.00}°");
        Assert.True(JointLimitEnvelope.JointsInside(solved, joints),
            $"{folder} print joints left the usable range: {FmtJoints(solved)}");
    }

    [Fact]
    public void Mill_start_flange_is_45deg_from_world_down()
    {
        var cell = ReadCell("LFAM3", "lfam3.json");
        var chain = Chain(cell);
        var t2 = cell.EffectiveTools.First(t => t.KrlIndex == 2);
        var flange = Make(cell, chain.RobotWorld, Matrix4.Identity, cell.Robot.Joints, 0f, 0f, 0f, 0f, newton: false);
        var spindleTool = Make(cell, chain.RobotWorld, Matrix4.Identity, cell.Robot.Joints, 0f, t2.TcpA, t2.TcpB, t2.TcpC, newton: false);
        float flangeErr = BestDownError(flange);
        float t2Err = BestDownError(spindleTool);
        Assert.InRange(flangeErr, 40f, 50f);
        Assert.InRange(t2Err, 40f, 50f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-15f)]
    public void Spindle_cutter_abc_tracks_the_bit_not_the_flange(float rollDeg)
    {
        var cell = ReadCell("LFAM3", "lfam3.json");
        var chain = Chain(cell);
        float roll = rollDeg * MathF.PI / 180f;
        var (axis, zInFlange) = CutterInFlange(cell, roll);
        var abc = KukaOrientation.AbcAimingToolZ(new System.Numerics.Vector3(zInFlange.X, zInFlange.Y, zInFlange.Z));
        var ruler = Make(cell, chain.RobotWorld, Matrix4.Identity, cell.Robot.Joints, roll, abc.A, abc.B, abc.C, newton: false);
        var toolZ = ruler.ToolZScene(MillStart);
        float toCutter = AngleDeg(toolZ, axis);
        Assert.True(toCutter < 1f, $"roll {rollDeg}° ABC=({abc.A:0.##},{abc.B:0.##},{abc.C:0.##}) tool Z is {toCutter:0.00}° off the spindle cutter");

        var down = ruler.TargetRotFromMillNormal(Vector3.UnitZ, 0f);
        var tcp = ruler.ComputeTcpPosScene(MillStart) - chain.RobotWorld;
        var solved = ruler.Solve(tcp, MillStart, down, maxIterations: 400, requireOrientation: true);
        Assert.NotNull(solved);
        float tilt = AngleDeg(ruler.ToolZScene(solved!), new Vector3(0f, 0f, -1f));
        Assert.True(tilt < 2f, $"world-down left the spindle cutter {tilt:0.00}° off vertical ({FmtJoints(solved!)})");
    }

    [Fact]
    public void Horizontal_cut_keeps_the_upright_roll()
    {
        // Cutter into a +X wall is the upright frame swung 90°. Tool Y stays on
        // that swing (world −Z here). Popping it to world +Z was the Y flip.
        var cell = ReadCell("LFAM3", "lfam3.json");
        var (_, zInFlange) = CutterInFlange(cell, 0f);
        var tcp = KukaOrientation.AbcAimingToolZ(
            new System.Numerics.Vector3(zInFlange.X, zInFlange.Y, zInFlange.Z));
        var tcpM = KukaIkSolver.AbcToMatrix(tcp.A, tcp.B, tcp.C);
        var flangeZInTool = new System.Numerics.Vector3(tcpM.M13, tcpM.M23, tcpM.M33);

        var (a, b, c) = KukaOrientation.AbcFromMillNormal(System.Numerics.Vector3.UnitX);
        var tool = KukaIkSolver.AbcToMatrix(a, b, c);
        var toolX = new System.Numerics.Vector3(tool.M11, tool.M12, tool.M13);
        var toolY = new System.Numerics.Vector3(tool.M21, tool.M22, tool.M23);
        var toolZ = new System.Numerics.Vector3(tool.M31, tool.M32, tool.M33);
        Assert.True(toolZ.X < -0.99f, $"cutter {toolZ}");
        Assert.True(System.Numerics.Vector3.Dot(toolY, -System.Numerics.Vector3.UnitZ) > 0.99f, $"tool Y {toolY}");
        var flangeZ = flangeZInTool.X * toolX + flangeZInTool.Y * toolY + flangeZInTool.Z * toolZ;
        Assert.True(System.Numerics.Vector3.Dot(flangeZ, toolY) > 0.99f,
            $"flange axis {flangeZ} left tool Y. cutter-in-flange {zInFlange} tcp ABC=({tcp.A:0.#},{tcp.B:0.#},{tcp.C:0.#})");
    }

    static float BestDownError(GltfNumericalIkSolver ruler)
    {
        float best = 999f;
        for (int y = -180; y <= 180; y++)
        {
            float deg = ruler.OrientationError(MillStart, ruler.TargetRotFromMillNormal(Vector3.UnitZ, y)) * (180f / MathF.PI);
            if (deg < best) best = deg;
        }
        return best;
    }

    static float AngleDeg(Vector3 a, Vector3 b)
        => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)) * (180f / MathF.PI);

    static (Vector3 Axis, Vector3 InFlange) CutterInFlange(CellConfig cell, float totalRoll)
    {
        var glb = GltfLoader.Load(AssetPaths.Resolve(cell.Robot.ModelPath));
        var p = cell.Robot.ModelWorldPosition;
        var wrapper = new SceneNode { LocalTransform = Matrix4.CreateTranslation(p.X, p.Y, p.Z) };
        wrapper.AddChild(glb);
        var fk = RobotFkController.TryBuild(wrapper, cell.Robot.Joints);
        Assert.NotNull(fk);
        fk!.Apply(MillStart[0], MillStart[1], MillStart[2], MillStart[3], MillStart[4], MillStart[5]);
        var flange = fk.FlangeNode!;
        var spindle = GltfLoader.Load(AssetPaths.Resolve("assets/cells/LFAM3/Toolheads/spindle.glb"));
        spindle.LocalTransform = spindle.LocalTransform * flange.WorldTransform;
        new SceneNode().AddChild(spindle);
        Assert.True(SpindleBitCylinder.TryGetCutterWorld(spindle, out _, out var axis));
        var fw = flange.WorldTransform;
        var r0 = Vector3.Normalize(fw.Row0.Xyz);
        var r1 = Vector3.Normalize(fw.Row1.Xyz);
        var r2 = Vector3.Normalize(fw.Row2.Xyz);
        float cr = MathF.Cos(totalRoll), sr = MathF.Sin(totalRoll);
        var kukaX = cr * r0 + sr * r2;
        var kukaY = sr * r0 - cr * r2;
        var kukaZ = r1;
        return (axis, new Vector3(Vector3.Dot(axis, kukaX), Vector3.Dot(axis, kukaY), Vector3.Dot(axis, kukaZ)));
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Mill_from_mill_start_keeps_cutter_and_a4(string folder, string file)
    {
        var cell = ReadCell(folder, file);
        var joints = cell.Robot.Joints;
        var robotWorld = Chain(cell).RobotWorld;
        var tcp = CutterTcpLocal(folder, cell);
        var t12 = Tool12Abc();
        var newton = Make(cell, robotWorld, tcp, joints, toolFrameRoll: 0f, t12.A, t12.B, t12.C, newton: true);
        var ruler = Make(cell, robotWorld, tcp, joints, toolFrameRoll: 0f, t12.A, t12.B, t12.C, newton: false);

        Vector3 target = default;
        var shifted = false;
        foreach (var delta in new[] { 5f, 10f, 15f, -10f, -15f })
        {
            var q = (float[])MillStart.Clone();
            q[0] += delta;
            if (!JointLimitEnvelope.JointsInside(q, joints)) continue;
            var candidate = ruler.ComputeTcpPosScene(q) - robotWorld;
            if (!ruler.IsInWorkspace(candidate)) continue;
            target = candidate;
            shifted = true;
            break;
        }
        Assert.True(shifted, $"{folder} has no in-workspace cutter target near Mill Start");

        var rulerPos = ruler.Solve(target, MillStart, maxIterations: 400);
        Assert.True(rulerPos is not null,
            $"{folder} glTF solver did not accept the mill cutter target {Fmt(target)}");

        var pos = newton.Solve(target, MillStart, maxIterations: 400);
        Assert.True(pos is not null, $"{folder} Newton position-first mill returned null");
        Assert.True(PosMm(newton, pos!, target, robotWorld) <= 1f,
            $"{folder} mill position error {PosMm(newton, pos!, target, robotWorld):0.00} mm");

        var rot = newton.TargetRotFromMillNormal(Vector3.UnitZ);
        var oriented = newton.Solve(target, pos!, rot, maxIterations: 120, requireOrientation: false);
        var used = pos!;
        if (oriented is not null
            && MathF.Abs(oriented[3] - MillStart[3]) <= 5f
            && PosMm(newton, oriented, target, robotWorld) <= 1f)
            used = oriented;

        float mm = PosMm(newton, used, target, robotWorld);
        float a4 = MathF.Abs(used[3] - MillStart[3]);
        Assert.True(mm <= 1f, $"{folder} mill cutter error {mm:0.00} mm");
        Assert.True(a4 <= 5f, $"{folder} A4 moved {a4:0.00}° from Mill Start ({FmtJoints(used)})");
        Assert.True(JointLimitEnvelope.JointsInside(used, joints),
            $"{folder} mill joints left the usable range: {FmtJoints(used)}");
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Miss_outside_reach_returns_null(string folder, string file)
    {
        var cell = LoadCell(folder, file, out var newton, out var ruler, out _, out _);
        var far = new Vector3(20000f, 20000f, 5000f);
        var home = Home(cell);
        var rot = ruler.TargetRotFromGlobalOrientation(Vector3.UnitZ, 0f, 0f, 0f);
        Assert.Null(ruler.Solve(far, home));
        Assert.Null(newton.Solve(far, home));
        Assert.Null(newton.Solve(far, home, rot, requireOrientation: true));
    }

    static void AssertJacobian(NewtonIkSolver newton, float[] q, string folder)
    {
        var p0 = newton.ComputeTcpPosScene(q);
        var J = newton.AnalyticTcpJacobianMmPerDeg(q);
        const float Eps = 0.05f;
        for (int j = 0; j < 6; j++)
        {
            var qq = (float[])q.Clone();
            qq[j] += Eps;
            var fd = (newton.ComputeTcpPosScene(qq) - p0) / Eps;
            float mag = J[j].Length;
            if (mag < 0.25f) continue;
            float rel = (fd - J[j]).Length / mag;
            Assert.True(rel < 0.05f,
                $"{folder} A{j + 1} analytic Jacobian disagrees with the chain (rel {rel:0.000} J {J[j]} fd {fd})");
        }
    }

    static float[] ClosestNozzleUp(
        GltfNumericalIkSolver ruler,
        (Vector3 r0, Vector3 r1, Vector3 r2) rot,
        Vector3 bed, Vector3 robotWorld, IReadOnlyList<JointConfig> joints)
    {
        float[]? best = null;
        float bestScore = float.MaxValue;
        foreach (var a1 in new[] { -40f, -20f, 0f, 20f, 40f })
        foreach (var a5 in new[] { -60f, -30f, 0f, 30f, 60f, 90f })
        {
            if (!Inside(a1, joints[0]) || !Inside(a5, joints[4])) continue;
            for (float a2 = joints[1].UsableMinDeg; a2 <= joints[1].UsableMaxDeg; a2 += 12f)
            for (float a3 = joints[2].UsableMinDeg; a3 <= joints[2].UsableMaxDeg; a3 += 12f)
            {
                var q = new[] { a1, a2, a3, 0f, a5, 0f };
                if (!ruler.IsInWorkspace(ruler.ComputeTcpPosScene(q) - robotWorld)) continue;
                float score = Score(ruler, rot, bed, robotWorld, q);
                if (score < bestScore) { bestScore = score; best = q; }
            }
        }
        Assert.NotNull(best);

        float a1b = best![0], a2b = best[1], a3b = best[2], a5b = best[4];
        for (float a2 = a2b - 8f; a2 <= a2b + 8f; a2 += 2f)
        for (float a3 = a3b - 8f; a3 <= a3b + 8f; a3 += 2f)
        for (float a5 = a5b - 8f; a5 <= a5b + 8f; a5 += 2f)
        {
            if (!Inside(a2, joints[1]) || !Inside(a3, joints[2]) || !Inside(a5, joints[4])) continue;
            var q = new[] { a1b, a2, a3, 0f, a5, 0f };
            if (!ruler.IsInWorkspace(ruler.ComputeTcpPosScene(q) - robotWorld)) continue;
            float score = Score(ruler, rot, bed, robotWorld, q);
            if (score < bestScore) { bestScore = score; best = q; }
        }
        return best;
    }

    static float Score(
        GltfNumericalIkSolver ruler,
        (Vector3 r0, Vector3 r1, Vector3 r2) rot,
        Vector3 bed, Vector3 robotWorld, float[] q)
    {
        float ori = ruler.OrientationError(q, rot);
        var tcp = ruler.ComputeTcpPosScene(q) - robotWorld;
        float dx = tcp.X - bed.X, dy = tcp.Y - bed.Y;
        return ori + MathF.Sqrt(dx * dx + dy * dy) / 4000f;
    }

    static List<float[]> PrintSeeds(CellConfig cell, Vector3 target, float[] pose)
    {
        var seeds = new List<float[]> { Home(cell) };
        seeds.AddRange(ToolpathFeasibilityEvaluator.PrintIkFallbackSeeds(target.X, target.Y));
        var nudged = (float[])pose.Clone();
        nudged[0] += 15f;
        nudged[1] -= 10f;
        seeds.Add(nudged);
        var close = (float[])pose.Clone();
        close[0] += 3f;
        seeds.Add(close);
        return seeds;
    }

    static CellConfig LoadCell(
        string folder, string file,
        out NewtonIkSolver newton, out GltfNumericalIkSolver ruler,
        out Vector3 robotWorld, out IReadOnlyList<JointConfig> joints)
    {
        var cell = ReadCell(folder, file);
        var chain = Chain(cell);
        robotWorld = chain.RobotWorld;
        joints = cell.Robot.Joints;
        var tool = cell.EffectiveTools.FirstOrDefault(t => t.Default)
                   ?? cell.EffectiveTools.First(t => t.Name.Contains("Extruder", StringComparison.OrdinalIgnoreCase));
        float roll = tool.ToolFrameRoll * MathF.PI / 180f;
        var tcp = TaughtTcp(tool, roll);
        newton = (NewtonIkSolver)Make(cell, robotWorld, tcp, joints, roll, 0f, 0f, 0f, newton: true);
        ruler = Make(cell, robotWorld, tcp, joints, roll, 0f, 0f, 0f, newton: false);
        return cell;
    }

    static GltfNumericalIkSolver Make(
        CellConfig cell, Vector3 robotWorld, Matrix4 tcp, IReadOnlyList<JointConfig> joints,
        float toolFrameRoll, float a, float b, float c, bool newton)
    {
        var chain = Chain(cell);
        return newton
            ? new NewtonIkSolver(chain.Poses, chain.Root, robotWorld, tcp, joints, toolFrameRoll, a, b, c)
            : new GltfNumericalIkSolver(chain.Poses, chain.Root, robotWorld, tcp, joints, toolFrameRoll, a, b, c);
    }

    static (IReadOnlyList<Matrix4> Poses, Matrix4 Root, Vector3 RobotWorld) Chain(CellConfig cell)
    {
        var glb = GltfLoader.Load(AssetPaths.Resolve(cell.Robot.ModelPath));
        var p = cell.Robot.ModelWorldPosition;
        var wrapper = new SceneNode
        {
            Name = cell.Name + "_Robot",
            LocalTransform = Matrix4.CreateTranslation(p.X, p.Y, p.Z),
        };
        wrapper.AddChild(glb);
        var fk = RobotFkController.TryBuild(wrapper, cell.Robot.Joints);
        Assert.NotNull(fk);
        var origin = wrapper.WorldTransform.Row3.Xyz;
        return (fk!.RestPoses, fk.ChainRootTransform, origin);
    }

    static Matrix4 CutterTcpLocal(string folder, CellConfig cell)
    {
        var glb = GltfLoader.Load(AssetPaths.Resolve(cell.Robot.ModelPath));
        var p = cell.Robot.ModelWorldPosition;
        var wrapper = new SceneNode
        {
            LocalTransform = Matrix4.CreateTranslation(p.X, p.Y, p.Z),
        };
        wrapper.AddChild(glb);
        var fk = RobotFkController.TryBuild(wrapper, cell.Robot.Joints);
        Assert.NotNull(fk);
        fk!.Apply(MillStart[0], MillStart[1], MillStart[2], MillStart[3], MillStart[4], MillStart[5]);
        var flange = fk.FlangeNode;
        Assert.NotNull(flange);

        var spindle = GltfLoader.Load(AssetPaths.Resolve("assets/cells/LFAM3/Toolheads/spindle.glb"));
        var correction = spindle.LocalTransform;
        spindle.LocalTransform = correction * flange!.WorldTransform;
        new SceneNode().AddChild(spindle);

        Assert.True(SpindleBitCylinder.TryGetCutterWorld(spindle, out var bit, out _),
            $"{folder} spindle has no SpindleBitTCP cutter");

        Matrix4.Invert(flange.WorldTransform, out var inv);
        var local = Vector3.TransformPosition(bit, inv);
        var origin = wrapper.WorldTransform.Row3.Xyz;
        string misses = "";
        foreach (var scale in new[] { 1f, 0.001f })
        {
            var candidate = Matrix4.CreateTranslation(local * scale);
            var probe = new GltfNumericalIkSolver(
                fk.RestPoses, fk.ChainRootTransform, origin, candidate, cell.Robot.Joints);
            float err = (probe.ComputeTcpPosScene(MillStart) - bit).Length;
            if (err <= 2f)
                return candidate;
            misses += $" scale {scale}: {err:0.#} mm;";
        }
        Assert.Fail($"{folder} cutter offset did not sit on SpindleBitTCP.{misses}");
        return Matrix4.Identity;
    }

    static (float A, float B, float C) Tool12Abc()
    {
        var cell = ReadCell("LFAM3", "lfam3.json");
        var t12 = cell.EffectiveTools.First(t => t.KrlIndex == 12 || t.Name.Contains("Tool 12", StringComparison.OrdinalIgnoreCase));
        return (t12.TcpA, t12.TcpB, t12.TcpC);
    }

    static CellConfig ReadCell(string folder, string file)
    {
        var root = AssetPaths.FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!, "assets", "cells", folder, file);
        Assert.True(File.Exists(path), path);
        return CellLoader.Load(path);
    }

    static Vector3 BedInSolverRobroot(CellConfig cell, Vector3 solverRob)
    {
        var kin = cell.Robot.WorldPosition;
        Float3 world = cell.HeatedBed is { } hb
            ? hb.WorldOrigin(kin)
            : cell.Bed.BaseMarkerWorld(kin);
        return new Vector3(world.X - solverRob.X, world.Y - solverRob.Y, world.Z - solverRob.Z);
    }

    static Matrix4 TaughtTcp(ToolCellConfig t, float roll)
    {
        float cr = MathF.Cos(roll), sr = MathF.Sin(roll);
        return Matrix4.CreateTranslation(
            (t.TcpX * cr + t.TcpY * sr) / 1000f,
            t.TcpZ / 1000f,
            (t.TcpX * sr - t.TcpY * cr) / 1000f);
    }

    static float[] Home(CellConfig cell)
    {
        var h = cell.Robot.HomePosition;
        Assert.NotNull(h);
        Assert.True(h!.Length >= 6);
        return [h[0], h[1], h[2], h[3], h[4], h[5]];
    }

    static bool Inside(float deg, JointConfig j) => JointLimitEnvelope.Contains(deg, j.MinDeg, j.MaxDeg);

    static float PosMm(GltfNumericalIkSolver solver, float[] q, Vector3 targetRobroot, Vector3 robotWorld)
        => (solver.ComputeTcpPosScene(q) - (targetRobroot + robotWorld)).Length;

    static string Fmt(Vector3 v) => $"({v.X:0.#}, {v.Y:0.#}, {v.Z:0.#})";
    static string FmtJoints(float[] q) => string.Join(", ", q.Select(a => a.ToString("0.0")));
}
