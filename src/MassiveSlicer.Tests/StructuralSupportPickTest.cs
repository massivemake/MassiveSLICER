using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Covers the geometry behind clicking a Structural Support pocket in the viewport
/// (<see cref="StructuralSupportSpec.ContainsPoint"/>) — the part that decides whether a
/// click grabs a support or falls through to a bead pick.
/// </summary>
public sealed class StructuralSupportPickTest
{
    static StructuralSupportSpec Pocket(
        float cx = 100f, float cy = 50f, float w = 92f, float d = 42f, float rot = 0f) =>
        new()
        {
            AnchorX = cx, AnchorY = cy - d * 0.5f - 12f, AnchorLayer = 0,
            CenterX = cx, CenterY = cy,
            WidthMm = w, DepthMm = d, RotationDeg = rot,
        };

    [Fact]
    public void Centre_of_a_2x4_pocket_is_a_hit()
    {
        Assert.True(Pocket().ContainsPoint(new Vector2(100f, 50f)));
    }

    [Fact]
    public void Just_outside_each_edge_is_a_miss()
    {
        var p = Pocket();                       // 92 x 42 at (100, 50), unrotated
        Assert.False(p.ContainsPoint(new Vector2(100f + 47f, 50f)));   // past +X (hw = 46)
        Assert.False(p.ContainsPoint(new Vector2(100f - 47f, 50f)));   // past -X
        Assert.False(p.ContainsPoint(new Vector2(100f, 50f + 22f)));   // past +Y (hd = 21)
        Assert.False(p.ContainsPoint(new Vector2(100f, 50f - 22f)));   // past -Y
    }

    [Fact]
    public void Just_inside_each_edge_is_a_hit()
    {
        var p = Pocket();
        Assert.True(p.ContainsPoint(new Vector2(100f + 45f, 50f)));
        Assert.True(p.ContainsPoint(new Vector2(100f, 50f + 20f)));
    }

    [Fact]
    public void Rotation_moves_the_footprint_with_it()
    {
        // Rotated 90 degrees, the long axis is now Y: a point 40 mm out along X was
        // inside before (hw = 46) and must now be outside (hd = 21 in that direction).
        var flat = Pocket();
        var turned = Pocket(rot: 90f);
        var probe = new Vector2(100f + 40f, 50f);
        Assert.True(flat.ContainsPoint(probe));
        Assert.False(turned.ContainsPoint(probe));
        Assert.True(turned.ContainsPoint(new Vector2(100f, 50f + 40f)));
    }

    [Fact]
    public void Circle_pocket_uses_the_diameter_not_the_bounding_box()
    {
        var c = Pocket(w: 100f) with { Shape = SupportShapeKind.Circle };
        Assert.True(c.ContainsPoint(new Vector2(100f + 49f, 50f)));    // inside r = 50
        Assert.False(c.ContainsPoint(new Vector2(100f + 51f, 50f)));   // outside r
        // The bounding-box corner is outside a circle — a square test would pass this.
        Assert.False(c.ContainsPoint(new Vector2(100f + 45f, 50f + 45f)));
    }

    [Fact]
    public void Anchor_is_not_part_of_the_pick_target()
    {
        // The anchor sits on the wall; picking it would steal bead clicks in edit mode.
        var p = Pocket();
        Assert.False(p.ContainsPoint(new Vector2(p.AnchorX, p.AnchorY)));
    }

    /// <summary>
    /// The arm must meet the pocket at the SAME two mouth points on every layer. Originally
    /// the attachment was re-picked per layer from that layer's own split point, so on a
    /// wall that wanders in XY it hopped between corners and the rectangle looked like it
    /// was moving.
    /// <para>
    /// Note this asserts the mouth POINT SET, not which leg is outgoing. Pinning the
    /// traversal direction as well was over-strict and is what made the legs cross — the
    /// direction has to adapt per layer because it is a non-crossing test and the wall
    /// rotates as the stack rises. Same deposited geometry either way.
    /// </para>
    /// </summary>
    [Fact]
    public void Arm_meets_the_pocket_at_the_same_two_points_on_every_layer()
    {
        // A wall that always passes THROUGH the anchor (so the reach gate never terminates
        // it) but ROTATES as the stack rises. Rotation is what drives the per-layer
        // non-crossing decision, so this is the geometry that would flip the leg pairing.
        var tp = new Toolpath();
        for (int li = 0; li < 6; li++)
        {
            float z = li * 3f;
            float a = (-60f + li * 24f) * MathF.PI / 180f;   // -60° … +60°
            float dx = MathF.Cos(a) * 100f, dy = MathF.Sin(a) * 100f;
            var layer = new ToolpathLayer(li, z) { PlaneNormal = Vector3.UnitZ, Height = 3f };
            layer.Moves.Add(new ToolpathMove(
                new Vector3(200f - dx, 0f - dy, z), new Vector3(200f + dx, 0f + dy, z),
                MoveKind.Extrude));
            tp.Layers.Add(layer);
        }

        var spec = new StructuralSupportSpec
        {
            AnchorX = 200f, AnchorY = 0f, AnchorLayer = 0,
            CenterX = 200f, CenterY = 100f,
            WidthMm = 92f, DepthMm = 42f,
            LayersUp = 9999, LayersDown = 0,
        };
        StructuralSupportPlanner.Apply(tp, new SliceSettings { StructuralSupports = [spec] });

        var outline = spec.BuildOutline();
        // A leg is a move with exactly one endpoint on the pocket outline. Collect both
        // legs' pocket-side endpoints per layer as an order-independent set, plus where
        // each leg leaves the wall.
        // Distance to the outline's EDGES, not its vertices — the mouth points sit mid-edge,
        // so a vertex-proximity test misclassifies them and picks up wrap moves as legs.
        static float DistToEdges(Vector2[] poly, Vector3 p)
        {
            float best = float.MaxValue;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var a = poly[j];
                var b = poly[i];
                var ab = b - a;
                float len2 = ab.LengthSquared();
                float t = len2 < 1e-12f
                    ? 0f
                    : Math.Clamp(Vector2.Dot(new Vector2(p.X, p.Y) - a, ab) / len2, 0f, 1f);
                var c = a + ab * t;
                float d = Vector2.Distance(c, new Vector2(p.X, p.Y));
                if (d < best) best = d;
            }
            return best;
        }
        bool OnOutline(Vector3 p) => DistToEdges(outline, p) < 1f;
        var entries = new List<string>();
        var wallRoots = new List<Vector3>();
        foreach (var layer in tp.Layers)
        {
            var pocketEnds = new List<string>();
            foreach (var mv in layer.Moves)
            {
                if (mv.Kind != MoveKind.Extrude) continue;
                bool fromPocket = OnOutline(mv.From);
                bool toPocket   = OnOutline(mv.To);
                if (fromPocket == toPocket) continue;      // wall piece or pocket edge
                pocketEnds.Add(fromPocket
                    ? $"{mv.From.X:0.##},{mv.From.Y:0.##}"
                    : $"{mv.To.X:0.##},{mv.To.Y:0.##}");
                wallRoots.Add(fromPocket ? mv.To : mv.From);
            }
            if (pocketEnds.Count == 0) continue;
            pocketEnds.Sort();                             // order-independent
            entries.Add(string.Join(" & ", pocketEnds));
        }

        Assert.Equal(tp.Layers.Count, entries.Count);
        // Vacuity guard: the wall must actually have rotated, or the per-layer direction
        // decision was never under pressure and this proves nothing.
        float rootSpread = wallRoots.Max(p => p.Y) - wallRoots.Min(p => p.Y);
        Assert.True(rootSpread > 1f,
            "test geometry isn't exercising wall rotation — the leg roots only spread "
            + $"{rootSpread:0.##} mm in Y");

        Assert.True(entries.Distinct().Count() == 1,
            "arm must land on the same point of the pocket on every layer; got ["
            + string.Join(" | ", entries) + "] across " + entries.Count + " layers");
    }

    /// <summary>
    /// The neck legs must NOT retrace one centreline any more — they are a duct, offset so
    /// the deposited beads touch without overlapping. This replaces the old test that
    /// pinned the retracing behaviour.
    /// </summary>
    [Fact]
    public void Neck_legs_do_not_retrace_the_same_centreline()
    {
        // One straight wall run at Z=0 so the planner has something to split.
        var layer = new ToolpathLayer(0, 0f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
        layer.Moves.Add(new ToolpathMove(
            new Vector3(0f, 0f, 0f), new Vector3(400f, 0f, 0f), MoveKind.Extrude));
        var tp = new Toolpath();
        tp.Layers.Add(layer);

        var spec = new StructuralSupportSpec
        {
            AnchorX = 200f, AnchorY = 0f, AnchorLayer = 0,
            CenterX = 200f, CenterY = 80f,          // pocket sits off the wall in +Y
            WidthMm = 92f, DepthMm = 42f,
            LayersUp = 0, LayersDown = 0,
        };
        StructuralSupportPlanner.Apply(tp, new SliceSettings { StructuralSupports = [spec] });

        var moves = layer.Moves;
        // Find a pair of extrude moves that run exactly back along one another.
        int pairs = 0;
        for (int i = 0; i < moves.Count; i++)
        {
            if (moves[i].Kind != MoveKind.Extrude) continue;
            if (Vector3.Distance(moves[i].From, moves[i].To) < 1f) continue;
            for (int j = i + 1; j < moves.Count; j++)
            {
                if (moves[j].Kind != MoveKind.Extrude) continue;
                if (Vector3.Distance(moves[i].From, moves[j].To) > 0.01f) continue;
                if (Vector3.Distance(moves[i].To, moves[j].From) > 0.01f) continue;
                pairs++;
                break;
            }
        }

        Assert.Equal(0, pairs);
    }

    /// <summary>
    /// A support placed at the END of an open wall run still gets a full one-bead mouth.
    /// The anchor has no wall on one side, so half-from-each-side produced a half-bead
    /// opening with the two legs overlapping — the mangled arm seen on end-placed supports.
    /// The mouth must shift inboard rather than shrink.
    /// </summary>
    [Fact]
    public void Mouth_at_an_open_path_end_is_still_a_full_bead()
    {
        const float bead = 6f;
        var layer = new ToolpathLayer(0, 0f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
        // Open run ending at x = 400. Anchor sits exactly on that endpoint.
        layer.Moves.Add(new ToolpathMove(
            new Vector3(0f, 0f, 0f), new Vector3(400f, 0f, 0f), MoveKind.Extrude));
        var tp = new Toolpath();
        tp.Layers.Add(layer);

        var spec = new StructuralSupportSpec
        {
            AnchorX = 400f, AnchorY = 0f, AnchorLayer = 0,
            CenterX = 400f, CenterY = 80f,
            WidthMm = 92f, DepthMm = 42f,
            LayersUp = 0, LayersDown = 0,
        };
        StructuralSupportPlanner.Apply(tp, new SliceSettings
        {
            BeadWidth = bead,
            StructuralSupports = [spec],
        });

        var ex = layer.Moves.Where(m => m.Kind == MoveKind.Extrude).ToList();
        static bool OnWall(Vector3 p) => MathF.Abs(p.Y) < 0.01f;
        var legs = ex.Where(m => OnWall(m.From) ^ OnWall(m.To)).ToList();
        Assert.Equal(2, legs.Count);

        float r0 = OnWall(legs[0].From) ? legs[0].From.X : legs[0].To.X;
        float r1 = OnWall(legs[1].From) ? legs[1].From.X : legs[1].To.X;
        float mouth = MathF.Abs(r0 - r1);
        Assert.True(MathF.Abs(mouth - bead) < 0.51f,
            $"mouth at an open end should still be ~{bead} mm, measured {mouth:0.###} mm");
        // Both roots must lie ON the run, not past its end.
        Assert.True(MathF.Max(r0, r1) <= 400.01f,
            $"a leg root ran past the end of the wall (x={MathF.Max(r0, r1):0.##})");
    }

    /// <summary>
    /// The arm ends where the wall stops passing through the break, and does NOT come back
    /// if the wall returns higher up — a column can't restart in mid-air. Models a filleted
    /// top: the wall is in reach for a few layers, recedes far away, then comes back.
    /// </summary>
    [Fact]
    public void Arm_terminates_when_the_wall_recedes_and_never_resumes()
    {
        const float bead = 6f;
        var tp = new Toolpath();
        for (int li = 0; li < 12; li++)
        {
            // In reach on layers 0-3 and again on 8-11; far away on 4-7.
            float y = li is >= 4 and <= 7 ? 400f : 0f;
            var layer = new ToolpathLayer(li, li * 3f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
            layer.Moves.Add(new ToolpathMove(
                new Vector3(0f, y, li * 3f), new Vector3(400f, y, li * 3f), MoveKind.Extrude));
            tp.Layers.Add(layer);
        }

        var spec = new StructuralSupportSpec
        {
            AnchorX = 200f, AnchorY = 0f, AnchorLayer = 0,
            CenterX = 200f, CenterY = 80f,
            WidthMm = 92f, DepthMm = 42f,
            LayersUp = 9999, LayersDown = 0,
        };
        StructuralSupportPlanner.Apply(tp, new SliceSettings
        {
            BeadWidth = bead,
            StructuralSupports = [spec],
        });

        // A layer "has the pocket" if any extrude endpoint lands on an outline VERTEX (the
        // wrap visits them all). Testing "y past the pocket centre" would false-positive on
        // the receded wall itself, which sits at y = 400.
        var outlineV = spec.BuildOutline();
        bool HasPocket(int li) => tp.Layers[li].Moves.Any(m =>
            m.Kind == MoveKind.Extrude
            && outlineV.Any(v =>
                (MathF.Abs(m.To.X - v.X) < 1f && MathF.Abs(m.To.Y - v.Y) < 1f)
                || (MathF.Abs(m.From.X - v.X) < 1f && MathF.Abs(m.From.Y - v.Y) < 1f)));

        for (int li = 0; li <= 3; li++)
            Assert.True(HasPocket(li), $"L{li + 1} is in reach and should carry the pocket");
        for (int li = 4; li <= 7; li++)
            Assert.False(HasPocket(li), $"L{li + 1} is out of reach and must NOT carry it");
        // The wall returns here, but the arm was already terminated below.
        for (int li = 8; li <= 11; li++)
            Assert.False(HasPocket(li),
                $"L{li + 1} must stay empty — the arm ended at L5 and cannot restart in mid-air");
    }

    /// <summary>
    /// The three gaps Jeff specified: a break in the WALL at the anchor, a gap ALONG the
    /// arm (the two legs one bead apart, not stacked), and a break in the RECTANGLE's own
    /// surface where the arm meets it. All three are one bead width, because the bead is
    /// deposited centred on the path — so touching-but-not-overlapping means the two
    /// centrelines sit a full bead apart.
    /// </summary>
    [Fact]
    public void Wall_arm_and_pocket_each_get_a_one_bead_gap()
    {
        const float bead = 6f;
        var layer = new ToolpathLayer(0, 0f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
        layer.Moves.Add(new ToolpathMove(
            new Vector3(0f, 0f, 0f), new Vector3(400f, 0f, 0f), MoveKind.Extrude));
        var tp = new Toolpath();
        tp.Layers.Add(layer);

        var spec = new StructuralSupportSpec
        {
            AnchorX = 200f, AnchorY = 0f, AnchorLayer = 0,
            CenterX = 200f, CenterY = 80f,
            WidthMm = 92f, DepthMm = 42f,
            LayersUp = 0, LayersDown = 0,
        };
        StructuralSupportPlanner.Apply(tp, new SliceSettings
        {
            BeadWidth = bead,
            StructuralSupports = [spec],
        });

        var extrudes = layer.Moves.Where(m => m.Kind == MoveKind.Extrude).ToList();
        // Classify by how many endpoints sit on the wall line (y ≈ 0). A pocket edge that
        // happens to run in Y is NOT a leg — the earlier naive "big ΔY" filter caught the
        // rectangle's own side and reported three legs.
        static bool OnWall(Vector3 p) => MathF.Abs(p.Y) < 0.01f;
        var wallPieces = extrudes.Where(m => OnWall(m.From) && OnWall(m.To)).ToList();
        var legs       = extrudes.Where(m => OnWall(m.From) ^ OnWall(m.To)).ToList();
        var pocket     = extrudes.Where(m => !OnWall(m.From) && !OnWall(m.To)).ToList();

        // ── Gap 1: the WALL is broken at the anchor ──────────────────────────────────
        Assert.Equal(2, wallPieces.Count);
        float innerLeft  = wallPieces.SelectMany(m => new[] { m.From.X, m.To.X })
                                     .Where(x => x < spec.AnchorX).Max();
        float innerRight = wallPieces.SelectMany(m => new[] { m.From.X, m.To.X })
                                     .Where(x => x > spec.AnchorX).Min();
        float wallGap = innerRight - innerLeft;
        Assert.True(MathF.Abs(wallGap - bead) < 0.51f,
            $"wall break should be ~{bead} mm, measured {wallGap:0.###} mm");
        // ...and centred on the anchor, so the mouth doesn't creep along the wall.
        Assert.Equal(spec.AnchorX, (innerLeft + innerRight) * 0.5f, 1);

        // ── Gap 2: the two legs run a bead apart, not stacked ────────────────────────
        Assert.Equal(2, legs.Count);
        float leg0X = OnWall(legs[0].From) ? legs[0].From.X : legs[0].To.X;
        float leg1X = OnWall(legs[1].From) ? legs[1].From.X : legs[1].To.X;
        float legSeparation = MathF.Abs(leg0X - leg1X);
        Assert.True(MathF.Abs(legSeparation - bead) < 0.51f,
            $"the two arm legs should be ~{bead} mm apart (touching, not overlapping), "
            + $"measured {legSeparation:0.###} mm");

        // ── Gap 3: the RECTANGLE's own surface is broken where the arm meets it ──────
        // Take the wrap's open ends in PATH order: where the first pocket move starts and
        // where the last one ends. A closed loop would put these at the same point.
        Assert.NotEmpty(pocket);
        float pocketGap = Vector3.Distance(pocket[0].From, pocket[^1].To);
        Assert.True(pocketGap > 0.5f,
            "the rectangle's surface must be broken where the arm meets it — the wrap "
            + $"start and end are only {pocketGap:0.###} mm apart (still a closed loop)");
    }
}
