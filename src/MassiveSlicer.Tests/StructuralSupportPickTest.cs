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
    /// The neck must attach to the SAME pocket corner on every layer. Previously the entry
    /// vertex was re-picked per layer from that layer's own split point, so on a wall that
    /// wanders in XY the neck hopped between corners — the rectangle appeared to move even
    /// though its footprint is fixed data.
    /// </summary>
    [Fact]
    public void Neck_attaches_to_the_same_pocket_corner_on_every_layer()
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
        // The entry vertex is the first outline point the neck reaches. Collect, per layer,
        // which outline vertex the detour starts at.
        var entries = new List<int>();
        var splitXs = new List<float>();
        foreach (var layer in tp.Layers)
        {
            foreach (var mv in layer.Moves)
            {
                if (mv.Kind != MoveKind.Extrude) continue;
                int idx = -1;
                for (int i = 0; i < outline.Length; i++)
                {
                    float dx = mv.To.X - outline[i].X, dy = mv.To.Y - outline[i].Y;
                    if (dx * dx + dy * dy < 0.01f) { idx = i; break; }
                }
                // First move whose END is an outline vertex and whose START is not = the neck.
                if (idx < 0) continue;
                bool startOnOutline = outline.Any(v =>
                    (mv.From.X - v.X) * (mv.From.X - v.X)
                    + (mv.From.Y - v.Y) * (mv.From.Y - v.Y) < 0.01f);
                if (startOnOutline) continue;
                entries.Add(idx);
                splitXs.Add(mv.From.X);   // where the neck leaves the wall
                break;
            }
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
            "neck must enter the same pocket corner on every layer; got entry vertices ["
            + string.Join(", ", entries) + "] across " + entries.Count + " layers");
    }

    /// <summary>
    /// Pins CURRENT behaviour, which is not necessarily desired: the neck out and the neck
    /// back run along the identical centreline, so with the bead deposited centred on the
    /// path the two passes overlap 100% rather than sitting side by side. If we offset the
    /// legs by half a bead each way, this test SHOULD fail — update it then, deliberately.
    /// </summary>
    [Fact]
    public void Neck_legs_currently_retrace_the_identical_centreline()
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

        Assert.True(pairs >= 1,
            "expected the neck out and neck back to be exact reverses of each other "
            + $"(current planner behaviour), found {pairs} retraced pair(s) in {moves.Count} moves");
    }
}
