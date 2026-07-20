using System.Numerics;
using MassiveSlicer.Core.Collision;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using Xunit;

namespace MassiveSlicer.Tests.Collision;

/// <summary>
/// Synthetic robot for FK/self-collision tests:
/// - L1 rotates about Y at the origin; its link geometry is a 100-long slab along +X.
/// - L2 rides a joint at the end of L1 (rest pose = +100 X), also rotating about Y,
///   with another 100-long slab along +X.
/// Joints 3..6 are identity stubs (no geometry). Folding joint 2 by 180° brings
/// L2's slab back over L1's — a self collision.
/// </summary>
static class TwoLinkRobot
{
    public static ConvexHull Slab(float len, float half) => Quickhull.Build(
    [
        new Vector3(0, -half, -half), new Vector3(len, -half, -half),
        new Vector3(0,  half, -half), new Vector3(len,  half, -half),
        new Vector3(0, -half,  half), new Vector3(len, -half,  half),
        new Vector3(0,  half,  half), new Vector3(len,  half,  half),
    ]);

    public static RobotCollisionModel Build(float slabHalf = 10f)
    {
        var linkHulls = new IReadOnlyList<ConvexHull>[RobotCollisionModel.LinkCount];
        for (int i = 0; i < linkHulls.Length; i++) linkHulls[i] = [];
        linkHulls[1] = new[] { Slab(100f, slabHalf) };
        linkHulls[2] = new[] { Slab(100f, slabHalf) };

        var rest = new Matrix4x4[6];
        rest[0] = Matrix4x4.Identity;                          // joint_1 at chain root
        rest[1] = Matrix4x4.CreateTranslation(100f, 0f, 0f);   // joint_2 at end of L1
        for (int i = 2; i < 6; i++) rest[i] = Matrix4x4.Identity;

        var cfg = new JointConfig[6];
        for (int i = 0; i < 6; i++)
            cfg[i] = new JointConfig { KrlSign = 1f, Axis = RotationAxis.Y };

        return new RobotCollisionModel(linkHulls, rest, cfg, Matrix4x4.Identity);
    }
}

public sealed class RobotCollisionModelTest
{
    [Fact]
    public void Fk_LinkTransforms_MatchExpected()
    {
        var robot = TwoLinkRobot.Build();
        Span<Matrix4x4> xf = stackalloc Matrix4x4[RobotCollisionModel.LinkCount];
        Span<float> krl = stackalloc float[6];

        robot.ComputeLinkTransforms(krl, Matrix4x4.Identity, xf);
        // At zero pose: joint_2 world sits at +100 X.
        Assert.Equal(100f, xf[2].Translation.X, 2);
        Assert.Equal(0f, xf[2].Translation.Y, 2);

        // Rotate joint_1 by -90° about Y (X→Z axis swing for row-vector convention).
        krl[0] = -90f;
        robot.ComputeLinkTransforms(krl, Matrix4x4.Identity, xf);
        Assert.Equal(100f, MathF.Abs(xf[2].Translation.Z), 1);
        Assert.True(MathF.Abs(xf[2].Translation.X) < 1f);
    }

    [Fact]
    public void SelfCollision_FoldedElbow_Hits()
    {
        var robot = TwoLinkRobot.Build();
        var world = new CollisionWorld(robot, null,
            new CollisionSettings { CheckEnvironment = false, CheckMaterial = false });
        var scratch = new CollisionWorld.Scratch();

        // Straight arm: L1 spans [0,100], L2 spans [100,200] — adjacent-excluded, no hit.
        Span<float> straight = stackalloc float[6];
        Assert.False(world.CheckPose(straight, Matrix4x4.Identity, -1, default, scratch, out _));

        // Fold joint_2 fully back: L2 overlaps L1 — self collision (pair not adjacent-excluded?
        // L1-L2 IS adjacent... so geometry overlap between L1 and L2 is excluded by design.
        // Instead give L3 geometry riding joint_3 (identity at joint_2's frame) and fold
        // joint_2 — L3's slab folds back over L1: pair (1,3) is NOT excluded.
        var linkHulls = new IReadOnlyList<ConvexHull>[RobotCollisionModel.LinkCount];
        for (int i = 0; i < linkHulls.Length; i++) linkHulls[i] = [];
        // L1 slab stops at 98 so the straight pose has a real 2mm gap to L3's slab
        // (which starts at joint_2/3, x=100) — exact face contact would rightly flag.
        linkHulls[1] = new[] { TwoLinkRobot.Slab(98f, 10f) };
        linkHulls[3] = new[] { TwoLinkRobot.Slab(100f, 10f) };
        var rest = new Matrix4x4[6];
        rest[0] = Matrix4x4.Identity;
        rest[1] = Matrix4x4.CreateTranslation(100f, 0f, 0f);
        for (int i = 2; i < 6; i++) rest[i] = Matrix4x4.Identity;
        var cfg = new JointConfig[6];
        for (int i = 0; i < 6; i++) cfg[i] = new JointConfig { KrlSign = 1f, Axis = RotationAxis.Y };
        var robot2 = new RobotCollisionModel(linkHulls, rest, cfg, Matrix4x4.Identity);
        var world2 = new CollisionWorld(robot2, null,
            new CollisionSettings { CheckEnvironment = false, CheckMaterial = false });

        Assert.False(world2.CheckPose(straight, Matrix4x4.Identity, -1, default, scratch, out _));

        Span<float> folded = stackalloc float[6];
        folded[1] = 180f; // joint_2 folds L3's slab back over L1
        Assert.True(world2.CheckPose(folded, Matrix4x4.Identity, -1, default, scratch, out var hit));
        Assert.Equal(CollisionClass.Self, hit.Class);
    }

    [Fact]
    public void SelfBaseline_ExcludesRestingOverlap()
    {
        // L1 and L3 slabs both at the origin (identity rests) → they overlap at home.
        var linkHulls = new IReadOnlyList<ConvexHull>[RobotCollisionModel.LinkCount];
        for (int i = 0; i < linkHulls.Length; i++) linkHulls[i] = [];
        linkHulls[1] = new[] { TwoLinkRobot.Slab(100f, 10f) };
        linkHulls[3] = new[] { TwoLinkRobot.Slab(100f, 10f) };
        var rest = new Matrix4x4[6];
        for (int i = 0; i < 6; i++) rest[i] = Matrix4x4.Identity;
        var cfg = new JointConfig[6];
        for (int i = 0; i < 6; i++) cfg[i] = new JointConfig { KrlSign = 1f, Axis = RotationAxis.Y };
        var robot = new RobotCollisionModel(linkHulls, rest, cfg, Matrix4x4.Identity);

        Span<float> home = stackalloc float[6];
        var excluded = robot.ApplySelfBaseline(home, Matrix4x4.Identity, 1f);
        Assert.Contains(excluded, e => (e.a == 1 && e.b == 3));
        Assert.True(robot.IsExcluded(1, 3));
    }

    [Fact]
    public void Environment_ArmIntoBox_Hits()
    {
        var robot = TwoLinkRobot.Build();
        // Environment: a wall (two triangles) at x = 150, spanning y/z ±500.
        var v = new Vector3[]
        {
            new(150, -500, -500), new(150, 500, -500), new(150, 500, 500),
            new(150, -500, -500), new(150, 500, 500), new(150, -500, 500),
        };
        var env = new EnvironmentCollider(v, ["wall", "wall"]);
        var world = new CollisionWorld(robot, env,
            new CollisionSettings { CheckSelf = false, CheckMaterial = false, ClearanceMm = 10f });
        var scratch = new CollisionWorld.Scratch();

        // Straight arm reaches to x=200 → punches through the wall at 150.
        Span<float> straight = stackalloc float[6];
        Assert.True(world.CheckPose(straight, Matrix4x4.Identity, -1, default, scratch, out var hit));
        Assert.Equal(CollisionClass.Environment, hit.Class);
        Assert.Equal("wall", hit.Other);

        // Swing joint_1 by -90°: arm goes up +Z, clear of the wall.
        Span<float> up = stackalloc float[6];
        up[0] = -90f;
        Assert.False(world.CheckPose(up, Matrix4x4.Identity, -1, default, scratch, out _));
    }
}

public sealed class BeadObstacleGridTest
{
    static Toolpath TwoLayerPath()
    {
        var tp = new Toolpath();
        for (int li = 0; li < 2; li++)
        {
            var layer = new ToolpathLayer(li, li * 3f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
            for (int m = 0; m < 10; m++)
                layer.Moves.Add(new ToolpathMove(
                    new Vector3(m * 10f, 0f, li * 3f),
                    new Vector3((m + 1) * 10f, 0f, li * 3f),
                    MoveKind.Extrude));
            tp.Layers.Add(layer);
        }
        return tp;
    }

    [Fact]
    public void PrefixFilter_HidesFutureBeads()
    {
        var grid = new BeadObstacleGrid(TwoLayerPath(), beadWidthMm: 6f);
        var hits = new List<int>();
        var seen = new HashSet<int>();
        var box = new Aabb(new Vector3(-5, -5, -5), new Vector3(105, 5, 10));

        // Only beads with flat index <= 4 exist.
        grid.Query(box, maxFlatIdx: 4, tcp: new Vector3(1000, 1000, 1000),
            nozzleExcludeR: 1f, minTopZ: -1000f, hits, seen);
        Assert.Equal(5, hits.Count);
        foreach (var h in hits) Assert.True(grid.FlatIndex(h) <= 4);

        hits.Clear();
        grid.Query(box, maxFlatIdx: 19, tcp: new Vector3(1000, 1000, 1000),
            nozzleExcludeR: 1f, minTopZ: -1000f, hits, seen);
        Assert.Equal(20, hits.Count);
    }

    [Fact]
    public void NozzlePlaneGate_HidesMaterialAtOrBelowNozzle()
    {
        // Two layers: layer0 beads centered z=0 (top 1.5), layer1 centered z=3 (top 4.5).
        // Printing at nozzle z=4.5 (top of layer 1): NOTHING should be an obstacle —
        // the extruder always hovers above what it printed. Only a bead whose top
        // rises above the nozzle plane may collide.
        var grid = new BeadObstacleGrid(TwoLayerPath(), beadWidthMm: 6f);
        var hits = new List<int>();
        var seen = new HashSet<int>();
        var box = new Aabb(new Vector3(-5, -5, -100), new Vector3(105, 5, 100));

        grid.Query(box, maxFlatIdx: 19, tcp: new Vector3(1000, 1000, 4.5f),
            nozzleExcludeR: 1f, minTopZ: 4.5f + 2f, hits, seen);
        Assert.Empty(hits);

        // Nozzle down at layer-0 height (z=1.5): layer-1 beads (top 4.5) tower above
        // the plane → visible obstacles; layer-0 beads stay hidden.
        hits.Clear();
        grid.Query(box, maxFlatIdx: 19, tcp: new Vector3(1000, 1000, 1.5f),
            nozzleExcludeR: 1f, minTopZ: 1.5f + 2f, hits, seen);
        Assert.Equal(10, hits.Count);
        foreach (var h in hits)
            Assert.True(grid.Obb(h).Bounds.Max.Z > 3.5f);
    }

    [Fact]
    public void NozzleExclusion_SkipsNearbyBeads()
    {
        var grid = new BeadObstacleGrid(TwoLayerPath(), beadWidthMm: 6f);
        var hits = new List<int>();
        var seen = new HashSet<int>();
        var box = new Aabb(new Vector3(-5, -5, -5), new Vector3(105, 5, 10));

        // TCP at (50,0,3): beads centered within 40mm get skipped.
        grid.Query(box, maxFlatIdx: 19, tcp: new Vector3(50, 0, 3),
            nozzleExcludeR: 40f, minTopZ: -1000f, hits, seen);
        foreach (var h in hits)
        {
            var obb = grid.Obb(h);
            Assert.True(Vector3.Distance(obb.Bounds.Center, new Vector3(50, 0, 3)) >= 35f);
        }
        Assert.True(hits.Count > 0);
        Assert.True(hits.Count < 20);
    }
}

public sealed class ToolpathCollisionCheckerTest
{
    [Fact]
    public void DescendingArm_FlagsExpectedRange_AndGatingMatchesExhaustive()
    {
        // Robot: 100-long slab on L1 rotating about Y. Environment: floor at z = -60.
        var robot = TwoLinkRobot.Build();
        var floor = new Vector3[]
        {
            new(-500, -500, -60), new(500, -500, -60), new(500, 500, -60),
            new(-500, -500, -60), new(500, 500, -60), new(-500, 500, -60),
        };
        var env = new EnvironmentCollider(floor, ["floor", "floor"]);
        var settings = new CollisionSettings
        {
            CheckSelf = false, CheckMaterial = false, ClearanceMm = 5f,
            JointDeltaGateDeg = 0.5f, TcpDeltaGateMm = 5f,
        };
        var world = new CollisionWorld(robot, env, settings);

        // Sweep joint_1 from 0° to 120°: the slab tip dips below z=-55 partway through.
        // (rotation about +Y at +angle sends +X toward -Z in row-vector convention…
        // either sign — we just need a known-bad span, verified against CheckPose.)
        int total = 240;
        var solutions = new float[total][];
        var roots = new Matrix4x4[total];
        var tcp = new Vector3[total];
        for (int i = 0; i < total; i++)
        {
            solutions[i] = [i * 0.5f, 0, 0, 0, 0, 0];
            roots[i] = Matrix4x4.Identity;
            tcp[i] = new Vector3(0, 0, 1000); // far away — nozzle exclusion irrelevant
        }

        var result = ToolpathCollisionChecker.Check(world, solutions, roots, tcp);

        // Exhaustive reference.
        var scratch = new CollisionWorld.Scratch();
        int mismatches = 0;
        for (int i = 0; i < total; i++)
        {
            bool expected = world.CheckPose(solutions[i], roots[i], -1, tcp[i], scratch, out _);
            if (expected != result.Colliding[i]) mismatches++;
        }
        // Pose-delta gating may smear a verdict across moves within the gate width
        // (0.5°/step == exactly the gate) — allow a tiny transition band.
        Assert.True(mismatches <= 4, $"gating drifted {mismatches} moves from exhaustive");
        Assert.Contains(true, result.Colliding);
        Assert.Contains(false, result.Colliding);
        Assert.Equal(1, result.SampleStride);
    }
}
