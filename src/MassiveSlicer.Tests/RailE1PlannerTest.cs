using System.Numerics;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class RailE1PlannerTest
{
    private static RobotRailCellConfig YRail(float min = -4000f, float max = 4000f, float sign = 1f)
        => new() { Axis = "Y", MinMm = min, MaxMm = max, E1Sign = sign };

    [Fact]
    public void IdealE1_TracksWorldY_WithinAllowance()
    {
        var rail = YRail();
        var home = new Vector3(0, 0, 0);
        float homeE1 = 0f;
        // Want base at Y=300 → e1 = 300 when sign=1
        float e1 = RailE1Planner.IdealE1(new Vector3(100, 300, 50), home, rail, homeE1, yPlusMm: 500, yMinusMm: 500);
        Assert.InRange(e1, 299f, 301f);
    }

    [Fact]
    public void IdealE1_ClampsToYPlusYMinusFromHome()
    {
        var rail = YRail(min: -5000, max: 5000);
        var home = new Vector3(0, 0, 0);
        float homeE1 = 100f;
        // Ideal would be 1000, but +allowance is only 200 → clamp to 300
        float e1 = RailE1Planner.IdealE1(new Vector3(0, 1000, 0), home, rail, homeE1, yPlusMm: 200, yMinusMm: 50);
        Assert.Equal(300f, e1, 1);
    }

    [Fact]
    public void IdealE1_RespectsRailSoftLimits()
    {
        var rail = YRail(min: -100f, max: 50f);
        var home = new Vector3(0, 0, 0);
        float e1 = RailE1Planner.IdealE1(new Vector3(0, 500, 0), home, rail, homeE1Mm: 0, yPlusMm: 1000, yMinusMm: 1000);
        // Usable stop is the 5% envelope, not the raw soft limit. Span 150 mm → 7.5 mm inset.
        Assert.Equal(42.5f, e1, 1);
    }

    [Fact]
    public void SmoothToward_Blends()
    {
        float s = RailE1Planner.SmoothToward(0f, 100f, blend: 0.25f);
        Assert.InRange(s, 24f, 26f);
    }

    [Fact]
    public void PickBestE1_PrefersReachableSampleOverUnreachableHome()
    {
        var rail = YRail(min: -2000, max: 2000);
        var home = new Vector3(0, 0, 0);
        // TCP far along +Y; home E1=0 puts base at 0 → large dxy if we claim only e1≈800 is reachable
        var tcp = new Vector3(0, 800, 200);
        float pick = RailE1Planner.PickBestE1(
            tcp, home, rail, homeE1Mm: 0, yPlusMm: 1000, yMinusMm: 1000,
            prevE1: 0, preferredHorizReachMm: 100f,
            inWorkspace: rel =>
            {
                // Only near-zero relative Y is "reachable" → forces E1 ≈ 800
                float dxy = MathF.Sqrt(rel.X * rel.X + rel.Y * rel.Y);
                return dxy < 150f;
            },
            gridCount: 11);
        Assert.InRange(pick, 650f, 950f);
    }

    [Fact]
    public void PlanPath_VariesE1AlongYSpan()
    {
        var rail = YRail(min: -3000, max: 3000);
        var home = new Vector3(0, 0, 0);
        var pts = new List<Vector3>();
        for (int i = 0; i <= 10; i++)
            pts.Add(new Vector3(0, i * 100f, 100)); // Y 0..1000
        float[] e1 = RailE1Planner.PlanPath(
            pts, home, rail, homeE1Mm: 0, yPlusMm: 1200, yMinusMm: 200,
            preferredHorizReachMm: 50f, inWorkspace: rel =>
            {
                float dxy = MathF.Sqrt(rel.X * rel.X + rel.Y * rel.Y);
                return dxy < 120f;
            },
            gridCount: 11, smoothBlend: 0.5f);
        Assert.Equal(pts.Count, e1.Length);
        // Should trend upward along the path
        Assert.True(e1[^1] > e1[0] + 200f,
            $"expected E1 to track +Y path, start={e1[0]:0} end={e1[^1]:0}");
    }

    [Fact]
    public void PlanLayer_HoldsConstantE1WhenOnePoseCoversLayer()
    {
        var rail = YRail(min: -2000, max: 2000);
        var home = new Vector3(0, 0, 0);
        var pts = new List<Vector3>();
        for (int i = 0; i < 8; i++)
            pts.Add(new Vector3(100, 360 + i * 10f, 50));

        var plan = RailE1Planner.PlanLayer(
            pts, home, rail, homeE1Mm: 0, yPlusMm: 1500, yMinusMm: 1500,
            prevE1Mm: 400f, poseOk: ReachWindow(home, 200f));

        Assert.Equal(pts.Count, plan.E1Mm.Length);
        Assert.Equal(0, plan.GlideCount);
        foreach (float e in plan.E1Mm)
            Assert.InRange(e, 399.5f, 400.5f);
    }

    [Fact]
    public void PlanLayer_UsesNearestCoveringE1_NotAGridCell()
    {
        var rail = YRail(min: -2000, max: 2000);
        var home = new Vector3(0, 0, 0);
        var pts = new List<Vector3>();
        for (int i = 0; i < 6; i++)
            pts.Add(new Vector3(0, 800, 50));

        var plan = RailE1Planner.PlanLayer(
            pts, home, rail, homeE1Mm: 0, yPlusMm: 1500, yMinusMm: 1500,
            prevE1Mm: 0f, poseOk: ReachWindow(home, 200f));

        Assert.Equal(0, plan.GlideCount);
        foreach (float e in plan.E1Mm)
            Assert.InRange(e, 600.5f, 610f);
        AssertAllReachable(pts, plan.E1Mm, ReachWindow(home, 200f));
    }

    [Fact]
    public void PlanLayer_GlideIsLinearInPathLength_NotPointIndex()
    {
        var rail = YRail(min: -2000, max: 2000);
        var home = new Vector3(0, 0, 0);
        // Short beads, then one long move. Index interpolation would put most of the
        // rail change on the last step; path-length interpolation does not.
        var pts = new List<Vector3>
        {
            new(0, 0, 100),
            new(0, 10, 100),
            new(0, 20, 100),
            new(0, 30, 100),
            new(0, 400, 100),
        };
        var ok = ReachWindow(home, 250f);
        var plan = RailE1Planner.PlanLayer(
            pts, home, rail, homeE1Mm: 0, yPlusMm: 1500, yMinusMm: 1500,
            prevE1Mm: 0f, poseOk: ok);

        Assert.Equal(1, plan.GlideCount);
        AssertAllReachable(pts, plan.E1Mm, ok);
        float cover = plan.E1Mm[^1];
        float pathFrac = 30f / 400f;
        float expected = pathFrac * cover;
        float indexExpected = 0.75f * cover;
        Assert.InRange(plan.E1Mm[3], expected - 20f, expected + 20f);
        Assert.True(MathF.Abs(plan.E1Mm[3] - expected) + 40f < MathF.Abs(plan.E1Mm[3] - indexExpected),
            $"E1={string.Join(", ", plan.E1Mm.Select(v => v.ToString("0")))} expected~{expected:0} index~{indexExpected:0}");
    }

    [Fact]
    public void PlanLayer_LongWall_FewMonotonicGlides()
    {
        var rail = YRail(min: -3000, max: 3000);
        var home = new Vector3(0, 0, 0);
        var pts = new List<Vector3>();
        for (int i = 0; i <= 40; i++)
            pts.Add(new Vector3(0, i * 50f, 100));
        var ok = ReachWindow(home, 300f);
        var plan = RailE1Planner.PlanLayer(
            pts, home, rail, homeE1Mm: 0, yPlusMm: 2500, yMinusMm: 200,
            prevE1Mm: 0f, poseOk: ok);

        Assert.InRange(plan.GlideCount, 1, 6);
        AssertAllReachable(pts, plan.E1Mm, ok);
        for (int i = 1; i < plan.E1Mm.Length; i++)
            Assert.True(plan.E1Mm[i] >= plan.E1Mm[i - 1] - 0.5f,
                $"reversal at {i}: {string.Join(", ", plan.E1Mm.Select(v => v.ToString("0")))}");
        Assert.True(MaxRailPerPath(pts, plan.E1Mm) < 2f,
            $"rail/path {MaxRailPerPath(pts, plan.E1Mm):0.00} E1={string.Join(", ", plan.E1Mm.Select(v => v.ToString("0")))}");
    }

    [Fact]
    public void PlanLayer_Circle_TwoHoldsAndTwoGlides()
    {
        var rail = YRail(min: -2000, max: 2000);
        var home = new Vector3(0, 0, 0);
        var pts = new List<Vector3>();
        const int n = 48;
        for (int i = 0; i < n; i++)
        {
            float a = -MathF.PI / 2f + i * MathF.Tau / n;
            pts.Add(new Vector3(600f * MathF.Cos(a), 600f * MathF.Sin(a), 100));
        }
        var ok = ReachWindow(home, 280f);
        var plan = RailE1Planner.PlanLayer(
            pts, home, rail, homeE1Mm: 0, yPlusMm: 1500, yMinusMm: 1500,
            prevE1Mm: -400f, poseOk: ok);

        Assert.Equal(2, plan.GlideCount);
        AssertAllReachable(pts, plan.E1Mm, ok);
        Assert.True(MaxRailPerPath(pts, plan.E1Mm) < 1.2f,
            $"rail/path {MaxRailPerPath(pts, plan.E1Mm):0.00} E1={string.Join(", ", plan.E1Mm.Select(v => v.ToString("0")))}");
        int reversals = 0;
        float prevDelta = 0f;
        for (int i = 1; i < plan.E1Mm.Length; i++)
        {
            float d = plan.E1Mm[i] - plan.E1Mm[i - 1];
            if (MathF.Abs(d) < 1f) continue;
            if (prevDelta != 0f && MathF.Sign(d) != MathF.Sign(prevDelta)) reversals++;
            prevDelta = d;
        }
        Assert.Equal(1, reversals);
    }

    [Fact]
    public void PlanLayer_DoesNotCrossAForbiddenPose()
    {
        var rail = YRail(min: -2000, max: 2000);
        var home = new Vector3(0, 0, 0);
        var pts = new List<Vector3>();
        for (int y = 0; y <= 800; y += 100)
            pts.Add(new Vector3(0, y, 100));

        bool Ok(Vector3 world, float e1) =>
            MathF.Abs(world.Y - e1) < 250f
            && !(e1 > 380f && e1 < 420f && world.Y > 380f && world.Y < 420f);

        var plan = RailE1Planner.PlanLayer(
            pts, home, rail, homeE1Mm: 0, yPlusMm: 1500, yMinusMm: 1500,
            prevE1Mm: 0f, poseOk: Ok);

        AssertAllReachable(pts, plan.E1Mm, Ok);
        Assert.True(plan.GlideCount <= 3,
            $"glides={plan.GlideCount} E1={string.Join(", ", plan.E1Mm.Select(v => v.ToString("0")))}");
    }

    [Fact]
    public void TryPullGlideBack_RefusesWhenTheTailCannotReach()
    {
        var home = new Vector3(0, 0, 0);
        var pts = new List<Vector3>();
        for (int i = 0; i < 5; i++)
            pts.Add(new Vector3(0, 0, 100));
        var e1 = new[] { 0f, 0f, 0f, 0f, 0f };
        bool pulled = RailE1Planner.TryPullGlideBack(e1, pts, targetE1: 600f, ReachWindow(home, 200f));
        Assert.False(pulled);
        Assert.All(e1, v => Assert.Equal(0f, v));
    }

    /// <summary>Base within <paramref name="windowMm"/> of the point along Y is reachable.</summary>
    private static Func<Vector3, float, bool> ReachWindow(Vector3 home, float windowMm)
        => (world, e1) => MathF.Abs(world.Y - (home.Y + e1)) < windowMm
                          && MathF.Abs(world.X - home.X) < 800f;

    private static void AssertAllReachable(
        IReadOnlyList<Vector3> pts, float[] e1, Func<Vector3, float, bool> ok)
    {
        Assert.Equal(pts.Count, e1.Length);
        for (int i = 0; i < pts.Count; i++)
            Assert.True(ok(pts[i], e1[i]),
                $"point {i} Y={pts[i].Y:0} E1={e1[i]:0} not reachable. " +
                string.Join(", ", e1.Select(v => v.ToString("0"))));
        var s = new float[pts.Count];
        for (int i = 1; i < pts.Count; i++)
            s[i] = s[i - 1] + Vector3.Distance(pts[i], pts[i - 1]);
        for (int i = 1; i < pts.Count; i++)
        {
            float span = s[i] - s[i - 1];
            if (span < 1f) continue;
            for (float d = 20f; d < span; d += 20f)
            {
                float u = d / span;
                var world = Vector3.Lerp(pts[i - 1], pts[i], u);
                float e = e1[i - 1] + (e1[i] - e1[i - 1]) * u;
                Assert.True(ok(world, e),
                    $"sample between {i - 1} and {i} at {u:0.00} Y={world.Y:0} E1={e:0}");
            }
        }
    }

    private static float MaxRailPerPath(IReadOnlyList<Vector3> pts, float[] e1)
    {
        float worst = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            float ds = Vector3.Distance(pts[i], pts[i - 1]);
            if (ds < 1f) continue;
            worst = MathF.Max(worst, MathF.Abs(e1[i] - e1[i - 1]) / ds);
        }
        return worst;
    }
}
