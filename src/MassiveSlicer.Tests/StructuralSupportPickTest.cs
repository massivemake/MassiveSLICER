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
        // Short wall segments that SLIDE ALONG X as the stack rises. The split point is
        // clamped to each segment, so it sweeps from x~40 to x~340 — crossing the pocket's
        // midline, which is what makes the nearest-corner choice actually flip. (A wall
        // that only shifts in Y keeps the two near corners equidistant and the test would
        // pass without the fix.)
        var tp = new Toolpath();
        for (int li = 0; li < 6; li++)
        {
            float z = li * 3f;
            float x0 = li * 60f;
            var layer = new ToolpathLayer(li, z) { PlaneNormal = Vector3.UnitZ, Height = 3f };
            layer.Moves.Add(new ToolpathMove(
                new Vector3(x0, 0f, z), new Vector3(x0 + 40f, 0f, z), MoveKind.Extrude));
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
        // Per layer, find the outgoing leg (exactly one endpoint on the wall line y≈0) and
        // record BOTH where it leaves the wall and where it lands on the pocket. The pocket
        // landing point must be identical on every layer; the wall end is free to move.
        // Per layer, collect BOTH legs' pocket-side endpoints as an order-independent set,
        // plus where the legs leave the wall.
        var entries = new List<string>();
        var splitXs = new List<float>();
        foreach (var layer in tp.Layers)
        {
            var pocketEnds = new List<string>();
            foreach (var mv in layer.Moves)
            {
                if (mv.Kind != MoveKind.Extrude) continue;
                bool fromWall = MathF.Abs(mv.From.Y) < 0.01f;
                bool toWall   = MathF.Abs(mv.To.Y) < 0.01f;
                if (fromWall == toWall) continue;          // wall piece or pocket edge
                var wallEnd   = fromWall ? mv.From : mv.To;
                var pocketEnd = fromWall ? mv.To : mv.From;
                pocketEnds.Add($"{pocketEnd.X:0.##},{pocketEnd.Y:0.##}");
                splitXs.Add(wallEnd.X);
            }
            if (pocketEnds.Count == 0) continue;
            pocketEnds.Sort();                             // order-independent
            entries.Add(string.Join(" & ", pocketEnds));
        }

        Assert.Equal(tp.Layers.Count, entries.Count);
        // Guard against a vacuous pass: the wall split point MUST actually move across
        // layers, otherwise the corner choice was never under pressure to change.
        Assert.True(splitXs.Max() - splitXs.Min() > spec.WidthMm,
            "test geometry is not exercising the bug — wall split point only moved "
            + $"{splitXs.Max() - splitXs.Min():0.#} mm, needs to exceed the pocket width "
            + $"({spec.WidthMm:0.#} mm) to make the nearest corner flip");
        // Second vacuity guard, and the real point of the test: replay the OLD rule
        // (nearest outline vertex to THIS layer's split point). It must produce more than
        // one distinct corner on this geometry — that is exactly the bug being fixed. If
        // this ever collapses to one corner, the scenario has stopped covering the
        // regression and the assertion below would be meaningless.
        var oldRuleEntries = splitXs.Select(sx =>
        {
            int best = 0;
            float bestD2 = float.MaxValue;
            for (int i = 0; i < outline.Length; i++)
            {
                float dx = outline[i].X - sx, dy = outline[i].Y - 0f;
                float d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; best = i; }
            }
            return best;
        }).ToList();
        Assert.True(oldRuleEntries.Distinct().Count() > 1,
            "vacuous test: the pre-fix per-layer rule would have picked a single corner "
            + $"anyway ([{string.Join(", ", oldRuleEntries)}]) — geometry needs to straddle "
            + "the pocket midline");

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
