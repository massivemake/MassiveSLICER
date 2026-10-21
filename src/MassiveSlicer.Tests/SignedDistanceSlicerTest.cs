using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Signed-distance shells: interior is negative, the outward gradient leaves the solid,
/// iso-surfaces point outward, and a box slice puts bead centers one half-bead in from
/// the wall with the nozzle aimed out of that wall.
/// </summary>
public sealed class SignedDistanceSlicerTest
{
    static Vector3[] BoxSoup(float sx, float sy, float sz, bool outward = true)
    {
        var p = new Vector3[8]
        {
            new(0, 0, 0), new(sx, 0, 0), new(sx, sy, 0), new(0, sy, 0),
            new(0, 0, sz), new(sx, 0, sz), new(sx, sy, sz), new(0, sy, sz),
        };
        int[][] quads =
        [
            [3, 2, 1, 0],
            [4, 5, 6, 7],
            [0, 1, 5, 4],
            [1, 2, 6, 5],
            [2, 3, 7, 6],
            [3, 0, 4, 7],
        ];
        var soup = new List<Vector3>(quads.Length * 6);
        foreach (var q in quads)
        {
            if (outward)
            {
                soup.Add(p[q[0]]); soup.Add(p[q[1]]); soup.Add(p[q[2]]);
                soup.Add(p[q[0]]); soup.Add(p[q[2]]); soup.Add(p[q[3]]);
            }
            else
            {
                soup.Add(p[q[0]]); soup.Add(p[q[2]]); soup.Add(p[q[1]]);
                soup.Add(p[q[0]]); soup.Add(p[q[3]]); soup.Add(p[q[2]]);
            }
        }
        return [.. soup];
    }

    static Vector3[] UvSphere(float radius, int slices, int stacks)
    {
        var rings = new Vector3[stacks + 1][];
        for (int s = 0; s <= stacks; s++)
        {
            float v = s / (float)stacks;
            float phi = v * MathF.PI;
            rings[s] = new Vector3[slices];
            for (int i = 0; i < slices; i++)
            {
                float u = i / (float)slices;
                float theta = u * MathF.Tau;
                rings[s][i] = radius * new Vector3(
                    MathF.Sin(phi) * MathF.Cos(theta),
                    MathF.Sin(phi) * MathF.Sin(theta),
                    MathF.Cos(phi));
            }
        }

        var soup = new List<Vector3>();
        for (int s = 0; s < stacks; s++)
        for (int i = 0; i < slices; i++)
        {
            int n = (i + 1) % slices;
            var a = rings[s][i];
            var b = rings[s][n];
            var c = rings[s + 1][n];
            var d = rings[s + 1][i];
            soup.Add(a); soup.Add(c); soup.Add(b);
            soup.Add(a); soup.Add(d); soup.Add(c);
        }
        return [.. soup];
    }

    [Fact]
    public void Sdf_Box_InteriorNegative_OutsidePositive_GradientOutward()
    {
        var sdf = new MeshSignedDistance([BoxSoup(10, 10, 10)]);

        var inside = sdf.Sample(new Vector3(5, 5, 5));
        Assert.InRange(inside.Distance, -5.05f, -4.95f);
        Assert.InRange(inside.Outward.Length(), 0.99f, 1.01f);

        var above = sdf.Sample(new Vector3(5, 5, 12));
        Assert.InRange(above.Distance, 1.9f, 2.1f);
        Assert.True(above.Outward.Z > 0.9f, $"above gradient {above.Outward}");

        var nearPlusX = sdf.Sample(new Vector3(9, 5, 5));
        Assert.InRange(nearPlusX.Distance, -1.05f, -0.95f);
        Assert.True(nearPlusX.Outward.X > 0.9f, $"+X face gradient {nearPlusX.Outward}");

        var onTop = sdf.Sample(new Vector3(5, 5, 10));
        Assert.InRange(onTop.Distance, -0.05f, 0.05f);
    }

    [Fact]
    public void Sdf_InvertedBox_StillNegativeInside()
    {
        var sdf = new MeshSignedDistance([BoxSoup(10, 10, 10, outward: false)]);
        Assert.True(sdf.Sample(new Vector3(5, 5, 5)).Distance < -4f);
        var above = sdf.Sample(new Vector3(5, 5, 12));
        Assert.True(above.Distance > 1.5f);
        Assert.True(above.Outward.Z > 0.9f, $"inverted above gradient {above.Outward}");
    }

    [Fact]
    public void Sdf_Sphere_SignFollowsRadius()
    {
        var sdf = new MeshSignedDistance([UvSphere(10f, 16, 12)]);
        Assert.True(sdf.Sample(Vector3.Zero).Distance < -5f);
        var outside = sdf.Sample(new Vector3(15, 0, 0));
        Assert.True(outside.Distance > 2f, $"outside distance {outside.Distance}");
        Assert.True(outside.Outward.X > 0.7f, $"sphere gradient {outside.Outward}");
    }

    [Fact]
    public void ToolNormal_FoldsDownwardIntoUpperHemisphere()
    {
        var down = SignedDistanceSlicer.ToolNormal(-Vector3.UnitZ);
        Assert.True(down.Z > 0.99f);

        var side = SignedDistanceSlicer.ToolNormal(Vector3.UnitX);
        Assert.True(side.X > 0.99f);
        Assert.True(MathF.Abs(side.Z) < 1e-4f);

        var slope = SignedDistanceSlicer.ToolNormal(Vector3.Normalize(new Vector3(1, 0, -1)));
        Assert.True(slope.Z > 0.6f);
        Assert.True(slope.X > 0.6f);
    }

    [Fact]
    public void IsoSurface_Sphere_LiesOnRadius_AndFacesOut()
    {
        const int n = 28;
        const float radius = 8f;
        float pitch = 1f;
        var origin = new Vector3(-14, -14, -14);
        var field = new float[n * n * n];
        for (int k = 0; k < n; k++)
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            var p = origin + new Vector3(i, j, k) * pitch;
            field[i + n * (j + n * k)] = p.Length() - radius;
        }

        var soup = IsoSurface.Extract(field, n, n, n, origin, new Vector3(pitch), 0f);
        Assert.True(soup.Length >= 36);

        int outward = 0, inward = 0;
        var edges = new Dictionary<(int, int, int, int, int, int), int>();
        for (int t = 0; t + 2 < soup.Length; t += 3)
        {
            var a = soup[t]; var b = soup[t + 1]; var c = soup[t + 2];
            Assert.InRange(a.Length(), radius - 1.2f, radius + 1.2f);
            var mid = (a + b + c) / 3f;
            var fn = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(fn, mid) > 0f) outward++; else inward++;

            CountEdge(edges, a, b);
            CountEdge(edges, b, c);
            CountEdge(edges, c, a);
        }

        Assert.True(outward > inward * 8, $"out {outward} in {inward}");
        int boundary = 0;
        foreach (var count in edges.Values)
            if (count == 1) boundary++;
        Assert.True(boundary < edges.Count * 0.02, $"boundary edges {boundary} of {edges.Count}");
    }

    static void CountEdge(Dictionary<(int, int, int, int, int, int), int> edges, Vector3 a, Vector3 b)
    {
        var ka = Key(a);
        var kb = Key(b);
        var key = Compare(ka, kb) <= 0 ? (ka.X, ka.Y, ka.Z, kb.X, kb.Y, kb.Z) : (kb.X, kb.Y, kb.Z, ka.X, ka.Y, ka.Z);
        edges.TryGetValue(key, out int n);
        edges[key] = n + 1;
    }

    static (int X, int Y, int Z) Key(Vector3 p) =>
        ((int)MathF.Round(p.X * 100f), (int)MathF.Round(p.Y * 100f), (int)MathF.Round(p.Z * 100f));

    static int Compare((int X, int Y, int Z) a, (int X, int Y, int Z) b)
    {
        int c = a.X.CompareTo(b.X);
        if (c != 0) return c;
        c = a.Y.CompareTo(b.Y);
        return c != 0 ? c : a.Z.CompareTo(b.Z);
    }

    [Fact]
    public void PlanGrid_CoarsensTheLongAxis()
    {
        var (_, _, nx, _, _, coarsened) = SignedDistanceSlicer.PlanGrid(
            Vector3.Zero, new Vector3(400, 20, 20), layerHeight: 1f);
        Assert.True(coarsened);
        Assert.InRange(nx, 3, SignedDistanceSlicer.MaxAxisCells + 2);
    }

    [Fact]
    public void PlanGrid_HalfLayerPitchOnASmallBox()
    {
        var (_, spacing, _, _, _, coarsened) = SignedDistanceSlicer.PlanGrid(
            Vector3.Zero, new Vector3(40, 40, 40), layerHeight: 4f);
        Assert.False(coarsened);
        Assert.InRange(spacing.X, 1.9f, 2.1f);
    }

    [Fact]
    public void Slice_Box_OuterAndInnerShells_NormalsAimOut()
    {
        // 40 mm cube, 4 mm layers, 8 mm bead.
        // Outer bead center is 4 mm in from each face. The next shell is 12 mm in.
        var settings = new SliceSettings
        {
            LayerHeight = 4f,
            FirstLayerHeight = 4f,
            BeadWidth = 8f,
        };

        var tp = SignedDistanceSlicer.Slice([BoxSoup(40, 40, 40)], settings);

        var extrude = tp.Layers
            .SelectMany(l => l.Moves)
            .Where(m => m.Kind == MoveKind.Extrude)
            .ToList();
        Assert.True(extrude.Count > 20, $"extrude count {extrude.Count}");
        Assert.True(tp.Layers.Count > 4, $"layers {tp.Layers.Count}");

        bool OnFace(ToolpathMove m, float x0, float x1, float y0, float y1, float z0, float z1)
        {
            var mid = (m.From + m.To) * 0.5f;
            return mid.X >= x0 && mid.X <= x1 && mid.Y >= y0 && mid.Y <= y1 && mid.Z >= z0 && mid.Z <= z1;
        }

        var outer = extrude.Where(m => OnFace(m, 1, 8, 12, 28, 12, 28)).ToList();
        Assert.NotEmpty(outer);
        Assert.All(outer, m => Assert.True(m.Normal.X < -0.7f, $"outer normal {m.Normal}"));

        var inner = extrude.Where(m => OnFace(m, 9, 16, 16, 24, 16, 24)).ToList();
        Assert.NotEmpty(inner);
        Assert.All(inner, m => Assert.True(m.Normal.X < -0.7f, $"inner normal {m.Normal}"));

        Assert.Contains(extrude, m =>
        {
            var mid = (m.From + m.To) * 0.5f;
            return mid.Z > 30f && m.Normal.Z > 0.7f;
        });
        Assert.All(extrude, m => Assert.True(m.Normal.Z >= -0.05f, $"downward normal {m.Normal}"));
        Assert.All(extrude, m => Assert.True(m.IsWall));
    }

    [Fact]
    public void Slice_EmptyMesh_ReturnsNoLayers()
    {
        var tp = SignedDistanceSlicer.Slice([], new SliceSettings { LayerHeight = 3f, BeadWidth = 6f });
        Assert.Empty(tp.Layers);
    }
}
