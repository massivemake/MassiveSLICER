using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Core.Slicing.Effects;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class GhostFillRepro
{
    private static Vector3[] Cube(float s)
    {
        var v = new List<Vector3>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        { v.AddRange([a,b,c, a,c,d]); }
        float h = s/2;
        Quad(new(-h,-h,0), new(h,-h,0), new(h,h,0), new(-h,h,0));
        Quad(new(-h,-h,s), new(-h,h,s), new(h,h,s), new(h,-h,s));
        Quad(new(-h,-h,0), new(-h,h,0), new(-h,h,s), new(-h,-h,s));
        Quad(new(h,-h,0), new(h,-h,s), new(h,h,s), new(h,h,0));
        Quad(new(-h,-h,0), new(-h,-h,s), new(h,-h,s), new(h,-h,0));
        Quad(new(-h,h,0), new(h,h,0), new(h,h,s), new(-h,h,s));
        return v.ToArray();
    }

    [Fact]
    public void GhostMeshGrid_EmitsInfill()
    {
        var settings = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = 6f,
            InfillPattern = InfillPattern.GhostMeshGrid,
        };
        var tp = PlanarSlicer.Slice([Cube(300f)], settings, null);
        tp = WaveEffect.Apply(tp, settings);
        tp = PatternEffect.Apply(tp, settings);
        int moves = tp.Layers.Sum(l => l.Moves.Count);
        float totalLen = tp.Layers.Sum(l => l.Moves.Where(m => m.Kind == MoveKind.Extrude)
            .Sum(m => Vector3.Distance(m.From, m.To)));
        // A 300mm cube shell perimeter is ~1200mm/layer; grid infill adds far more.
        var firstLayer = tp.Layers.FirstOrDefault();
        float l0 = firstLayer is null ? 0 : firstLayer.Moves.Where(m => m.Kind == MoveKind.Extrude)
            .Sum(m => Vector3.Distance(m.From, m.To));
        Assert.True(tp.Layers.Count > 10, $"layers {tp.Layers.Count}");
        Assert.True(l0 > 2000f, $"first-layer extrusion only {l0:0} mm — infill missing (shell-only ~1200)");
    }

    [Fact]
    public void GhostMeshGrid_EmitsInfill_OnAngledSlicer()
    {
        var settings = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = 6f,
            TiltAngle = 25f,
            InfillPattern = InfillPattern.GhostMeshGrid,
        };
        var tp = AngledPlanarSlicer.Slice([Cube(300f)], settings);
        Assert.True(tp.Layers.Count > 10, $"layers {tp.Layers.Count}");

        var mid = tp.Layers[tp.Layers.Count / 2];
        float len = mid.Moves.Where(m => m.Kind == MoveKind.Extrude)
            .Sum(m => Vector3.Distance(m.From, m.To));
        Assert.True(len > 2000f, $"mid-layer extrusion only {len:0} mm — angled infill missing");

        // Infill must lie on the tilted plane and carry its normal for tool orientation.
        Assert.All(mid.Moves.Where(m => m.Kind == MoveKind.Extrude).Take(50),
            m => Assert.True(m.Normal.Z > 0.5f, "missing plane normal"));
    }
}
