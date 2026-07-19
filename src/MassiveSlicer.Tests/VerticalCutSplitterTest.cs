using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Modifiers;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class VerticalCutSplitterTest
{
    private static Toolpath OneLayerToolpath(int index, float z, params ToolpathMove[] moves)
    {
        var layer = new ToolpathLayer(index, z) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.AddRange(moves);
        var tp = new Toolpath();
        tp.Layers.Add(layer);
        return tp;
    }

    private static ToolpathMove Extrude(float x0, float y0, float x1, float y1, float z = 0f)
        => new(new Vector3(x0, y0, z), new Vector3(x1, y1, z), MoveKind.Extrude);

    [Fact]
    public void Move_entirely_on_one_side_is_kept_whole_and_absent_from_the_other()
    {
        var source = OneLayerToolpath(0, 0f, Extrude(-10, 0, -5, 0));

        var result = VerticalCutSplitter.Split(source, planePoint: Vector3.Zero, planeNormal: Vector3.UnitX);

        Assert.Empty(result.Positive.Layers);
        var negMoves = Assert.Single(result.Negative.Layers).Moves;
        var move = Assert.Single(negMoves);
        Assert.Equal(new Vector3(-10, 0, 0), move.From);
        Assert.Equal(new Vector3(-5, 0, 0), move.To);
    }

    [Fact]
    public void Move_crossing_the_plane_is_split_at_the_intersection_point()
    {
        var source = OneLayerToolpath(0, 0f, Extrude(-10, 0, 10, 0));

        var result = VerticalCutSplitter.Split(source, planePoint: Vector3.Zero, planeNormal: Vector3.UnitX);

        var posMove = Assert.Single(Assert.Single(result.Positive.Layers).Moves);
        var negMove = Assert.Single(Assert.Single(result.Negative.Layers).Moves);

        Assert.Equal(new Vector3(0, 0, 0), posMove.From);
        Assert.Equal(new Vector3(10, 0, 0), posMove.To);
        Assert.Equal(new Vector3(-10, 0, 0), negMove.From);
        Assert.Equal(new Vector3(0, 0, 0), negMove.To);
    }

    [Fact]
    public void Gap_left_by_dropping_the_other_sides_move_gets_a_bridging_travel()
    {
        // Two separate extrude runs on the positive side, with a move on the negative
        // side sandwiched between them (as if the path dipped across and back).
        var source = OneLayerToolpath(0, 0f,
            Extrude(5, 0, 10, 0),      // positive
            Extrude(10, 0, -5, 5),     // crosses to negative (positive part: 10,0 -> 0,~3.33)
            Extrude(-5, 5, -1, 6),     // fully negative
            Extrude(-1, 6, 8, 8));     // fully positive again — should bridge from wherever positive pen last was

        var result = VerticalCutSplitter.Split(source, planePoint: Vector3.Zero, planeNormal: Vector3.UnitX);

        var posMoves = Assert.Single(result.Positive.Layers).Moves;
        // Expect: [extrude 5,0->10,0], [extrude 10,0->intersection], [bridging travel], [extrude -1,6->8,8]... but
        // the last extrude starts at x=-1 (negative) so it must itself be clipped too — assert structurally instead
        // of hand-computing every coordinate: there must be at least one IsMergeConnector travel bridging a gap.
        Assert.Contains(posMoves, m => m.Kind == MoveKind.Travel && m.IsMergeConnector);
    }

    [Fact]
    public void Layer_index_and_z_are_preserved_unchanged_on_both_sides()
    {
        var source = OneLayerToolpath(3, 42f, Extrude(-10, 0, 10, 0));

        var result = VerticalCutSplitter.Split(source, planePoint: Vector3.Zero, planeNormal: Vector3.UnitX);

        Assert.Equal(3, result.Positive.Layers[0].Index);
        Assert.Equal(42f, result.Positive.Layers[0].Z);
        Assert.Equal(3, result.Negative.Layers[0].Index);
        Assert.Equal(42f, result.Negative.Layers[0].Z);
    }

    [Fact]
    public void Split_does_not_mutate_the_source_toolpath()
    {
        var source = OneLayerToolpath(0, 0f, Extrude(-10, 0, 10, 0));

        VerticalCutSplitter.Split(source, planePoint: Vector3.Zero, planeNormal: Vector3.UnitX);

        var move = Assert.Single(source.Layers[0].Moves);
        Assert.Equal(new Vector3(-10, 0, 0), move.From);
        Assert.Equal(new Vector3(10, 0, 0), move.To);
    }

    [Fact]
    public void Layer_with_content_on_only_one_side_produces_no_layer_on_the_other()
    {
        var source = OneLayerToolpath(0, 0f, Extrude(-10, 0, -5, 0));

        var result = VerticalCutSplitter.Split(source, planePoint: Vector3.Zero, planeNormal: Vector3.UnitX);

        Assert.Empty(result.Positive.Layers);
        Assert.Single(result.Negative.Layers);
    }

    [Fact]
    public void Contours_are_not_carried_over_since_move_indices_no_longer_correspond()
    {
        var source = OneLayerToolpath(0, 0f, Extrude(-10, 0, 10, 0));
        source.Layers[0].Contours.Add(new ContourSpan(0, 1, Closed: false, EntryTravelIndex: -1));

        var result = VerticalCutSplitter.Split(source, planePoint: Vector3.Zero, planeNormal: Vector3.UnitX);

        Assert.Empty(result.Positive.Layers[0].Contours);
        Assert.Empty(result.Negative.Layers[0].Contours);
    }

    [Fact]
    public void Works_with_a_y_axis_plane_normal_too()
    {
        var source = OneLayerToolpath(0, 0f, Extrude(0, -10, 0, 10));

        var result = VerticalCutSplitter.Split(source, planePoint: Vector3.Zero, planeNormal: Vector3.UnitY);

        var posMove = Assert.Single(Assert.Single(result.Positive.Layers).Moves);
        var negMove = Assert.Single(Assert.Single(result.Negative.Layers).Moves);
        Assert.Equal(new Vector3(0, 0, 0), posMove.From);
        Assert.Equal(new Vector3(0, 10, 0), posMove.To);
        Assert.Equal(new Vector3(0, -10, 0), negMove.From);
        Assert.Equal(new Vector3(0, 0, 0), negMove.To);
    }
}
