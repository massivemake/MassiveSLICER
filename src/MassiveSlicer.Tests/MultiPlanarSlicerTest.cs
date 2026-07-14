using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

public class MultiPlanarSlicerTest
{
    /// <summary>Closed cylinder r=80, h=300 (triangle soup).</summary>
    private static Vector3[] Cylinder(float r = 80f, float h = 300f, int seg = 48)
    {
        var tris = new List<Vector3>();
        for (int i = 0; i < seg; i++)
        {
            float a0 = MathF.Tau * i / seg, a1 = MathF.Tau * (i + 1) / seg;
            var b0 = new Vector3(r * MathF.Cos(a0), r * MathF.Sin(a0), 0);
            var b1 = new Vector3(r * MathF.Cos(a1), r * MathF.Sin(a1), 0);
            var t0 = b0 with { Z = h };
            var t1 = b1 with { Z = h };
            tris.AddRange([b0, b1, t1]);
            tris.AddRange([b0, t1, t0]);
            tris.AddRange([new Vector3(0, 0, 0), b1, b0]);   // bottom cap
            tris.AddRange([new Vector3(0, 0, h), t0, t1]);   // top cap
        }
        return [.. tris];
    }

    private static SliceSettings Settings(bool axisX = false) => new()
    {
        LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = 6f,
        MultiPlanarPlanes = [new(0f, 0f), new(50f, 15f), new(100f, 30f)],
        MultiPlanarAxisX = axisX,
    };

    [Fact]
    public void PlanesRotateFromBaseToTopAndLayersAreWedges()
    {
        var tp = AngledPlanarSlicer.SliceMultiPlanar([Cylinder()], Settings());
        Assert.True(tp.Layers.Count > 60, $"only {tp.Layers.Count} layers");

        // Plane normals rotate: flat at the bottom, ~tilted at the top. The per-layer
        // rotation clamp (0.75·layerH/lever) may not reach the full 30° on this
        // squat part — assert monotonic progress and a substantial final tilt.
        float TiltDeg(ToolpathLayer l) =>
            MathF.Atan2(MathF.Abs(l.PlaneNormal.X), l.PlaneNormal.Z) * 180f / MathF.PI;
        Assert.True(TiltDeg(tp.Layers[0]) < 1.5f, $"base tilt {TiltDeg(tp.Layers[0]):0.#}");
        float topTilt = TiltDeg(tp.Layers[^1]);
        Assert.True(topTilt > 10f, $"top tilt only {topTilt:0.#}°");
        for (int i = 1; i < tp.Layers.Count; i++)
            Assert.True(TiltDeg(tp.Layers[i]) >= TiltDeg(tp.Layers[i - 1]) - 0.01f,
                $"tilt regressed at layer {i}");

        // Wedge thickness: on a rotating layer, the +X side must be measurably
        // thicker or thinner than the −X side, and HeightScale must stay clamped.
        var mid = tp.Layers[tp.Layers.Count / 2];
        var ext = mid.Moves.Where(m => m.Kind == MoveKind.Extrude && !m.IsLayerStitch).ToList();
        Assert.NotEmpty(ext);
        float plusX  = ext.Where(m => (m.From.X + m.To.X) * 0.5f > 40f).Select(m => m.HeightScale).DefaultIfEmpty(1f).Average();
        float minusX = ext.Where(m => (m.From.X + m.To.X) * 0.5f < -40f).Select(m => m.HeightScale).DefaultIfEmpty(1f).Average();
        Assert.True(MathF.Abs(plusX - minusX) > 0.08f,
            $"no wedge: +X scale {plusX:0.###} vs −X {minusX:0.###}");
        foreach (var m in ext)
            Assert.InRange(m.HeightScale, 0.25f, 3f);

        // Average thickness stays near nominal (wedge is balanced about the axis).
        float avg = ext.Average(m => m.HeightScale);
        Assert.InRange(avg, 0.75f, 1.3f);
    }

    [Fact]
    public void AxisToggleLeansAlongY()
    {
        var tp = AngledPlanarSlicer.SliceMultiPlanar([Cylinder()], Settings(axisX: true));
        Assert.True(tp.Layers.Count > 60);
        var top = tp.Layers[^1].PlaneNormal;
        Assert.True(MathF.Abs(top.Y) > 0.15f, $"expected Y lean, normal={top}");
        Assert.True(MathF.Abs(top.X) < 0.02f, $"unexpected X lean, normal={top}");

        // Wedge now varies along Y, not X.
        var mid = tp.Layers[tp.Layers.Count / 2];
        var ext = mid.Moves.Where(m => m.Kind == MoveKind.Extrude && !m.IsLayerStitch).ToList();
        float plusY  = ext.Where(m => (m.From.Y + m.To.Y) * 0.5f > 40f).Select(m => m.HeightScale).DefaultIfEmpty(1f).Average();
        float minusY = ext.Where(m => (m.From.Y + m.To.Y) * 0.5f < -40f).Select(m => m.HeightScale).DefaultIfEmpty(1f).Average();
        Assert.True(MathF.Abs(plusY - minusY) > 0.08f, $"no Y wedge: {plusY:0.###} vs {minusY:0.###}");
    }

    [Fact]
    public void FivePlaneStackInterpolatesThroughEveryGuide()
    {
        var s = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = 6f,
            MultiPlanarPlanes =
                [new(0f, 0f), new(25f, 10f), new(50f, 5f), new(75f, 20f), new(100f, 30f)],
        };
        var tp = AngledPlanarSlicer.SliceMultiPlanar([Cylinder()], s);
        float TiltDeg(ToolpathLayer l) =>
            MathF.Atan2(MathF.Abs(l.PlaneNormal.X), l.PlaneNormal.Z) * 180f / MathF.PI;
        // Tilt rises to ~10 by a quarter height, dips toward 5 at half, climbs after.
        var quarter = tp.Layers[tp.Layers.Count / 4];
        var half    = tp.Layers[tp.Layers.Count / 2];
        Assert.True(TiltDeg(quarter) > TiltDeg(half) - 8f && TiltDeg(quarter) > 4f,
            $"quarter {TiltDeg(quarter):0.#}° vs half {TiltDeg(half):0.#}°");
        Assert.True(TiltDeg(tp.Layers[^1]) > 12f, $"top only {TiltDeg(tp.Layers[^1]):0.#}°");
    }

    /// <summary>Flat-top vessel: cylinder closing to a small cap — the closing top
    /// demands Formbound Bridge support fingers.</summary>
    private static Vector3[] FlatTopVessel(int seg = 48)
    {
        (float r, float z)[] profile = [(60f, 0f), (60f, 117f), (6f, 123f)];
        var tris = new List<Vector3>();
        for (int k = 0; k < profile.Length - 1; k++)
        {
            var (r0, z0) = profile[k];
            var (r1, z1) = profile[k + 1];
            for (int i = 0; i < seg; i++)
            {
                float a0 = MathF.Tau * i / seg, a1 = MathF.Tau * (i + 1) / seg;
                var p00 = new Vector3(r0 * MathF.Cos(a0), r0 * MathF.Sin(a0), z0);
                var p01 = new Vector3(r0 * MathF.Cos(a1), r0 * MathF.Sin(a1), z0);
                var p10 = new Vector3(r1 * MathF.Cos(a0), r1 * MathF.Sin(a0), z1);
                var p11 = new Vector3(r1 * MathF.Cos(a1), r1 * MathF.Sin(a1), z1);
                tris.AddRange([p00, p01, p11]);
                tris.AddRange([p00, p11, p10]);
                tris.AddRange([new Vector3(0, 0, 0), p01, p00]);
                tris.AddRange([new Vector3(0, 0, 123f), p10, p11]);
            }
        }
        return [.. tris];
    }

    [Fact]
    public void FormboundBridgeGrowsFingersUnderMultiPlanar()
    {
        var s = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = 6f,
            InfillPattern = InfillPattern.LightningBridge, LightningOverhangDeg = 30f,
            LightningAnchorInterior = true, LightningAnchorExterior = true,
            MultiPlanarPlanes = [new(0f, 0f), new(50f, 10f), new(100f, 20f)],
        };
        var tp = AngledPlanarSlicer.SliceMultiPlanar([FlatTopVessel()], s);
        Assert.True(tp.Layers.Count > 30, $"only {tp.Layers.Count} layers");

        // Fingers exist under the closing top and are tagged.
        int lightningMoves = tp.Layers.SelectMany(l => l.Moves).Count(m => m.IsLightning);
        Assert.True(lightningMoves > 50, $"only {lightningMoves} Formbound moves");

        // Continuity per layer (fingers are perimeter detours, not islands).
        foreach (var layer in tp.Layers)
        {
            int travels = layer.Moves.Count(m =>
                m.Kind == MoveKind.Travel && !m.IsLayerChange && !m.IsZHop);
            Assert.True(travels <= 2, $"z={layer.Z:0.#}: {travels} travels");
        }

        // Support: fingers rest within reach of the previous layer's material.
        float allowance = MathF.Max(1.74f + 3f + 0.75f, 4f * 6f * 0.6f) + 6.5f;
        for (int li = 1; li < tp.Layers.Count; li++)
        {
            var below = tp.Layers[li - 1].Moves.Where(m => m.Kind == MoveKind.Extrude).ToList();
            if (below.Count == 0) continue;
            foreach (var m in tp.Layers[li].Moves)
            {
                if (!m.IsLightning || m.Kind != MoveKind.Extrude) continue;
                var mid = (m.From + m.To) * 0.5f;
                float best = float.MaxValue;
                foreach (var b in below)
                {
                    var ab = b.To - b.From;
                    float len2 = ab.LengthSquared();
                    float t = len2 < 1e-9f ? 0f : Math.Clamp(Vector3.Dot(mid - b.From, ab) / len2, 0f, 1f);
                    float d = (mid - (b.From + ab * t)).Length();
                    if (d < best) best = d;
                    if (best < allowance) break;
                }
                Assert.True(best <= allowance,
                    $"floating finger at z={tp.Layers[li].Z:0.#}: {best:0.##} mm");
            }
        }
    }

    /// <summary>Regression: an aggressive reversing plane stack (like the Drone V52
    /// setup) makes the plane frame rotate fast enough that plane-local coordinates
    /// drift several mm per layer. The planner must compare layers in physical space —
    /// otherwise it hallucinates unsupported arcs along whole edges, spawns a new
    /// finger row every layer, and the merged slits eat the perimeter wall.</summary>
    [Fact]
    public void AggressiveReversingStackKeepsThePerimeter()
    {
        var mesh = Cylinder(r: 150f, h: 400f);
        SliceSettings S(InfillPattern pattern) => new()
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = 6f,
            InfillPattern = pattern, LightningOverhangDeg = 30f,
            LightningAnchorInterior = true, LightningAnchorExterior = true,
            MultiPlanarPlanes = [new(0f, 0f), new(60f, 45f), new(80f, -30f)],
        };

        var reference = AngledPlanarSlicer.SliceMultiPlanar([mesh], S(InfillPattern.None));
        var lightning = AngledPlanarSlicer.SliceMultiPlanar([mesh], S(InfillPattern.LightningBridge));
        Assert.Equal(reference.Layers.Count, lightning.Layers.Count);

        // Every point of the plain perimeter must have printed material nearby in the
        // Formbound slice. Finger mouths notch the wall ~1 bead wide, so 2×bead of
        // slack is generous — the bug produced 40–60 mm losses.
        float allowance = 2f * 6f;
        float worst = 0f; int badLayers = 0; float worstZ = 0f;
        for (int li = 0; li < reference.Layers.Count; li++)
        {
            var walls = lightning.Layers[li].Moves
                .Where(m => m.Kind == MoveKind.Extrude).ToList();
            if (walls.Count == 0) continue;
            float layerWorst = 0f;
            foreach (var pm in reference.Layers[li].Moves)
            {
                if (pm.Kind != MoveKind.Extrude || pm.IsLayerStitch) continue;
                var mid = (pm.From + pm.To) * 0.5f;
                float best = float.MaxValue;
                foreach (var w in walls)
                {
                    var ab = w.To - w.From;
                    float len2 = ab.LengthSquared();
                    float t = len2 < 1e-9f ? 0f
                        : Math.Clamp(Vector3.Dot(mid - w.From, ab) / len2, 0f, 1f);
                    float d = (mid - (w.From + ab * t)).Length();
                    if (d < best) best = d;
                    if (best < allowance) break;
                }
                if (best > layerWorst) layerWorst = best;
            }
            if (layerWorst > allowance) badLayers++;
            if (layerWorst > worst) { worst = layerWorst; worstZ = reference.Layers[li].Z; }
        }
        Assert.True(badLayers == 0,
            $"perimeter lost on {badLayers} layers, worst gap {worst:0.#} mm at z={worstZ:0.#}");
    }

    [Fact]
    public void ConsecutivePlanesNeverCrossInsideThePart()
    {
        var tp = AngledPlanarSlicer.SliceMultiPlanar([Cylinder()], Settings());
        // If planes crossed, some moves would report (clamped) near-zero thickness.
        int nearZero = tp.Layers.Skip(1).SelectMany(l => l.Moves)
            .Count(m => m.Kind == MoveKind.Extrude && m.HeightScale <= 0.26f);
        Assert.True(nearZero == 0, $"{nearZero} moves at the crossing clamp");
    }
}
