using System.Linq;
using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class AdaMillPlannerTest
{
    static AdaMachiningSettings Base(MillOperationKind op) => new()
    {
        Operation        = op,
        ToolDiameterMm   = 6,
        BallEnd          = true,
        StepoverMm       = 4,
        StepdownMm       = 2,
        RetractHeightMm  = 10,
        FeedHeightMm     = 2,
        CuttingFeedMmS   = 50,
        AxialPassCount   = 1,
        CutoutCutDepthMm = 6,
        CutoutLayerHeightMm = 2,
        MorphSteps       = 5,
        DrillingBreakthroughMm = 5,
    };

    static AdaMillRequest BoxReq(MillOperationKind op, float s = 20)
    {
        Box(s, out var pos, out var nrm, out var idx);
        return new AdaMillRequest
        {
            Settings  = Base(op),
            Positions = pos,
            Normals   = nrm,
            Indices   = idx,
        };
    }

    static int MillCuts(Toolpath tp) =>
        tp.Layers.Sum(l => l.Moves.Count(m => m.Kind == MoveKind.Mill));

    [Fact]
    public void Catalog_includes_AdaOne_morph()
    {
        Assert.Contains(MillOperationInfo.Catalog, c => c.Kind == MillOperationKind.Morph);
        Assert.Equal(8, MillOperationInfo.Catalog.Count);
    }

    [Fact]
    public void Cutout_steps_deeper_each_pass()
    {
        var tp = AdaMillPlanner.Generate(BoxReq(MillOperationKind.Cutout));
        Assert.True(tp.Layers.Count >= 3, $"cutout layers={tp.Layers.Count}");
        Assert.True(MillCuts(tp) > 0);
        float z0 = tp.Layers[0].Z;
        float z1 = tp.Layers[^1].Z;
        Assert.True(z0 > z1, $"cutout should step down, top={z0} bottom={z1}");
        Assert.All(tp.Layers, l => Assert.Contains(l.Moves, m => m.Kind == MoveKind.Mill));
    }

    [Fact]
    public void Contouring_emits_waterline_layers()
    {
        var tp = AdaMillPlanner.Generate(BoxReq(MillOperationKind.Contouring));
        Assert.True(tp.Layers.Count >= 2, $"contour layers={tp.Layers.Count}");
        Assert.True(MillCuts(tp) > 0);
    }

    [Fact]
    public void Drilling_plunges_past_bottom_by_breakthrough()
    {
        var req = BoxReq(MillOperationKind.Drilling);
        var tp = AdaMillPlanner.Generate(req);
        Assert.True(MillCuts(tp) > 0);
        float minZ = tp.Layers.SelectMany(l => l.Moves).Min(m => MathF.Min(m.From.Z, m.To.Z));
        Assert.True(minZ <= 0.01f - req.Settings.DrillingBreakthroughMm + 0.2f,
            $"drill minZ={minZ} expected around {-req.Settings.DrillingBreakthroughMm}");
    }

    [Fact]
    public void PlanarFacing_rasters_the_top()
    {
        var tp = AdaMillPlanner.Generate(BoxReq(MillOperationKind.PlanarFacing));
        Assert.True(MillCuts(tp) > 0);
    }

    [Fact]
    public void MultiAxisFinishing_covers_the_box()
    {
        var tp = AdaMillPlanner.Generate(BoxReq(MillOperationKind.MultiAxisFinishing));
        Assert.True(MillCuts(tp) > 0);
    }

    [Fact]
    public void Morph_emits_step_count_layers()
    {
        var tp = AdaMillPlanner.Generate(BoxReq(MillOperationKind.Morph));
        Assert.Equal(5, tp.Layers.Count);
        Assert.True(MillCuts(tp) > 0);
    }

    [Fact]
    public void Swarf_follows_a_closed_guide()
    {
        var tp = AdaMillPlanner.Generate(BoxReq(MillOperationKind.Swarf));
        Assert.True(MillCuts(tp) > 0);
    }

    [Fact]
    public void Empty_mesh_is_empty_path()
    {
        var req = new AdaMillRequest
        {
            Settings  = Base(MillOperationKind.Cutout),
            Positions = [],
            Normals   = [],
            Indices   = [],
        };
        var tp = AdaMillPlanner.Generate(req);
        Assert.Empty(tp.Layers);
    }

    static void Box(float s, out Vector3[] pos, out Vector3[] nrm, out int[] idx)
    {
        pos =
        [
            new(0, 0, 0), new(s, 0, 0), new(s, s, 0), new(0, s, 0),
            new(0, 0, s), new(s, 0, s), new(s, s, s), new(0, s, s),
        ];
        // 12 triangles, outward-ish normals per vertex (box corners).
        nrm = new Vector3[8];
        for (int i = 0; i < 8; i++)
            nrm[i] = Vector3.Normalize(pos[i] - new Vector3(s / 2, s / 2, s / 2));
        idx =
        [
            0, 1, 2, 0, 2, 3, // bottom
            4, 6, 5, 4, 7, 6, // top
            0, 4, 5, 0, 5, 1, // y=0
            3, 2, 6, 3, 6, 7, // y=s
            0, 3, 7, 0, 7, 4, // x=0
            1, 5, 6, 1, 6, 2, // x=s
        ];
    }
}
