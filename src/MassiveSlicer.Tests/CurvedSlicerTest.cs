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