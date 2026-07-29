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
