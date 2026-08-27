using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// The arm's mouths — where its two legs actually land on the pocket. The viewport preview
/// used to draw a line from the anchor to the pocket's NEAREST OUTLINE VERTEX, which is not
/// where the duct attaches, so it pointed at a corner the printer never visits. These pin the
/// shared math the preview and the builder now both use.
/// </summary>
public sealed class StructuralSupportArmMouthTest
{
    // Anchor below the pocket, arm pointing +Y at a 92 x 42 rectangle centred (100, 50).
    // Near edge is y = 29, corners at x = 54 and x = 146.
    static StructuralSupportSpec Spec(float rot = 0f) => new()
    {
        Shape = SupportShapeKind.Rectangle,
        AnchorX = 100, AnchorY = 0, AnchorLayer = 0,
        CenterX = 100, CenterY = 50, WidthMm = 92, DepthMm = 42, RotationDeg = rot,
    };

    [Fact]
    public void Legs_land_on_the_near_edge_not_on_a_corner()
    {
        Assert.True(StructuralSupportPlanner.TryArmMouths(
            Spec(), 6f, new Vector2(100, 0),
            out var legA, out var mouthA, out var legB, out var mouthB));

        // Straight shot at the centre: both mouths sit on the near edge y = 29.
        Assert.Equal(29f, mouthA.Y, 2);
        Assert.Equal(29f, mouthB.Y, 2);

        // And nowhere near either corner (x = 54 / 146) — they straddle the axis at x = 100.
        // Leg A is the +perp side, and perp = (-u.Y, u.X) = (-1, 0) for an arm pointing +Y.
        Assert.Equal(97f, mouthA.X, 2);
        Assert.Equal(103f, mouthB.X, 2);

        // Legs start half a bead either side of the anchor and run parallel.
        Assert.Equal(6f, Vector2.Distance(legA, legB), 2);
        Assert.Equal(6f, Vector2.Distance(mouthA, mouthB), 2);
    }

    [Fact]
    public void The_old_nearest_vertex_guess_would_have_pointed_somewhere_else()
    {
        // Vacuity guard: proves the two answers genuinely differ, so the fix is not cosmetic.
        var spec = Spec();
        var outline = spec.BuildOutline();
        var anchor = new Vector2(100, 0);

        int near = 0; float nd = float.MaxValue;
        for (int i = 0; i < outline.Length; i++)
        {
            float d2 = Vector2.DistanceSquared(outline[i], anchor);
            if (d2 < nd) { nd = d2; near = i; }
        }

        Assert.True(StructuralSupportPlanner.TryArmMouths(
            spec, 6f, anchor, out _, out var mouthA, out _, out _));

        // The nearest corner is ~46 mm off in X from where the leg actually lands.
        Assert.True(Vector2.Distance(outline[near], mouthA) > 40f,
            $"nearest vertex {outline[near]} vs real mouth {mouthA}");
    }

    [Fact]
    public void A_rotated_pocket_still_gets_mouths_on_the_face_the_arm_meets()
    {
        // Rotated 30 degrees, the arm hits an oblique face. Both legs must still land ON the
        // outline (that obliqueness is what makes the turn sharp — Jeff's separate note).
        var spec = Spec(rot: 30f);
        Assert.True(StructuralSupportPlanner.TryArmMouths(
            spec, 6f, new Vector2(100, 0),
            out _, out var mouthA, out _, out var mouthB));

        Assert.True(DistToOutline(spec.BuildOutline(), mouthA) < 0.01f);
        Assert.True(DistToOutline(spec.BuildOutline(), mouthB) < 0.01f);
        // Still one bead apart across the arm, not collapsed onto one point.
        Assert.True(Vector2.Distance(mouthA, mouthB) > 5f);
    }

    [Fact]
    public void An_anchor_sitting_on_the_pocket_centre_has_no_arm_direction()
    {
        var spec = Spec();
        spec = spec with { AnchorX = spec.CenterX, AnchorY = spec.CenterY };
        Assert.False(StructuralSupportPlanner.TryArmMouths(
            spec, 6f, new Vector2(spec.CenterX, spec.CenterY),
            out _, out _, out _, out _));
    }

    static float DistToOutline(Vector2[] poly, Vector2 p)
    {
        float best = float.MaxValue;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[j]; var b = poly[i];
            var ab = b - a;
            float len2 = ab.LengthSquared();
            float t = len2 < 1e-12f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
            best = MathF.Min(best, Vector2.Distance(p, a + ab * t));
        }
        return best;
    }
}
