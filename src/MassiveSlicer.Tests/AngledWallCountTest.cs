using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Angled + Normal grows concentric shells. One wall is today's single perimeter.
/// Each added wall steps one bead into the material and stays on the tilted plane.
/// </summary>
public class AngledWallCountTest
{
    const float Bead = 8f;

    [Fact]
    public void One_wall_is_a_single_loop()
    {
        var tp = AngledPlanarSlicer.Slice([Box()], Settings(walls: 1, tiltX: -30f));
        var mid = tp.Layers[tp.Layers.Count / 2];
        Assert.Equal(1, Loops(mid).Count);
    }

    [Fact]
    public void Extra_walls_inset_by_one_bead_and_stay_on_the_tilted_plane()
    {
        var n = TiltOptimizer.TiltToDirection(-30f, 0f);
        var tp = AngledPlanarSlicer.Slice([Box()], Settings(walls: 3, tiltX: -30f));
        var mid = tp.Layers[tp.Layers.Count / 2];
        var loops = Loops(mid);
        Assert.Equal(3, loops.Count);

        var radii = loops.Select(loop => MeanRadius(loop, n)).OrderByDescending(r => r).ToArray();
        Assert.InRange(radii[0] - radii[1], Bead * 0.6f, Bead * 1.8f);
        Assert.InRange(radii[1] - radii[2], Bead * 0.6f, Bead * 1.8f);

        foreach (var loop in loops)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var p in loop)
            {
                float d = Vector3.Dot(p, n);
                lo = MathF.Min(lo, d);
                hi = MathF.Max(hi, d);
            }
            Assert.True(hi - lo < 0.75f, $"loop left the plane by {hi - lo:0.###} mm");
        }
    }

    [Fact]
    public void A_hole_grows_a_wall_into_the_material()
    {
        var n = TiltOptimizer.TiltToDirection(-30f, 0f);
        var oneTp = AngledPlanarSlicer.Slice([Tube()], Settings(walls: 1, tiltX: -30f));
        var twoTp = AngledPlanarSlicer.Slice([Tube()], Settings(walls: 2, tiltX: -30f));
        int mid = oneTp.Layers.Count / 2;
        var one = Loops(oneTp.Layers[mid]);
        var two = Loops(twoTp.Layers[mid]);
        Assert.Equal(2, one.Count);
        Assert.Equal(4, two.Count);

        var radii = two.Select(loop => MeanRadius(loop, n)).OrderByDescending(r => r).ToArray();
        // Outer pair shrinks inward. Hole pair: the added wall sits outside the
        // hole centerline, so the two smaller radii still differ by about a bead
        // and the smallest stays near the original hole wall.
        Assert.InRange(radii[0] - radii[1], Bead * 0.6f, Bead * 1.8f);
        Assert.InRange(radii[2] - radii[3], Bead * 0.6f, Bead * 1.8f);
        float hole0 = one.Select(loop => MeanRadius(loop, n)).Min();
        Assert.InRange(radii[3], hole0 - 2f, hole0 + 2f);
    }

    [Fact]
    public void Surface_planar_and_multiplanar_ignore_wall_count()
    {
        var angledSurface = AngledPlanarSlicer.Slice([Box()], Settings(walls: 4, tiltX: -30f, surface: true));
        var angledOne = AngledPlanarSlicer.Slice([Box()], Settings(walls: 1, tiltX: -30f, surface: true));
        Assert.Equal(LoopCount(angledOne), LoopCount(angledSurface));

        var planar = PlanarSlicer.Slice([Box()], Settings(walls: 4, tiltX: 0f));
        Assert.Equal(1, Loops(planar.Layers[planar.Layers.Count / 2]).Count);

        var multi = AngledPlanarSlicer.SliceMultiPlanar([Box()], Settings(walls: 4, tiltX: 0f));
        Assert.True(multi.Layers.Count > 0);
        Assert.Equal(1, Loops(multi.Layers[multi.Layers.Count / 2]).Count);
    }

    [Fact]
    public void Infill_prints_inside_the_inner_wall()
    {
        var wallsOnly = AngledPlanarSlicer.Slice([Box()], Settings(walls: 2, tiltX: -30f));
        var withFill = AngledPlanarSlicer.Slice([Box()], Settings(walls: 2, tiltX: -30f, infill: true));
        float wallsLen = ExtrudeLength(wallsOnly);
        float fillLen = ExtrudeLength(withFill);
        Assert.True(fillLen > wallsLen * 1.2f, $"infill {fillLen:0} mm vs walls {wallsLen:0} mm");
        Assert.Equal(2, Loops(withFill.Layers[withFill.Layers.Count / 2]).Count);
    }

    private static int LoopCount(Toolpath tp)
        => tp.Layers.Count == 0 ? 0 : Loops(tp.Layers[tp.Layers.Count / 2]).Count;

    private static float ExtrudeLength(Toolpath tp)
        => tp.Layers.Sum(l => l.Moves.Where(m => m.Kind == MoveKind.Extrude)
            .Sum(m => Vector3.Distance(m.From, m.To)));

    private static SliceSettings Settings(int walls, float tiltX, bool surface = false, bool infill = false) => new()
    {
        LayerHeight = 4f,
        FirstLayerHeight = 4f,
        BeadWidth = Bead,
        WallCount = walls,
        TiltAngleX = tiltX,
        SlicingMode = surface ? SlicingMode.Surface : SlicingMode.Normal,
        InfillPattern = infill ? InfillPattern.Rectilinear : InfillPattern.None,
        InfillSpacingMm = 16f,
    };

    private static List<List<Vector3>> Loops(ToolpathLayer layer)
    {
        var loops = new List<List<Vector3>>();
        List<Vector3>? cur = null;
        foreach (var m in layer.Moves)
        {
            // The layer-change stitch is an extrude that climbs to the previous
            // plane. It is not a wall.
            if (m.IsLayerStitch) continue;
            if (m.Kind != MoveKind.Extrude)
            {
                if (cur is { Count: > 0 }) loops.Add(cur);
                cur = null;
                continue;
            }
            cur ??= [m.From];
            cur.Add(m.To);
        }
        if (cur is { Count: > 0 }) loops.Add(cur);
        return loops;
    }

    private static float MeanRadius(List<Vector3> loop, Vector3 n)
    {
        var u = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, n));
        var v = Vector3.Cross(n, u);
        var local = loop.Select(p => new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v))).ToList();
        var c = new Vector2(local.Average(p => p.X), local.Average(p => p.Y));
        return local.Average(p => Vector2.Distance(p, c));
    }

    private static Vector3[] Box() => Prism(80f, 80f, 32f);

    /// <summary>
    /// Closed tube, outer radius 60, hole radius 16, tall enough that a mid
    /// layer cuts walls only. Two bead walls fit in the 44 mm of material.
    /// </summary>
    private static Vector3[] Tube()
    {
        var tris = new List<Vector3>();
        const float rOut = 60f, rIn = 16f, h = 80f;
        const int seg = 48;
        void Skin(float r, bool outward)
        {
            for (int i = 0; i < seg; i++)
            {
                float a0 = MathF.Tau * i / seg, a1 = MathF.Tau * (i + 1) / seg;
                var b0 = new Vector3(r * MathF.Cos(a0), r * MathF.Sin(a0), 0);
                var b1 = new Vector3(r * MathF.Cos(a1), r * MathF.Sin(a1), 0);
                var t0 = b0 with { Z = h };
                var t1 = b1 with { Z = h };
                if (outward) { tris.AddRange([b0, b1, t1]); tris.AddRange([b0, t1, t0]); }
                else { tris.AddRange([b0, t1, b1]); tris.AddRange([b0, t0, t1]); }
            }
        }
        void Cap(float z, bool up)
        {
            for (int i = 0; i < seg; i++)
            {
                float a0 = MathF.Tau * i / seg, a1 = MathF.Tau * (i + 1) / seg;
                var i0 = new Vector3(rIn * MathF.Cos(a0), rIn * MathF.Sin(a0), z);
                var i1 = new Vector3(rIn * MathF.Cos(a1), rIn * MathF.Sin(a1), z);
                var o0 = new Vector3(rOut * MathF.Cos(a0), rOut * MathF.Sin(a0), z);
                var o1 = new Vector3(rOut * MathF.Cos(a1), rOut * MathF.Sin(a1), z);
                if (up) { tris.AddRange([i0, i1, o1]); tris.AddRange([i0, o1, o0]); }
                else { tris.AddRange([i0, o1, i1]); tris.AddRange([i0, o0, o1]); }
            }
        }
        Skin(rOut, outward: true);
        Skin(rIn, outward: false);
        Cap(0f, up: false);
        Cap(h, up: true);
        return [.. tris];
    }

    private static Vector3[] Prism(float sx, float sy, float sz)
    {
        var v = new List<Vector3>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            v.Add(a); v.Add(b); v.Add(c);
            v.Add(a); v.Add(c); v.Add(d);
        }
        float hx = sx * 0.5f, hy = sy * 0.5f;
        Quad(new(-hx, -hy, 0), new(hx, -hy, 0), new(hx, hy, 0), new(-hx, hy, 0));
        Quad(new(-hx, -hy, sz), new(-hx, hy, sz), new(hx, hy, sz), new(hx, -hy, sz));
        Quad(new(-hx, -hy, 0), new(-hx, hy, 0), new(-hx, hy, sz), new(-hx, -hy, sz));
        Quad(new(hx, -hy, 0), new(hx, -hy, sz), new(hx, hy, sz), new(hx, hy, 0));
        Quad(new(-hx, -hy, 0), new(-hx, -hy, sz), new(hx, -hy, sz), new(hx, -hy, 0));
        Quad(new(-hx, hy, 0), new(hx, hy, 0), new(hx, hy, sz), new(-hx, hy, sz));
        return [.. v];
    }
}
