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
        Assert.Equal(50f, e1, 1);
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
}
