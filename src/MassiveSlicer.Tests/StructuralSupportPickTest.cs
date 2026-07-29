using System.Numerics;
using MassiveSlicer.Core.Models;
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
}
