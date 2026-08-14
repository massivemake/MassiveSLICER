using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

/// <summary>
/// Does the SLICER actually stamp <see cref="ToolpathMove.IsWall"/>, which
/// <see cref="PatternScope.WallsOnly"/> depends on?
///
/// <para>
/// <see cref="PatternSkinOnlyTest"/> builds its toolpaths by hand and sets the flag itself, so it
/// proves the effects honour it — it cannot prove anything ever produces it. This drives the real
/// <see cref="PlanarSlicer"/> over a real mesh instead, which is the half of the path a user
/// running "Walls only" depends on. That gap is not theoretical: a scope setting that looked
/// correct in every test still did nothing on a real part, because nothing tested the seam
/// between the slicer and the effect.
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

    private static SliceSettings Settings(SlicingMode mode = SlicingMode.Normal) => new()
    {
        SlicingMode      = mode,
        LayerHeight      = 2f,
        FirstLayerHeight = 2f,
        BeadWidth        = 8f,
        InfillPattern    = InfillPattern.None,
    };

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
            $"slicer marked NO move as a wall ({extrudes} extrude moves) — 'Walls only' would " +
            "treat the whole part as internal structure and the pattern would vanish");
    }

    /// <summary>Surface mode skips the contour inset entirely, so it reaches the flag by a
    /// different path and deserves its own check.</summary>
    [Fact]
    public void SurfaceModeAlsoMarksPerimetersAsWalls()
    {
        var tp = PlanarSlicer.Slice(TubeWithBore(), Settings(SlicingMode.Surface));

        Assert.True(Walls(tp).Count > 0, "Surface mode marked no move as a wall at all");
    }
}
