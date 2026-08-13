using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Core.Slicing.Curved;

namespace MassiveSlicer.Tests;

public sealed class CurvedSlicerTest
{
    [Fact]
    public void Dome_AutoDetect_ProducesNonPlanarLayers()
    {
        var mesh = BuildHemisphere(8, 6, radius: 50f);
        var settings = new SliceSettings
        {
            LayerHeight = 5f,
            CurvedBoundarySource = CurvedBoundarySource.AutoDetect,
            CurvedAutoDetectBandMm = 3f,
            CurvedEnableRegionSplit = false,
        };

        var tp = CurvedSlicer.Slice([mesh], settings);
        Assert.True(tp.Layers.Count >= 3, $"Expected multiple layers, got {tp.Layers.Count}");

        float zMin = float.MaxValue, zMax = float.MinValue;
        foreach (var layer in tp.Layers)
        {
            foreach (var move in layer.Moves)
            {
                if (move.Kind != MoveKind.Extrude) continue;
                if (move.From.Z < zMin) zMin = move.From.Z;
                if (move.From.Z > zMax) zMax = move.From.Z;
            }
        }
        float globalRange = zMax - zMin;
        Assert.True(globalRange > 10f);

        // Layers should stack upward (average Z increases across layers).
        float prevAvgZ = float.MinValue;
        int risingLayers = 0;
        foreach (var layer in tp.Layers)
        {
            float sumZ = 0f; int n = 0;
            foreach (var move in layer.Moves)
            {
                if (move.Kind != MoveKind.Extrude) continue;
                sumZ += move.From.Z; n++;
            }
            if (n == 0) continue;
            float avgZ = sumZ / n;
            if (avgZ > prevAvgZ + 0.5f) risingLayers++;
            prevAvgZ = avgZ;
        }
        Assert.True(risingLayers >= 2, "Curved layers should progress from LOW toward HIGH boundary");
    }

    [Fact]
    public void InterpolationField_ZeroAtMidpoint()
    {
        var mesh = BuildOpenStrip();
        var welded = MeshGraph.Build([mesh]);
        var low  = new BoundaryTarget(welded, [0, 1]);
        var high = new BoundaryTarget(welded, [welded.VertexCount - 2, welded.VertexCount - 1]);

        var field = InterpolationField.Compute(0.5f, low, high);
        int mid = welded.VertexCount / 2;
        Assert.True(MathF.Abs(field[mid]) < 5f);
    }

    [Fact]
    public void BoundaryTarget_MultiCluster_UnionMin()
    {
        var mesh = BuildOpenStrip();
        var welded = MeshGraph.Build([mesh]);
        var target = new BoundaryTarget(welded, [0, welded.VertexCount - 1]);
        Assert.Equal(0f, target.GetDistance(0), 3);
        Assert.Equal(0f, target.GetDistance(welded.VertexCount - 1), 3);
    }

    [Fact]
    public void JsonImport_RoundTrip_PreservesIndices()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mslicer_boundary_{Guid.NewGuid():N}.json");
        try
        {
            var indices = new[] { 1, 4, 9, 12 };
            BoundaryJsonIO.SaveIndices(indices, path);
            var loaded = BoundaryJsonIO.LoadIndices(path);
            Assert.Equal(indices, loaded);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void InterpolationSchedule_EndsNearOne()
    {
        var tList = InterpolationSchedule.GetInterpolationParameters(5);
        Assert.True(tList.Count >= 5);
        Assert.True(tList[^1] > 0.99f && tList[^1] < 1f);
    }

    [Fact]
    public void CurvedZigZag_OpenFacesReverseEachLayer()
    {
        // Tall thin wall: LOW/HIGH auto-detect on bottom/top → closed isocurves become
        // single-skin open faces under zig-zag, reversing each layer.
        var mesh = BuildThinWallBox(len: 200f, thick: 18f, h: 80f);
        var settings = new SliceSettings
        {
            LayerHeight = 5f,
            BeadWidth = 6f,
            CurvedBoundarySource = CurvedBoundarySource.AutoDetect,
            CurvedAutoDetectBandMm = 3f,
            CurvedEnableRegionSplit = false,
            ZigZagSeam = true,
            ZigZagAllowSameLayerTravel = true,
        };

        var tp = CurvedSlicer.Slice([mesh], settings);
        Assert.True(tp.Layers.Count >= 4, $"expected several layers, got {tp.Layers.Count}");

        static Vector2 Dir(ToolpathLayer lyr)
        {
            foreach (var m in lyr.Moves)
            {
                if (m.Kind != MoveKind.Extrude || m.IsLayerChange || m.IsLayerStitch) continue;
                var d = new Vector2(m.To.X - m.From.X, m.To.Y - m.From.Y);
                if (d.LengthSquared() > 1e-4f) return Vector2.Normalize(d);
            }
            return Vector2.Zero;
        }

        var d0 = Dir(tp.Layers[0]);
        var d1 = Dir(tp.Layers[1]);
        Assert.True(d0.LengthSquared() > 0.5f && d1.LengthSquared() > 0.5f,
            $"missing extrude dirs d0={d0} d1={d1}");
        float dot = Vector2.Dot(d0, d1);
        Assert.True(dot < -0.25f,
            $"expected zig-zag reverse on curved isocurves, d0={d0} d1={d1} dot={dot:0.###}");
    }

    private static Vector3[] BuildThinWallBox(float len, float thick, float h)
    {
        float hx = len * 0.5f, hy = thick * 0.5f;
        var v = new Vector3[]
        {
            new(-hx, -hy, 0), new(hx, -hy, 0), new(hx, hy, 0), new(-hx, hy, 0),
            new(-hx, -hy, h), new(hx, -hy, h), new(hx, hy, h), new(-hx, hy, h),
        };
        int[][] faces =
        [
            [0,1,2],[0,2,3], [4,6,5],[4,7,6],
            [0,4,5],[0,5,1], [1,5,6],[1,6,2],
            [2,6,7],[2,7,3], [3,7,4],[3,4,0],
        ];
        var tris = new List<Vector3>();
        foreach (var f in faces)
            tris.AddRange([v[f[0]], v[f[1]], v[f[2]]]);
        return [.. tris];
    }

    private static Vector3[] BuildHemisphere(int slices, int stacks, float radius)
    {
        var verts = new List<Vector3>();
        for (int i = 0; i <= stacks; i++)
        {
            float v = MathF.PI * 0.5f * i / stacks;
            float z = radius * MathF.Sin(v);
            float r = radius * MathF.Cos(v);
            for (int j = 0; j < slices; j++)
            {
                float u = 2f * MathF.PI * j / slices;
                verts.Add(new Vector3(r * MathF.Cos(u), r * MathF.Sin(u), z));
            }
        }

        var tris = new List<Vector3>();
        for (int i = 0; i < stacks; i++)
        {
            for (int j = 0; j < slices; j++)
            {
                int i0 = i * slices + j;
                int i1 = i * slices + (j + 1) % slices;
                int i2 = (i + 1) * slices + j;
                int i3 = (i + 1) * slices + (j + 1) % slices;
                tris.Add(verts[i0]); tris.Add(verts[i1]); tris.Add(verts[i2]);
                tris.Add(verts[i1]); tris.Add(verts[i3]); tris.Add(verts[i2]);
            }
        }
        return tris.ToArray();
    }

    private static Vector3[] BuildOpenStrip()
    {
        var verts = new List<Vector3>();
        for (int i = 0; i < 10; i++)
            verts.Add(new Vector3(i * 10f, 0f, 0f));
        for (int i = 0; i < 9; i++)
        {
            var a = verts[i]; var b = verts[i + 1];
            verts.Add(a + new Vector3(0, 10, 5));
            verts.Add(b + new Vector3(0, 10, 5));
        }
        var soup = new List<Vector3>();
        for (int i = 0; i < 9; i++)
        {
            var bl = verts[i]; var br = verts[i + 1];
            var tl = verts[10 + i * 2]; var tr = verts[10 + i * 2 + 1];
            soup.Add(bl); soup.Add(br); soup.Add(tl);
            soup.Add(br); soup.Add(tr); soup.Add(tl);
        }
        return soup.ToArray();
    }
}