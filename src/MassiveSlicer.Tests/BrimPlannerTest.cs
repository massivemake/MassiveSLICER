using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

public class BrimPlannerTest
{
    private const float Bead = 10f;

    /// <summary>100×100 square wall on layer 0 at Z=3.</summary>
    private static Toolpath SquareToolpath()
    {
        var layer = new ToolpathLayer(0, 3f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
        Vector3 P(float x, float y) => new(x, y, 3f);
        layer.Moves.Add(new ToolpathMove(P(0, 0),     P(100, 0),   MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(P(100, 0),   P(100, 100), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(P(100, 100), P(0, 100),   MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(P(0, 100),   P(0, 0),     MoveKind.Extrude));
        var tp = new Toolpath();
        tp.Layers.Add(layer);
        return tp;
    }

    private static SliceSettings Settings(bool enabled = true, int loops = 3) => new()
    {
        BeadWidth   = Bead,
        LayerHeight = 3f,
        BrimEnabled = enabled,
        BrimLoops   = loops,
    };

    private static List<ToolpathMove> BrimMoves(Toolpath tp, int originalCount) =>
        tp.Layers[0].Moves.Take(tp.Layers[0].Moves.Count - originalCount).ToList();

    [Fact]
    public void Disabled_leaves_toolpath_untouched()
    {
        var tp = SquareToolpath();
        BrimPlanner.Apply(tp, Settings(enabled: false));
        Assert.Equal(4, tp.Layers[0].Moves.Count);
    }

    [Fact]
    public void Brim_is_prepended_and_original_moves_survive()
    {
        var tp = SquareToolpath();
        BrimPlanner.Apply(tp, Settings());
        var moves = tp.Layers[0].Moves;
        Assert.True(moves.Count > 4);
        // Original square is intact at the tail.
        var tail = moves.Skip(moves.Count - 4).ToList();
        Assert.All(tail, m => Assert.Equal(MoveKind.Extrude, m.Kind));
        Assert.Equal(new Vector3(0, 0, 3f), tail[0].From);
        // Brim starts the layer.
        Assert.Equal(MoveKind.Extrude, moves[0].Kind);
    }

    [Fact]
    public void Loop_count_matches_setting()
    {
        var tp = SquareToolpath();
        BrimPlanner.Apply(tp, Settings(loops: 3));
        var brim = BrimMoves(tp, 4);
        // One travel between rings + final travel back to the part start = 3 travels for 3 rings.
        Assert.Equal(3, brim.Count(m => m.Kind == MoveKind.Travel));
    }

    [Fact]
    public void Loops_are_outside_the_part_and_ordered_outermost_first()
    {
        var tp = SquareToolpath();
        BrimPlanner.Apply(tp, Settings(loops: 2));
        var brim = BrimMoves(tp, 4).Where(m => m.Kind == MoveKind.Extrude).ToList();
        // Everything must be outside the footprint edge (part 0..100, edge at -5/105).
        Assert.All(brim, m =>
        {
            bool outside = m.To.X < -Bead * 0.4f || m.To.X > 100 + Bead * 0.4f
                        || m.To.Y < -Bead * 0.4f || m.To.Y > 100 + Bead * 0.4f;
            Assert.True(outside, $"brim point {m.To} not outside part");
        });
        // Outermost ring first: the first extrude is farther out than the last.
        static float Extent(Vector3 v) => MathF.Max(MathF.Abs(v.X - 50f), MathF.Abs(v.Y - 50f));
        Assert.True(Extent(brim[0].To) > Extent(brim[^1].To));
        // All at the first-layer Z.
        Assert.All(brim, m => Assert.Equal(3f, m.To.Z, 3));
    }

    [Fact]
    public void Brim_loops_are_simplified_no_sub_millimetre_segments()
    {
        // Round offset joins tessellate corners into many sub-mm points; the planner must
        // simplify so the robot isn't fed segments below its interpolation step (the cause
        // of the on-brim over-extrusion/jitter).
        var tp = SquareToolpath();
        BrimPlanner.Apply(tp, Settings(loops: 3));
        var brim = BrimMoves(tp, 4).Where(m => m.Kind == MoveKind.Extrude).ToList();
        Assert.NotEmpty(brim);
        Assert.All(brim, m =>
            Assert.True(Vector3.Distance(m.From, m.To) >= 0.25f,
                $"brim segment {Vector3.Distance(m.From, m.To):F3}mm is below the simplification floor"));
    }

    [Fact]
    public void Brim_encloses_first_layer_additions_like_x_bracing()
    {
        var tp = SquareToolpath();
        // Simulate an X-bracing detour protruding well outside the square on layer 0.
        tp.Layers[0].Moves.Add(new ToolpathMove(
            new Vector3(100, 50, 3f), new Vector3(160, 50, 3f), MoveKind.Extrude));
        BrimPlanner.Apply(tp, Settings(loops: 1));
        var brim = BrimMoves(tp, 5).Where(m => m.Kind == MoveKind.Extrude).ToList();
        Assert.NotEmpty(brim);
        // The single loop must reach beyond the protrusion tip (160) plus its half-bead.
        float maxX = brim.Max(m => m.To.X);
        Assert.True(maxX > 160f + Bead * 0.4f, $"brim maxX {maxX} does not enclose the protrusion");
    }
}
