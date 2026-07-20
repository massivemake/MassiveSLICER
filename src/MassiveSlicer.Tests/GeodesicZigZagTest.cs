using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Geodesic slicing shares MeshGraph.SliceScalarLayers with Curved — zig-zag must reverse
/// open single-skin faces each layer.
/// </summary>
public sealed class GeodesicZigZagTest
{
    private const float Bead = 6f;

    private static Vector3[] ThinWallBox(float len = 200f, float thick = 18f, float h = 80f)
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

    [Fact]
    public void GeodesicZigZag_OpenFacesReverseEachLayer()
    {
        var settings = new SliceSettings
        {
            LayerHeight = 5f,
            FirstLayerHeight = 5f,
            BeadWidth = Bead,
            InfillPattern = InfillPattern.None,
            ZigZagSeam = true,
            ZigZagAllowSameLayerTravel = true,
        };

        var tp = GeodesicSlicer.Slice([ThinWallBox()], settings);
        Assert.True(tp.Layers.Count >= 4, $"expected several geodesic layers, got {tp.Layers.Count}");

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
            $"expected zig-zag reverse on geodesic layers, d0={d0} d1={d1} dot={dot:0.###}");
    }

    [Fact]
    public void GeodesicZigZag_SingleSkinShorterThanClosedPerimeter()
    {
        var settings = new SliceSettings
        {
            LayerHeight = 5f,
            FirstLayerHeight = 5f,
            BeadWidth = Bead,
            InfillPattern = InfillPattern.None,
            ZigZagSeam = true,
        };
        var tp = GeodesicSlicer.Slice([ThinWallBox(len: 200f, thick: 18f, h: 60f)], settings);
        Assert.True(tp.Layers.Count >= 3);

        float avg = 0f;
        int n = 0;
        foreach (var lyr in tp.Layers)
        {
            float len = 0f;
            foreach (var m in lyr.Moves)
            {
                if (m.Kind != MoveKind.Extrude || m.IsLayerChange || m.IsLayerStitch) continue;
                len += Vector3.Distance(m.From, m.To);
            }
            if (len < 1f) continue;
            avg += len;
            n++;
        }
        Assert.True(n > 0);
        avg /= n;
        // Full perimeter ~ 2*(200+18)=436; single long face ~200.
        Assert.True(avg < 320f, $"geodesic zig-zag avg {avg:0.#} looks like closed dual wall");
        Assert.True(avg > 80f, $"geodesic zig-zag avg {avg:0.#} too short");
    }
}
