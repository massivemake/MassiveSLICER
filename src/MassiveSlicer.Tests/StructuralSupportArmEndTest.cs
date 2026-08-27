using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Where the support ARM stops as the wall recedes out from under it.
/// <para>
/// The arm used to be built on the last layer that produced any attachment at all —
/// including one where <c>TryWallHit</c> fell back to clamping a leg onto the end of a wall
/// run instead of crossing it. That clamped top layer is a degenerate join: it doubles back
/// on itself rather than stepping up cleanly, and it is what Jeff saw on 2 of 8 supports.
/// Whether the top layer landed clean or clamped came down to where the layer planes
/// happened to fall against the receding surface, which is why it looked random.
/// </para>
/// <para>
/// The arm now ends on the last layer whose legs genuinely CROSS the wall.
/// </para>
/// </summary>
public sealed class StructuralSupportArmEndTest
{
    /// <summary>Wall along X at y=0, one move per layer, ending at the given X each layer.</summary>
    static Toolpath RecedingWall(params float[] endX)
    {
        var tp = new Toolpath();
        for (int li = 0; li < endX.Length; li++)
        {
            var layer = new ToolpathLayer(li, li * 3f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
            layer.Moves.Add(new ToolpathMove(
                new Vector3(0, 0, li * 3f), new Vector3(endX[li], 0, li * 3f), MoveKind.Extrude));
            tp.Layers.Add(layer);
        }
        return tp;
    }

    // Anchor (150,0), pocket centred (150,80) 92x42 — so the arm axis points +Y, the two legs
    // are the vertical lines x=147 and x=153, and the pocket's far edge is y=101.
    static StructuralSupportSpec Spec() => new()
    {
        Shape = SupportShapeKind.Rectangle,
        AnchorX = 150, AnchorY = 0, AnchorLayer = 0,
        LayersUp = 9999, LayersDown = 0,
        CenterX = 150, CenterY = 80, WidthMm = 92, DepthMm = 42,
    };

    /// <summary>A layer carries the duct when something reaches the pocket's far edge.</summary>
    static bool CarriesDuct(ToolpathLayer l) =>
        l.Moves.Count > 0 && l.Moves.Max(m => MathF.Max(m.From.Y, m.To.Y)) > 100f;

    static int DuctLayers(Toolpath tp)
    {
        int n = 0;
        foreach (var l in tp.Layers) if (CarriesDuct(l)) n++;
        return n;
    }

    [Fact]
    public void Arm_stops_below_a_clamped_attachment_even_though_it_could_still_attach()
    {
        // Layers 0-3: wall runs to x=200, so BOTH legs (x=147 and x=153) cross it — clean.
        // Layers 4-7: wall stops at x=151. Leg x=147 still crosses, but leg x=153 has no
        // crossing and gets clamped to the run end (|151-153| = 2 mm, inside the one-bead
        // rescue). So every upper layer CAN still attach — nothing here hard-fails. That is
        // what makes this test meaningful: the arm must stop for the clamp, not because it
        // ran out of wall.
        var tp = RecedingWall(200, 200, 200, 200, 151, 151, 151, 151);
        StructuralSupportPlanner.Apply(tp, new SliceSettings { StructuralSupports = [Spec()] });

        Assert.True(CarriesDuct(tp.Layers[0]), "L1 should carry the duct");
        Assert.True(CarriesDuct(tp.Layers[3]), "L4 is the last cleanly-crossed layer");
        Assert.False(CarriesDuct(tp.Layers[4]), "L5 is clamped — the arm must not end here");
        Assert.False(CarriesDuct(tp.Layers[7]), "nothing above the clean top");
        Assert.Equal(4, DuctLayers(tp));
    }

    [Fact]
    public void A_wall_that_never_recedes_still_builds_all_the_way_up()
    {
        // Vacuity guard: if the new rule truncated everything, the test above would pass
        // for the wrong reason.
        var tp = RecedingWall(200, 200, 200, 200, 200, 200);
        StructuralSupportPlanner.Apply(tp, new SliceSettings { StructuralSupports = [Spec()] });

        Assert.Equal(6, DuctLayers(tp));
    }

    [Fact]
    public void A_real_recede_ends_the_arm_before_the_wall_runs_out()
    {
        // The realistic shape: clean, then a couple of clamped layers, then no wall in reach.
        // x=140 puts the run end 7 mm off the near leg — outside the one-bead rescue.
        var tp = RecedingWall(200, 200, 200, 151, 149, 140);
        StructuralSupportPlanner.Apply(tp, new SliceSettings { StructuralSupports = [Spec()] });

        Assert.Equal(3, DuctLayers(tp));
        Assert.True(CarriesDuct(tp.Layers[2]));
        Assert.False(CarriesDuct(tp.Layers[3]));
    }

    [Fact]
    public void A_dip_does_not_let_the_arm_resume_above_it()
    {
        // Wall vanishes for two layers then comes back. Resuming would leave a hole in the
        // pocket column, which is worse than a short column — so the arm stays stopped below
        // the dip. The planner logs that it truncated at a dip rather than at the real end.
        var tp = RecedingWall(200, 200, 140, 140, 200, 200);
        StructuralSupportPlanner.Apply(tp, new SliceSettings { StructuralSupports = [Spec()] });

        Assert.True(CarriesDuct(tp.Layers[1]), "builds up to the dip");
        Assert.False(CarriesDuct(tp.Layers[2]), "no wall in reach");
        Assert.False(CarriesDuct(tp.Layers[4]), "must not restart above the gap");
        Assert.Equal(2, DuctLayers(tp));
    }
}
