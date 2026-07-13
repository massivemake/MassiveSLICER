using System.Numerics;
using Clipper2Lib;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Core.Slicing.Lightning;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Formbound Bridge: severity ranking should prefer free-edge overhangs over mild mid-wall demand.
/// </summary>
public sealed class FormboundSeverityTest
{
    private const float Bead = 6f;
    private const float LayerH = 3f;

    [Fact]
    public void CoverRadius_TightensAsSeverityGrows()
    {
        float supportR = 5f;
        float mild = LightningPlanner.CoverRadiusForSeverity(0.1f, supportR, Bead);
        float severe = LightningPlanner.CoverRadiusForSeverity(supportR * 2f, supportR, Bead);
        Assert.True(severe < mild,
            $"Severe cover ({severe}) should be tighter than mild ({mild})");
        Assert.True(mild >= supportR * 0.9f);
        Assert.True(severe < mild * 0.75f,
            $"Severe cover should shrink meaningfully ({severe} vs mild {mild})");
    }

    [Fact]
    public void DemandSeverity_LargerWhenFartherFromWall()
    {
        // Square region 0..100
        var region = new PathsD
        {
            new PathD
            {
                new PointD(0, 0), new PointD(100, 0),
                new PointD(100, 100), new PointD(0, 100),
            },
        };
        float supportR = 4f;
        float near = LightningPlanner.DemandSeverity(new Vector2(50, 6), region, null, supportR, Bead);
        float far  = LightningPlanner.DemandSeverity(new Vector2(50, 30), region, null, supportR, Bead);
        Assert.True(far > near, $"far severity {far} should exceed near {near}");
        Assert.True(near >= 0f);
        Assert.InRange(far, 20f, 40f); // ~30 - 4
    }

    [Fact]
    public void FreeEdgeExteriorPass_PlacesFinsUnderShallowOutwardSteps()
    {
        // Outer square grows only ~3 mm/layer — less than supportRadius (~5 mm with bead 6),
        // so the main "far" test used to miss free-edge demand. Free-edge pass uses
        // demandRadius and must place External fins when Affect Exterior is on.
        const int n = 10;
        var polys = new List<List<List<Vector2>>>();
        for (int i = 0; i < n; i++)
        {
            float h = 50f + i * 3f; // 3 mm step-out per layer
            polys.Add(
            [
                [new(-h, -h), new(h, -h), new(h, h), new(-h, h)],
            ]);
        }
        var heights = Enumerable.Repeat(3f, n).ToList();
        var plan = LightningPlanner.Build(polys, heights, new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
            InfillPattern = InfillPattern.LightningBridge,
            LightningOverhangDeg = 30f,
            LightningAnchorInterior = true,
            LightningAnchorExterior = true,
            LightningExteriorOverhangs = true,
        });

        int externalTrees = 0;
        for (int i = 0; i < n - 1; i++)
            externalTrees += plan.Layers[i].Trees.Count(t => t.External);
        Assert.True(externalTrees > 0,
            "shallow free-edge step-out must get exterior fins under Affect Exterior");
    }

    [Fact]
    public void ClosingVessel_StillSpawnsFingersUnderFastInwardWalls()
    {
        // Regression: severity-first must not kill classic closing-dome demand.
        (float r, float z)[] profile = [(60f, 0f), (60f, 117f), (6f, 123f)];
        int segments = 64;
        var v = new List<Vector3>();
        Vector3 P(float r, float z, int i)
        {
            float a = i / (float)segments * 2f * MathF.PI;
            return new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z);
        }
        for (int p = 0; p + 1 < profile.Length; p++)
        {
            var (r0, z0) = profile[p];
            var (r1, z1) = profile[p + 1];
            for (int i = 0; i < segments; i++)
            {
                var a0 = P(r0, z0, i); var a1 = P(r0, z0, i + 1);
                var b0 = P(r1, z1, i); var b1 = P(r1, z1, i + 1);
                if (r0 > 1e-3f) { v.AddRange([a0, a1, b0]); }
                if (r1 > 1e-3f) { v.AddRange([a1, b1, b0]); }
            }
        }
        var (rb, zb) = profile[0];
        var (rt, zt) = profile[^1];
        for (int i = 0; i < segments; i++)
        {
            if (rb > 1e-3f) v.AddRange([new Vector3(0, 0, zb), P(rb, zb, i + 1), P(rb, zb, i)]);
            if (rt > 1e-3f) v.AddRange([new Vector3(0, 0, zt), P(rt, zt, i), P(rt, zt, i + 1)]);
        }

        var settings = new SliceSettings
        {
            LayerHeight = LayerH, FirstLayerHeight = LayerH, BeadWidth = Bead,
            InfillPattern = InfillPattern.LightningBridge,
            LightningOverhangDeg = 30f,
        };
        var tp = PlanarSlicer.Slice([v.ToArray()], settings, null);
        float perimeter = 2f * MathF.PI * (60f - Bead / 2f);
        var nearTop = tp.Layers.Where(l => l.Z > 100f && l.Z < 118f).ToList();
        Assert.True(nearTop.Count > 2);
        float ExtrudeLen(ToolpathLayer l) =>
            l.Moves.Where(m => m.Kind == MoveKind.Extrude)
                   .Sum(m => Vector3.Distance(m.From, m.To));
        Assert.Contains(nearTop, l => ExtrudeLen(l) > perimeter * 1.3f);
    }
}
