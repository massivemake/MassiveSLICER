using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Core.Slicing.Effects;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Adaptive spacing for constant-tilt slices is the planar slope metric measured
/// along the plane normal. A roof a few degrees off that plane must tighten, while
/// the same roof measured in world Z (much steeper) must stay at the nominal height.
/// </summary>
public class AngledAdaptiveLayerHeightTest
{
    const float LayerH = 4f;
    const float FirstH = 3f;
    const float MinH   = 1f;

    [Fact]
    public void UnitZ_positions_match_the_planar_metric()
    {
        var n = TiltOptimizer.TiltToDirection(-45f, 0f);
        var mesh = ShallowRoof(n);
        var (zMin, zMax) = Extent(mesh, Vector3.UnitZ);

        var planar = AdaptiveLayerHeights.ComputeZPositions(
            [mesh], zMin, zMax, FirstH, MinH, LayerH, qualityFactor: 0.5f);
        var alongZ = AdaptiveLayerHeights.ComputePositions(
            [mesh], Vector3.UnitZ, zMin, zMax, FirstH, MinH, LayerH, qualityFactor: 0.5f);

        Assert.Equal(planar.Length, alongZ.Length);
        for (int i = 0; i < planar.Length; i++)
            Assert.Equal(planar[i], alongZ[i], precision: 4);
    }

    [Fact]
    public void Shallow_roof_tightens_along_the_tilted_normal_but_not_in_world_Z()
    {
        var n = TiltOptimizer.TiltToDirection(-45f, 0f);
        var mesh = ShallowRoof(n);
        var (tMin, tMax) = Extent(mesh, n);
        var (zMin, zMax) = Extent(mesh, Vector3.UnitZ);

        // Quality 1 only thins a face that is genuinely shallow to the axis.
        // This roof is ~8° off the slice plane and ~45° off horizontal.
        var along = AdaptiveLayerHeights.ComputePositions(
            [mesh], n, tMin, tMax, FirstH, MinH, LayerH, qualityFactor: 1f);
        var world = AdaptiveLayerHeights.ComputeZPositions(
            [mesh], zMin, zMax, FirstH, MinH, LayerH, qualityFactor: 1f);

        float alongMean = MeanGap(along, tMin);
        float worldMean = MeanGap(world, zMin);
        Assert.InRange(alongMean, MinH, 3.0f);
        Assert.True(worldMean > 3.8f, $"world-Z mean gap {worldMean:0.###} should stay at nominal {LayerH}");
        Assert.True(along.Length > world.Length,
            $"along-normal layers {along.Length} should outnumber world-Z layers {world.Length}");
    }

    [Fact]
    public void Angled_slice_spaces_planes_along_the_normal_and_scales_flow()
    {
        var n = TiltOptimizer.TiltToDirection(-45f, 0f);
        var mesh = ShallowRoof(n);
        var adaptive = AngledPlanarSlicer.Slice([mesh], Settings(adaptive: true));
        var uniform  = AngledPlanarSlicer.Slice([mesh], Settings(adaptive: false));

        Assert.True(adaptive.Layers.Count > 4, $"adaptive layers {adaptive.Layers.Count}");
        Assert.True(adaptive.Layers.Count > uniform.Layers.Count,
            $"adaptive {adaptive.Layers.Count} vs uniform {uniform.Layers.Count}");

        Assert.Equal(FirstH, adaptive.Layers[0].Height, precision: 2);
        Assert.All(adaptive.Layers, layer => Assert.InRange(layer.Height, MinH, LayerH + 0.05f));
        Assert.Contains(adaptive.Layers, layer => layer.Height < 3.0f);

        foreach (var layer in adaptive.Layers)
        {
            float expect = LayerHeightFlowPostProcessor.ScaleFor(layer.Height, LayerH);
            foreach (var move in layer.Moves)
            {
                if (move.Kind != MoveKind.Extrude) continue;
                Assert.Equal(expect, move.HeightScale, precision: 3);
            }
            Assert.True(PlaneSpan(layer, n) < 0.75f,
                $"layer {layer.Index} left the tilted plane by {PlaneSpan(layer, n):0.###} mm");
        }

        Assert.Contains(adaptive.Layers, layer =>
            layer.Moves.Any(m => m.Kind == MoveKind.Extrude && m.HeightScale < 0.8f));

        Assert.Equal(FirstH, uniform.Layers[0].Height, precision: 2);
        for (int i = 1; i < uniform.Layers.Count; i++)
            Assert.Equal(LayerH, uniform.Layers[i].Height, precision: 2);
        Assert.All(uniform.Layers, layer =>
            Assert.Equal(1f, layer.Moves[0].HeightScale, precision: 3));
    }

    private static SliceSettings Settings(bool adaptive) => new()
    {
        LayerHeight = LayerH,
        FirstLayerHeight = FirstH,
        MinLayerHeight = MinH,
        AdaptiveLayerHeight = adaptive,
        AdaptiveQuality = 1f,
        BeadWidth = 8f,
        TiltAngleX = -45f,
        TiltAngle = 0f,
        InfillPattern = InfillPattern.None,
    };

    /// <summary>Mean gap after the first layer, which is fixed at <see cref="FirstH"/>.</summary>
    private static float MeanGap(float[] positions, float start)
    {
        Assert.True(positions.Length >= 3, $"positions {positions.Length}");
        double sum = 0;
        int n = 0;
        float prev = positions[0];
        Assert.Equal(start + FirstH, positions[0], precision: 3);
        for (int i = 1; i < positions.Length; i++)
        {
            float gap = positions[i] - prev;
            Assert.InRange(gap, MinH - 0.01f, LayerH + 0.05f);
            sum += gap;
            n++;
            prev = positions[i];
        }
        return (float)(sum / n);
    }

    private static float PlaneSpan(ToolpathLayer layer, Vector3 n)
    {
        // The layer-to-layer stitch climbs the normal on purpose. The contour does not.
        float lo = float.MaxValue, hi = float.MinValue;
        int kept = 0;
        foreach (var m in layer.Moves)
        {
            if (m.Kind != MoveKind.Extrude) continue;
            float a = Vector3.Dot(m.From, n);
            float b = Vector3.Dot(m.To, n);
            if (MathF.Abs(a - b) > 0.5f) continue;
            lo = MathF.Min(lo, MathF.Min(a, b));
            hi = MathF.Max(hi, MathF.Max(a, b));
            kept++;
        }
        Assert.True(kept > 0, $"layer {layer.Index} has no in-plane extrude");
        return hi - lo;
    }

    private static (float min, float max) Extent(Vector3[] verts, Vector3 axis)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var v in verts)
        {
            float t = Vector3.Dot(v, axis);
            if (t < lo) lo = t;
            if (t > hi) hi = t;
        }
        return (lo, hi);
    }

    /// <summary>
    /// Closed wedge whose roof rises ~8° off the slice plane. Placed far from the
    /// origin so a world-Z spacing bug cannot hide in a part that straddles zero.
    /// </summary>
    private static Vector3[] ShallowRoof(Vector3 n)
    {
        var u = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, n));
        var v = Vector3.Cross(n, u);
        var o = new Vector3(1500f, 200f, 50f);
        const float L = 400f, W = 80f, rise = 56.2f, baseT = 2f;
        Vector3 P(float uu, float vv, float tt) => o + u * uu + v * vv + n * tt;

        var a = P(0, 0, 0);
        var b = P(L, 0, 0);
        var c = P(L, W, 0);
        var d = P(0, W, 0);
        var e = P(0, 0, baseT);
        var f = P(L, 0, baseT + rise);
        var g = P(L, W, baseT + rise);
        var h = P(0, W, baseT);

        var tris = new List<Vector3>();
        void Quad(Vector3 p, Vector3 q, Vector3 r, Vector3 s)
        {
            tris.Add(p); tris.Add(q); tris.Add(r);
            tris.Add(p); tris.Add(r); tris.Add(s);
        }
        Quad(a, b, c, d);
        Quad(e, h, g, f);
        Quad(a, e, f, b);
        Quad(b, f, g, c);
        Quad(c, g, h, d);
        Quad(d, h, e, a);
        return [.. tris];
    }
}
