using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

public sealed class LayerHeightHeatmapTest
{
    [Fact]
    public void Thinnest_cut_is_blue_and_thickest_is_red()
    {
        var tp = Path(
            Layer(0, 1f, 1f, MoveKind.Extrude),
            Layer(1, 4f, 3f, MoveKind.Extrude));

        var scores = LayerHeightHeatmap.Scores(tp);

        Assert.Equal(0f, scores[0], 3);
        Assert.Equal(1f, scores[1], 3);
        Near(new Vector3(0f, 0f, 1f), BeadOverhangPalette.Color(scores[0], true));
        Near(new Vector3(1f, 0f, 0f), BeadOverhangPalette.Color(scores[1], true));
    }

    [Fact]
    public void Uniform_layers_sit_on_green()
    {
        var tp = Path(
            Layer(0, 2f, 2f, MoveKind.Extrude),
            Layer(1, 4f, 2f, MoveKind.Extrude));

        var scores = LayerHeightHeatmap.Scores(tp);

        Assert.Equal(0.5f, scores[0], 3);
        Assert.Equal(0.5f, scores[1], 3);
    }

    [Fact]
    public void Height_scale_changes_the_bead_and_travel_does_not_set_the_range()
    {
        var thin = Layer(0, 2f, 4f, MoveKind.Extrude);
        thin.Moves[0] = thin.Moves[0] with { HeightScale = 0.5f };
        var hop = Layer(1, 6f, 4f, MoveKind.Travel);
        var thick = Layer(2, 10f, 4f, MoveKind.Extrude);
        var tp = Path(thin, hop, thick);

        var scores = LayerHeightHeatmap.Scores(tp);

        Assert.Equal(0f, scores[0], 3);
        Assert.Equal(0f, scores[1], 3);
        Assert.Equal(1f, scores[2], 3);
    }

    [Fact]
    public void Missing_height_uses_the_gap_between_layer_z()
    {
        var tp = Path(
            Layer(0, 0f, 0f, MoveKind.Extrude),
            Layer(1, 1f, 0f, MoveKind.Extrude),
            Layer(2, 4f, 0f, MoveKind.Extrude));

        var scores = LayerHeightHeatmap.Scores(tp);

        Assert.Equal(0f, scores[0], 3);
        Assert.Equal(0f, scores[1], 3);
        Assert.Equal(1f, scores[2], 3);
    }

    [Fact]
    public void Empty_toolpath_has_no_scores()
    {
        Assert.Empty(LayerHeightHeatmap.Scores(new Toolpath()));
    }

    static Toolpath Path(params ToolpathLayer[] layers)
    {
        var tp = new Toolpath();
        foreach (var layer in layers)
            tp.Layers.Add(layer);
        return tp;
    }

    static ToolpathLayer Layer(int index, float z, float height, MoveKind kind)
    {
        var layer = new ToolpathLayer(index, z) { Height = height };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, z), new Vector3(10, 0, z), kind));
        return layer;
    }

    static void Near(Vector3 expected, Vector3 actual)
    {
        Assert.True((expected - actual).Length() < 1e-4f, $"expected {expected}, got {actual}");
    }
}
