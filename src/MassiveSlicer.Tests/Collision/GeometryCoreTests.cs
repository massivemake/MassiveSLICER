using System.Numerics;
using MassiveSlicer.Core.Collision;
using Xunit;

namespace MassiveSlicer.Tests.Collision;

public sealed class ConvexHullTest
{
    static Vector3[] CubeCloud(float size, int interiorPoints)
    {
        var rng = new Random(42);
        var pts = new List<Vector3>();
        // 8 corners
        for (int i = 0; i < 8; i++)
            pts.Add(new Vector3(
                (i & 1) == 0 ? 0 : size,
                (i & 2) == 0 ? 0 : size,
                (i & 4) == 0 ? 0 : size));
        // interior noise — must never appear in the hull
        for (int i = 0; i < interiorPoints; i++)
            pts.Add(new Vector3(
                size * (0.1f + 0.8f * (float)rng.NextDouble()),
                size * (0.1f + 0.8f * (float)rng.NextDouble()),
                size * (0.1f + 0.8f * (float)rng.NextDouble())));
        return [.. pts];
    }

    [Fact]
    public void Cube_HullIsEightCorners()
    {
        var hull = Quickhull.Build(CubeCloud(100f, 500));
        Assert.Equal(8, hull.Vertices.Length);
        foreach (var v in hull.Vertices)
        {
            Assert.True(MathF.Abs(v.X) < 1e-3f || MathF.Abs(v.X - 100f) < 1e-3f);
            Assert.True(MathF.Abs(v.Y) < 1e-3f || MathF.Abs(v.Y - 100f) < 1e-3f);
            Assert.True(MathF.Abs(v.Z) < 1e-3f || MathF.Abs(v.Z - 100f) < 1e-3f);
        }
    }

    [Fact]
    public void CoplanarInput_FallsBackSafely()
    {
        // All points on z=0 — degenerate for 3D quickhull; must not throw and must
        // preserve the extremes (support-set correctness).
        var pts = new List<Vector3>();
        for (int x = 0; x <= 10; x++)
            for (int y = 0; y <= 10; y++)
                pts.Add(new Vector3(x * 10f, y * 10f, 0f));
        var hull = Quickhull.Build(pts);
        Assert.True(hull.Vertices.Length >= 4);
        Assert.Equal(0f, hull.LocalBounds.Min.X, 3);
        Assert.Equal(100f, hull.LocalBounds.Max.X, 3);
        Assert.Equal(100f, hull.LocalBounds.Max.Y, 3);
    }

    [Fact]
    public void VoxelDecimate_KeepsAxisExtremes()
    {
        var pts = CubeCloud(100f, 2000);
        var dec = Quickhull.VoxelDecimate(pts, 25f);
        Assert.True(dec.Length < pts.Length);
        var b = Aabb.FromPoints(dec);
        Assert.Equal(0f, b.Min.X, 3);
        Assert.Equal(100f, b.Max.X, 3);
        Assert.Equal(0f, b.Min.Z, 3);
        Assert.Equal(100f, b.Max.Z, 3);
    }

    [Fact]
    public void Support_ReturnsExtremeVertex()
    {
        var hull = Quickhull.Build(CubeCloud(100f, 100));
        var s = hull.Support(new Vector3(1f, 1f, 1f));
        Assert.Equal(new Vector3(100f, 100f, 100f), s);
    }
}

public sealed class GjkTest
{
    static TransformedHull Box(Vector3 center, float half)
    {
        var pts = new Vector3[8];
        for (int i = 0; i < 8; i++)
            pts[i] = new Vector3(
                (i & 1) == 0 ? -half : half,
                (i & 2) == 0 ? -half : half,
                (i & 4) == 0 ? -half : half);
        var hull = Quickhull.Build(pts);
        return new TransformedHull(hull, Matrix4x4.CreateTranslation(center));
    }

    [Fact]
    public void SeparatedBoxes_ExactDistance()
    {
        // Unit-half boxes 10 apart on X → gap = 10 - 1 - 1 = 8.
        var a = Box(Vector3.Zero, 1f);
        var b = Box(new Vector3(10f, 0f, 0f), 1f);
        Assert.Equal(8f, Gjk.Distance(a, b), 2);
    }

    [Fact]
    public void TouchingBoxes_DistanceNearZero()
    {
        var a = Box(Vector3.Zero, 1f);
        var b = Box(new Vector3(2f, 0f, 0f), 1f);
        Assert.True(Gjk.Distance(a, b) < 0.01f);
    }

    [Fact]
    public void PenetratingBoxes_DistanceZero()
    {
        var a = Box(Vector3.Zero, 1f);
        var b = Box(new Vector3(1f, 0.3f, -0.2f), 1f);
        Assert.Equal(0f, Gjk.Distance(a, b));
    }

    [Fact]
    public void DiagonalSeparation_ExactDistance()
    {
        // Corner-to-corner: boxes offset (5,5,5); nearest corners (1,1,1)/(4,4,4)
        // → distance = 3*sqrt(3).
        var a = Box(Vector3.Zero, 1f);
        var b = Box(new Vector3(5f, 5f, 5f), 1f);
        Assert.Equal(3f * MathF.Sqrt(3f), Gjk.Distance(a, b), 2);
    }

    [Fact]
    public void HullVsTriangle_Distance()
    {
        var box = Box(Vector3.Zero, 1f);
        var tri = new TriangleSupport(
            new Vector3(4f, -5f, -5f), new Vector3(4f, 5f, -5f), new Vector3(4f, 0f, 5f));
        Assert.Equal(3f, Gjk.Distance(box, tri), 2);
    }

    [Fact]
    public void HullVsObb_MarginThreshold()
    {
        var box = Box(Vector3.Zero, 1f);
        var obb = new ObbSupport(
            new Vector3(4f, 0f, 0f),
            Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ,
            new Vector3(1f, 1f, 1f)); // gap = 4 - 1 - 1 = 2
        Assert.True(Gjk.Hit(box, obb, 2.5f));
        Assert.False(Gjk.Hit(box, obb, 1.5f));
    }

    [Fact]
    public void RotatedObb_SupportsCorrect()
    {
        // OBB rotated 45° about Z: its reach along X grows to half*sqrt(2).
        float c = MathF.Cos(MathF.PI / 4f), s = MathF.Sin(MathF.PI / 4f);
        var obb = new ObbSupport(
            new Vector3(5f, 0f, 0f),
            new Vector3(c, s, 0f), new Vector3(-s, c, 0f), Vector3.UnitZ,
            new Vector3(1f, 1f, 1f));
        var a = Box(Vector3.Zero, 1f);
        // gap = 5 - 1 - sqrt(2) ≈ 2.586
        Assert.Equal(5f - 1f - MathF.Sqrt(2f), Gjk.Distance(a, obb), 2);
    }
}

public sealed class BvhTest
{
    [Fact]
    public void Query_MatchesBruteForce()
    {
        var rng = new Random(7);
        var leaves = new Aabb[500];
        for (int i = 0; i < leaves.Length; i++)
        {
            var min = new Vector3(
                (float)rng.NextDouble() * 1000f,
                (float)rng.NextDouble() * 1000f,
                (float)rng.NextDouble() * 500f);
            leaves[i] = new Aabb(min, min + new Vector3(
                5f + (float)rng.NextDouble() * 50f,
                5f + (float)rng.NextDouble() * 50f,
                5f + (float)rng.NextDouble() * 20f));
        }
        var bvh = new Bvh(leaves);

        for (int q = 0; q < 50; q++)
        {
            var qmin = new Vector3(
                (float)rng.NextDouble() * 1000f,
                (float)rng.NextDouble() * 1000f,
                (float)rng.NextDouble() * 500f);
            var box = new Aabb(qmin, qmin + new Vector3(80f, 80f, 40f));

            var hits = new List<int>();
            bvh.Query(box, hits);
            var expected = new List<int>();
            for (int i = 0; i < leaves.Length; i++)
                if (leaves[i].Overlaps(box)) expected.Add(i);

            hits.Sort();
            expected.Sort();
            Assert.Equal(expected, hits);
        }
    }

    [Fact]
    public void EmptyBvh_QueryNoThrow()
    {
        var bvh = new Bvh([]);
        var hits = new List<int>();
        bvh.Query(new Aabb(Vector3.Zero, Vector3.One), hits);
        Assert.Empty(hits);
    }
}
