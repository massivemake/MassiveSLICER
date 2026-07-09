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

    private static SliceSettings Settings() => new()
    {
        LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = 6f,
        MultiPlanarBaseDeg = 0f, MultiPlanarMidDeg = 15f, MultiPlanarTopDeg = 30f,
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
    public void ConsecutivePlanesNeverCrossInsideThePart()
    {
        var tp = AngledPlanarSlicer.SliceMultiPlanar([Cylinder()], Settings());
        // If planes crossed, some moves would report (clamped) near-zero thickness.
        int nearZero = tp.Layers.Skip(1).SelectMany(l => l.Moves)
            .Count(m => m.Kind == MoveKind.Extrude && m.HeightScale <= 0.26f);
        Assert.True(nearZero == 0, $"{nearZero} moves at the crossing clamp");
    }
}
