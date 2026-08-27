using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class StructuralSupportPlannerTest
{
    /// <summary>Straight wall along X at y=0, three layers, one 300mm move each.</summary>
    static Toolpath Wall(int layers = 3)
    {
        var tp = new Toolpath();
        for (int li = 0; li < layers; li++)
        {
            var layer = new ToolpathLayer(li, li * 3f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
            layer.Moves.Add(new ToolpathMove(
                new Vector3(0, 0, li * 3f), new Vector3(300, 0, li * 3f), MoveKind.Extrude));
            tp.Layers.Add(layer);
        }
        return tp;
    }

    static SliceSettings With(params StructuralSupportSpec[] specs) =>
        new() { StructuralSupports = specs };

    [Fact]
    public void Rectangle_SplicesDetour_SameAnchorEveryLayer()
    {
        var tp = Wall();
        var spec = new StructuralSupportSpec
        {
            Shape = SupportShapeKind.Rectangle,
            AnchorX = 150, AnchorY = 0, AnchorLayer = 0,
            LayersUp = 9999, LayersDown = 0,
            CenterX = 150, CenterY = 80, WidthMm = 92, DepthMm = 42,
        };
        StructuralSupportPlanner.Apply(tp, With(spec));

        foreach (var layer in tp.Layers)
        {
            // Wall split into 2 + neck out + 4 rect wrap sides + neck back = 8 moves.
            Assert.True(layer.Moves.Count >= 7, $"L{layer.Index}: {layer.Moves.Count} moves");

            // The wall no longer runs continuously through the anchor — it BREAKS there,
            // one bead wide and centred on it (a real surface break for the neck mouth).
            // So instead of a move landing exactly on (150, 0), assert the two wall pieces
            // straddle it symmetrically.
            var wallXs = layer.Moves
                .Where(m => MathF.Abs(m.From.Y) < 1e-3f && MathF.Abs(m.To.Y) < 1e-3f)
                .SelectMany(m => new[] { m.From.X, m.To.X })
                .ToList();
            float leftEnd  = wallXs.Where(x => x < 150f).Max();
            float rightEnd = wallXs.Where(x => x > 150f).Min();
            Assert.Equal(150f, (leftEnd + rightEnd) * 0.5f, 1);
            Assert.Equal(6f, rightEnd - leftEnd, 1);   // default BeadWidth

            // The detour must reach the rectangle's far edge (y = 80 + 21 = 101).
            float maxY = layer.Moves.Max(m => MathF.Max(m.From.Y, m.To.Y));
            Assert.Equal(101f, maxY, 1);

            // Continuous extrusion: consecutive moves connect (no travels introduced).
            for (int i = 1; i < layer.Moves.Count; i++)
                Assert.True(Vector3.Distance(layer.Moves[i - 1].To, layer.Moves[i].From) < 1e-3f,
                    $"L{layer.Index} gap at move {i}");

            // Path still starts at wall start and ends at wall end.
            Assert.Equal(0f, layer.Moves[0].From.X, 2);
            Assert.Equal(300f, layer.Moves[^1].To.X, 2);
        }
    }

    [Fact]
    public void LayerRange_LimitsEffect()
    {
        var tp = Wall(5);
        var spec = new StructuralSupportSpec
        {
            AnchorX = 150, AnchorY = 0, AnchorLayer = 2,
            LayersUp = 1, LayersDown = 1,
            CenterX = 150, CenterY = 60,
        };
        StructuralSupportPlanner.Apply(tp, With(spec));

        Assert.Single(tp.Layers[0].Moves);        // untouched
        Assert.True(tp.Layers[1].Moves.Count > 1); // anchor-1
        Assert.True(tp.Layers[2].Moves.Count > 1); // anchor
        Assert.True(tp.Layers[3].Moves.Count > 1); // anchor+1
        Assert.Single(tp.Layers[4].Moves);        // untouched
    }

    [Fact]
    public void Circle_WrapsFullOutline()
    {
        var tp = Wall(1);
        var spec = new StructuralSupportSpec
        {
            Shape = SupportShapeKind.Circle,
            AnchorX = 150, AnchorY = 0, AnchorLayer = 0,
            CenterX = 150, CenterY = 70, WidthMm = 60, // Ø60 cylinder
        };
        StructuralSupportPlanner.Apply(tp, With(spec));

        var layer = tp.Layers[0];
        // 32-segment circle + splits + necks.
        Assert.True(layer.Moves.Count > 30);
        float maxY = layer.Moves.Max(m => MathF.Max(m.From.Y, m.To.Y));
        Assert.Equal(100f, maxY, 0); // 70 + r30
        for (int i = 1; i < layer.Moves.Count; i++)
            Assert.True(Vector3.Distance(layer.Moves[i - 1].To, layer.Moves[i].From) < 1e-3f);
    }

    [Fact]
    public void Disabled_NoOp()
    {
        var tp = Wall(1);
        var spec = new StructuralSupportSpec
        {
            AnchorX = 150, AnchorY = 0, Enabled = false,
            CenterX = 150, CenterY = 60,
        };
        StructuralSupportPlanner.Apply(tp, With(spec));
        Assert.Single(tp.Layers[0].Moves);
    }
}
