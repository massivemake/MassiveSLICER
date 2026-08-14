using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

/// <summary>
/// Does the SLICER actually stamp the wall flags the pattern scope depends on?
///
/// <para>
/// <see cref="PatternSkinOnlyTest"/> builds its toolpaths by hand and sets
/// <see cref="ToolpathMove.IsWall"/>/<see cref="ToolpathMove.IsOuterWall"/> itself, so it proves
/// the effects honour the flags — it cannot prove anything ever produces them. This drives the
/// real <see cref="PlanarSlicer"/> over a real mesh instead, which is the half of the path a user
/// running "Outer surface only" depends on.
/// </para>
///
/// <para>
/// The model is a square tube: a 200mm box with a 40mm square bore through it. At any Z the
/// cross-section is two nested loops — the outer surface (nesting depth 0) and the bore wall
/// (depth 1) — which is the minimal stand-in for a part whose skin should be patterned while a
/// modelled hole through it stays straight.
/// </para>
/// </summary>
public class PatternScopeSlicerFlagsTest
{
    private static Vector3[] Box(float x0, float y0, float x1, float y1, float z0, float z1)
    {
        Vector3 V(float x, float y, float z) => new(x, y, z);
        var c = new[]
        {
            V(x0, y0, z0), V(x1, y0, z0), V(x1, y1, z0), V(x0, y1, z0),
            V(x0, y0, z1), V(x1, y0, z1), V(x1, y1, z1), V(x0, y1, z1),
        };
        int[] idx =
        [
            0,2,1, 0,3,2,   // bottom
            4,5,6, 4,6,7,   // top
            0,1,5, 0,5,4,   // -Y
            1,2,6, 1,6,5,   // +X
            2,3,7, 2,7,6,   // +Y
            3,0,4, 3,4,7,   // -X
        ];
        return [.. idx.Select(i => c[i])];
    }

    /// <summary>200mm box, 40mm square bore up the middle. Centre of both is (100,100).</summary>
    private static List<Vector3[]> TubeWithBore() =>
    [
        Box(0f, 0f, 200f, 200f, 0f, 20f),
        Box(80f, 80f, 120f, 120f, 0f, 20f),
    ];

    private static SliceSettings Settings() => new()
    {
        LayerHeight      = 2f,
        FirstLayerHeight = 2f,
        BeadWidth        = 8f,
        InfillPattern    = InfillPattern.None,
    };

    /// <summary>
    /// Chebyshev distance from the part centre. The bore wall sits near 20-25mm out, the outer
    /// surface near 96mm, so a 50mm split cleanly separates the two loops however the inset
    /// nudges them.
    /// </summary>
    private static float FromCentre(Vector3 p)
        => MathF.Max(MathF.Abs(p.X - 100f), MathF.Abs(p.Y - 100f));

    private static List<ToolpathMove> Walls(Toolpath tp) =>
        [.. tp.Layers.SelectMany(l => l.Moves)
             .Where(m => m.Kind == MoveKind.Extrude && !m.IsLayerStitch && m.IsWall)];

    [Fact]
    public void SlicerMarksPerimetersAsWalls()
    {
        var tp = PlanarSlicer.Slice(TubeWithBore(), Settings());

        int extrudes = tp.Layers.SelectMany(l => l.Moves)
                         .Count(m => m.Kind == MoveKind.Extrude && !m.IsLayerStitch);
        int walls = Walls(tp).Count;

        Assert.True(walls > 0,
            $"slicer marked NO move as a wall ({extrudes} extrude moves) — every pattern scope " +
            "other than Everything treats the whole part as internal structure");
    }

    [Fact]
    public void OuterSurfaceIsMarkedOuter()
    {
        var tp = PlanarSlicer.Slice(TubeWithBore(), Settings());

        var outerLoop = Walls(tp).Where(m => FromCentre(m.From) > 50f).ToList();
        Assert.True(outerLoop.Count > 0, "no wall moves found on the outer surface");

        int notOuter = outerLoop.Count(m => !m.IsOuterWall);
        Assert.True(notOuter == 0,
            $"{notOuter}/{outerLoop.Count} outer-surface wall moves were NOT flagged IsOuterWall — " +
            "under 'Outer surface only' the visible skin would be left unpatterned");
    }

    [Fact]
    public void BoreWallIsNotMarkedOuter()
    {
        var tp = PlanarSlicer.Slice(TubeWithBore(), Settings());

        var boreLoop = Walls(tp).Where(m => FromCentre(m.From) <= 50f).ToList();
        Assert.True(boreLoop.Count > 0, "no wall moves found on the bore");

        int wronglyOuter = boreLoop.Count(m => m.IsOuterWall);
        Assert.True(wronglyOuter == 0,
            $"{wronglyOuter}/{boreLoop.Count} bore-wall moves were flagged IsOuterWall — " +
            "the slicer is calling a depth-1 hole part of the outer surface, so 'Outer surface " +
            "only' cannot exclude it and the pattern lands everywhere");
    }

    /// <summary>
    /// Same tube, sliced in Surface mode. Surface mode skips the contour inset and re-derives
    /// depths through <c>SurfaceSlicing.FilterContours</c>, so it is a genuinely different path
    /// to the same flag — and it is the mode scanned/organic parts tend to be sliced in.
    /// </summary>
    [Fact]
    public void BoreWallIsNotMarkedOuterInSurfaceMode()
    {
        var settings = new SliceSettings
        {
            SlicingMode      = SlicingMode.Surface,
            LayerHeight      = 2f,
            FirstLayerHeight = 2f,
            BeadWidth        = 8f,
            InfillPattern    = InfillPattern.None,
        };
        var tp = PlanarSlicer.Slice(TubeWithBore(), settings);

        var walls = Walls(tp);
        Assert.True(walls.Count > 0, "Surface mode marked no move as a wall at all");

        var boreLoop = walls.Where(m => FromCentre(m.From) <= 50f).ToList();
        Assert.True(boreLoop.Count > 0, "no wall moves found on the bore in Surface mode");

        int wronglyOuter = boreLoop.Count(m => m.IsOuterWall);
        Assert.True(wronglyOuter == 0,
            $"Surface mode: {wronglyOuter}/{boreLoop.Count} bore-wall moves flagged IsOuterWall " +
            "— 'Outer surface only' cannot exclude a hole in this mode");
    }
}
