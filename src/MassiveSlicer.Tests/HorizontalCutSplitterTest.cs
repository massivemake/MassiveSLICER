using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Modifiers;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class HorizontalCutSplitterTest
{
    private static Toolpath MakeToolpath(params float[] layerZs)
    {
        var tp = new Toolpath();
        for (int i = 0; i < layerZs.Length; i++)
        {
            var layer = new ToolpathLayer(i, layerZs[i]) { PlaneNormal = Vector3.UnitZ };
            layer.Moves.Add(new ToolpathMove(
                new Vector3(0, 0, layerZs[i]), new Vector3(10, 0, layerZs[i]), MoveKind.Extrude));
            tp.Layers.Add(layer);
        }
        return tp;
    }

    [Fact]
    public void Split_buckets_whole_layers_by_z()
    {
        var source = MakeToolpath(0f, 5f, 10f, 15f, 20f);

        var result = HorizontalCutSplitter.Split(source, cutZ: 12f);

        Assert.Equal([0f, 5f, 10f], result.Below.Layers.Select(l => l.Z));
        Assert.Equal([15f, 20f], result.Above.Layers.Select(l => l.Z));
    }

    [Fact]
    public void Split_preserves_original_layer_index_and_z_on_both_sides()
    {
        var source = MakeToolpath(0f, 5f, 10f, 15f, 20f);

        var result = HorizontalCutSplitter.Split(source, cutZ: 12f);

        // Layer-sync requirement: a piece's layers keep the exact index/Z they had
        // in the un-cut model, not renumbered from 0, so both sides stay traceable
        // back to the same original layer stack.
        Assert.Equal([0, 1, 2], result.Below.Layers.Select(l => l.Index));
        Assert.Equal([3, 4], result.Above.Layers.Select(l => l.Index));
    }

    [Fact]
    public void Split_does_not_mutate_the_source_toolpath()
    {
        var source = MakeToolpath(0f, 5f, 10f);

        HorizontalCutSplitter.Split(source, cutZ: 6f);

        Assert.Equal(3, source.Layers.Count);
        Assert.Equal([0f, 5f, 10f], source.Layers.Select(l => l.Z));
    }

    [Fact]
    public void Split_preserves_moves_and_contours_within_each_layer()
    {
        var source = MakeToolpath(0f, 10f);
        source.Layers[0].Contours.Add(new ContourSpan(0, 1, Closed: false, EntryTravelIndex: -1));

        var result = HorizontalCutSplitter.Split(source, cutZ: 5f);

        var belowLayer = Assert.Single(result.Below.Layers);
        Assert.Single(belowLayer.Moves);
        Assert.Single(belowLayer.Contours);
    }

    [Fact]
    public void Split_with_cut_above_all_layers_yields_empty_above()
    {
        var source = MakeToolpath(0f, 5f, 10f);

        var result = HorizontalCutSplitter.Split(source, cutZ: 1000f);

        Assert.Equal(3, result.Below.Layers.Count);
        Assert.Empty(result.Above.Layers);
    }

    [Fact]
    public void Split_with_cut_below_all_layers_yields_empty_below()
    {
        var source = MakeToolpath(0f, 5f, 10f);

        var result = HorizontalCutSplitter.Split(source, cutZ: -1000f);

        Assert.Empty(result.Below.Layers);
        Assert.Equal(3, result.Above.Layers.Count);
    }
}
