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

    private static SliceSettings Settings(
        bool enabled = true,
        int loops = 3,
        BrimDirection direction = BrimDirection.Outside) => new()
    {
        BeadWidth     = Bead,
        LayerHeight   = 3f,
        BrimEnabled   = enabled,
        BrimLoops     = loops,
        BrimDirection = direction,
    };

    /// <summary>
    /// A single straight bead — a footprint with no interior hole, so nothing for an inward
    /// brim to offset into.
    /// </summary>
    private static Toolpath SolidLineToolpath()
    {
        var layer = new ToolpathLayer(0, 3f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
        layer.Moves.Add(new ToolpathMove(
            new Vector3(0, 50, 3f), new Vector3(100, 50, 3f), MoveKind.Extrude));
        var tp = new Toolpath();
        tp.Layers.Add(layer);
        return tp;
    }

    // The 100x100 square WALL leaves a void from 5..95 (the outer edge sits at -5..105).
    // An inward loop k therefore lands (k - 1/2) beads inside 5..95: k=1 -> 10..90,
    // k=2 -> 20..80, k=3 -> 30..70.
    private const float HoleMin = 5f, HoleMax = 95f;

    private static bool InsideHole(Vector3 v) =>
        v.X > HoleMin + 1f && v.X < HoleMax - 1f && v.Y > HoleMin + 1f && v.Y < HoleMax - 1f;

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

    // ── Direction ────────────────────────────────────────────────────────────────

    [Fact]
    public void Default_direction_is_outward_and_puts_nothing_in_the_hole()
    {
        // Guards the pre-setting behaviour: a workspace or preset carrying no direction must
        // slice exactly as it did before inward brim existed.
        var tp = SquareToolpath();
        BrimPlanner.Apply(tp, Settings(loops: 3));
        var brim = BrimMoves(tp, 4).Where(m => m.Kind == MoveKind.Extrude).ToList();
        Assert.NotEmpty(brim);
        Assert.DoesNotContain(brim, m => InsideHole(m.To));
    }

    [Fact]
    public void Inward_puts_loops_in_the_hole_and_none_outside()
    {
        var tp = SquareToolpath();
        BrimPlanner.Apply(tp, Settings(loops: 3, direction: BrimDirection.Inside));
        var brim = BrimMoves(tp, 4).Where(m => m.Kind == MoveKind.Extrude).ToList();
        Assert.NotEmpty(brim);
        Assert.All(brim, m => Assert.True(InsideHole(m.To), $"inward brim point {m.To} is not in the hole"));
        Assert.All(brim, m => Assert.Equal(3f, m.To.Z, 3));
    }

    [Fact]
    public void Inward_loops_sit_half_a_bead_off_the_hole_edge()
    {
        // One loop only: its centreline must be half a bead inside the hole edge, so the bead
        // touches the part wall — the same rule the outward loops follow.
        var tp = SquareToolpath();
        BrimPlanner.Apply(tp, Settings(loops: 1, direction: BrimDirection.Inside));
        var brim = BrimMoves(tp, 4).Where(m => m.Kind == MoveKind.Extrude).ToList();
        Assert.NotEmpty(brim);
        Assert.Equal(HoleMin + Bead * 0.5f, brim.Min(m => m.To.X), 1);
        Assert.Equal(HoleMax - Bead * 0.5f, brim.Max(m => m.To.X), 1);
    }

    [Fact]
    public void Inward_loops_end_against_the_wall_so_the_last_one_fuses()
    {
        // Deepest first, working back out — mirroring outward's farthest-first ordering, so
        // whichever loop prints last is the one adjacent to the part.
        var tp = SquareToolpath();
        BrimPlanner.Apply(tp, Settings(loops: 3, direction: BrimDirection.Inside));
        var brim = BrimMoves(tp, 4).Where(m => m.Kind == MoveKind.Extrude).ToList();
        static float FromCentre(Vector3 v) => MathF.Max(MathF.Abs(v.X - 50f), MathF.Abs(v.Y - 50f));
        Assert.True(FromCentre(brim[0].To) < FromCentre(brim[^1].To),
            "inward brim should start deepest in the hole and finish against the wall");
    }

    [Fact]
    public void Both_covers_outside_and_inside()
    {
        var tp = SquareToolpath();
        BrimPlanner.Apply(tp, Settings(loops: 2, direction: BrimDirection.Both));
        var brim = BrimMoves(tp, 4).Where(m => m.Kind == MoveKind.Extrude).ToList();
        Assert.Contains(brim, m => InsideHole(m.To));
        Assert.Contains(brim, m => m.To.X < HoleMin - 1f || m.To.X > HoleMax + 1f);
    }

    [Fact]
    public void Both_groups_the_two_families_instead_of_interleaving_them()
    {
        // Interleaving outward and inward per offset would drag the head over the part once
        // per loop, and every within-layer travel is a dead stop the screw pumps through.
        // Grouped, the run crosses the wall exactly once.
        var tp = SquareToolpath();
        BrimPlanner.Apply(tp, Settings(loops: 3, direction: BrimDirection.Both));
        var brim = BrimMoves(tp, 4).Where(m => m.Kind == MoveKind.Extrude).ToList();
        int crossings = 0;
        for (int i = 1; i < brim.Count; i++)
            if (InsideHole(brim[i].To) != InsideHole(brim[i - 1].To)) crossings++;
        Assert.Equal(1, crossings);
    }

    /// <summary>
    /// A 300x300 square wall with a second wall inside it, leaving a sealed gap between them —
    /// the shape of a real first layer, where the trapped air between wall passes is "inside".
    /// </summary>
    private static Toolpath TwoWallToolpath()
    {
        var layer = new ToolpathLayer(0, 3f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
        void Loop(float lo, float hi)
        {
            Vector3 P(float x, float y) => new(x, y, 3f);
            layer.Moves.Add(new ToolpathMove(P(lo, lo), P(hi, lo), MoveKind.Extrude));
            layer.Moves.Add(new ToolpathMove(P(hi, lo), P(hi, hi), MoveKind.Extrude));
            layer.Moves.Add(new ToolpathMove(P(hi, hi), P(lo, hi), MoveKind.Extrude));
            layer.Moves.Add(new ToolpathMove(P(lo, hi), P(lo, lo), MoveKind.Extrude));
        }
        Loop(0f, 300f);
        Loop(100f, 200f);
        var tp = new Toolpath();
        tp.Layers.Add(layer);
        return tp;
    }

    [Fact]
    public void Sealed_air_between_wall_passes_is_inside()
    {
        // Air flood-filling from infinity reaches the outside of the 300-square and the middle of
        // the 100..200 square, but NOT the ring of trapped space between them. That trapped ring
        // is what Inside means.
        var tp = TwoWallToolpath();
        BrimPlanner.Apply(tp, Settings(loops: 1, direction: BrimDirection.Inside));
        var brim = BrimMoves(tp, 8).Where(m => m.Kind == MoveKind.Extrude).ToList();
        Assert.NotEmpty(brim);
        // Nothing may sit outside the outer wall — that is air-reachable, hence Outside.
        Assert.All(brim, m => Assert.True(
            m.To.X > -1f && m.To.X < 301f && m.To.Y > -1f && m.To.Y < 301f,
            $"inside loop at {m.To} escaped to air-reachable space"));
    }

    [Fact]
    public void Both_is_outside_plus_inside()
    {
        int Count(BrimDirection d)
        {
            var tp = TwoWallToolpath();
            BrimPlanner.Apply(tp, Settings(loops: 2, direction: d));
            return BrimMoves(tp, 8).Count(m => m.Kind == MoveKind.Extrude);
        }
        int outside = Count(BrimDirection.Outside);
        int inside  = Count(BrimDirection.Inside);
        int both    = Count(BrimDirection.Both);
        Assert.True(outside > 0 && inside > 0, $"outside={outside} inside={inside}");
        // The bug this pins: Both came back byte-identical to Outside on a real capital.
        Assert.Equal(outside + inside, both);
    }

    [Fact]
    public void Slivers_too_small_to_hold_a_bead_produce_no_loop()
    {
        // A real first layer throws off hundreds of near-zero pockets where beads meet — 287 of
        // 300 on the capital were under one bead square. Inflating the material closes them, so
        // they drop out with no size test. This pins that no size test is NEEDED.
        var layer = new ToolpathLayer(0, 3f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
        Vector3 P(float x, float y) => new(x, y, 3f);
        // Two beads a hair apart: the gap between them seals but is far thinner than a bead.
        layer.Moves.Add(new ToolpathMove(P(0, 0),   P(200, 0),   MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(P(200, 0), P(200, 2),   MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(P(200, 2), P(0, 2),     MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(P(0, 2),   P(0, 0),     MoveKind.Extrude));
        var tp = new Toolpath();
        tp.Layers.Add(layer);
        BrimPlanner.Apply(tp, Settings(loops: 1, direction: BrimDirection.Inside));
        Assert.Equal(4, tp.Layers[0].Moves.Count);   // nothing added
    }

    [Fact]
    public void Inward_loses_its_deeper_loops_in_a_hole_too_narrow_to_hold_them()
    {
        // A hole only ~30mm across cannot hold a loop 25mm in from both edges: Clipper closes
        // that ring off and it simply is not emitted. No size test in the planner does this.
        var layer = new ToolpathLayer(0, 3f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
        Vector3 P(float x, float y) => new(x, y, 3f);
        layer.Moves.Add(new ToolpathMove(P(0, 0),   P(40, 0),  MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(P(40, 0),  P(40, 40), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(P(40, 40), P(0, 40),  MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(P(0, 40),  P(0, 0),   MoveKind.Extrude));
        var tp = new Toolpath();
        tp.Layers.Add(layer);

        BrimPlanner.Apply(tp, Settings(loops: 3, direction: BrimDirection.Inside));
        var brim = BrimMoves(tp, 4).Where(m => m.Kind == MoveKind.Extrude).ToList();
        // The k=1 ring (5mm into a 5..35 void) fits; k=3 (25mm in) cannot.
        static float FromCentre(Vector3 v) => MathF.Max(MathF.Abs(v.X - 20f), MathF.Abs(v.Y - 20f));
        Assert.NotEmpty(brim);
        Assert.All(brim, m => Assert.True(FromCentre(m.To) > 2f,
            $"a loop at {m.To} is deeper than a 30mm void can hold"));
    }
}
