using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>X-Bracing Wall: interior dual-wall diagonal channels (LFAM reference style).</summary>
public sealed class XBracingTest
{
    private const float Bead = 6f;

    /// <summary>Thick LFAM-scale wall (matches reference photos, not thin sheet).</summary>
    private static Vector3[] ThickWall(float len = 500f, float thick = 80f, float h = 200f)
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
    public void XBracingProducesInteriorLightningOnThickWall()
    {
        var settings = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
            InfillPattern = InfillPattern.None,
            XBracingEnabled = true,
            XBracingDepthMm = 50f,
            XBracingSpanMm = 120f,
            XBracingAngleDeg = 30f,
            XBracingExtendEdges = true,
        };
        var tp = PlanarSlicer.Slice([ThickWall()], settings, null);
        Assert.True(tp.Layers.Count > 10, $"expected layers, got {tp.Layers.Count}");
        int lightning = tp.Layers.SelectMany(l => l.Moves)
            .Count(m => m.IsLightning && m.Kind == MoveKind.Extrude);
        Assert.True(lightning > 50,
            $"X-bracing produced only {lightning} lightning segments — " +
            $"log={string.Join(" | ", tp.FormboundStats?.UncoveredLog ?? [])}");
        Assert.Contains(tp.FormboundStats!.UncoveredLog, l => l.Contains("INTERIOR") && l.Contains("fingers="));
        // Must have actually placed fingers.
        Assert.DoesNotContain(tp.FormboundStats.UncoveredLog, l => l.Contains("fingers=0"));
    }

    [Fact]
    public void XBracingDisabledProducesNoLightning()
    {
        var settings = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
            InfillPattern = InfillPattern.None,
            XBracingEnabled = false,
        };
        var tp = PlanarSlicer.Slice([ThickWall()], settings, null);
        int lightning = tp.Layers.SelectMany(l => l.Moves)
            .Count(m => m.IsLightning && m.Kind == MoveKind.Extrude);
        Assert.Equal(0, lightning);
    }

    [Fact]
    public void XBracingDepthIsClampedOnMediumWall()
    {
        // 40 mm thick: Depth 50 should clamp and still emit some braces.
        var settings = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
            InfillPattern = InfillPattern.None,
            XBracingEnabled = true,
            XBracingDepthMm = 50f,
            XBracingSpanMm = 100f,
            XBracingAngleDeg = 30f,
            XBracingExtendEdges = true,
        };
        var tp = PlanarSlicer.Slice([ThickWall(thick: 40f, h: 180f)], settings, null);
        int lightning = tp.Layers.SelectMany(l => l.Moves)
            .Count(m => m.IsLightning && m.Kind == MoveKind.Extrude);
        Assert.True(lightning > 20,
            $"clamped X-bracing produced only {lightning} lightning — " +
            $"log={string.Join(" | ", tp.FormboundStats?.UncoveredLog ?? [])}");
    }
}
