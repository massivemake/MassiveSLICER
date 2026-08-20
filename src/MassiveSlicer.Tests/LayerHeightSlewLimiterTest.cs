using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

/// <summary>
/// The slew limiter caps how much layer thickness may change between adjacent layers.
///
/// Two properties matter more than the smoothing itself, and most of these tests exist to pin
/// them rather than to check that ramps appear:
///
/// <list type="number">
/// <item><b>A layer only ever gets THINNER.</b> Both bounds it could be fighting — the stairstep
/// tolerance and the overlap target — are UPPER bounds on thickness, so thinning can never
/// violate either. If the limiter were ever to thicken a layer to smooth a cliff it would be
/// silently undoing the overlap guarantee support-driven layer height exists to provide.</item>
/// <item><b>The layer that needs to be thin still gets its thickness.</b> The ramp is spent on
/// its neighbours, walking DOWN into the thin region before reaching it. Clipping the constraint
/// layer itself would defeat the point.</item>
/// </list>
/// </summary>
public class LayerHeightSlewLimiterTest
{
    private const float Nominal = 4f;
    private const float Floor   = 2f;

    /// <summary>Ladder from a thickness list, starting at Z=0.</summary>
    private static float[] Ladder(params float[] thicknesses)
    {
        var z = new float[thicknesses.Length + 1];
        for (int i = 0; i < thicknesses.Length; i++) z[i + 1] = z[i] + thicknesses[i];
        return z;
    }

    private static float[] Thicknesses(float[] ladder)
    {
        var h = new float[ladder.Length - 1];
        for (int i = 0; i < h.Length; i++) h[i] = ladder[i + 1] - ladder[i];
        return h;
    }

    // -- The two invariants ------------------------------------------------------------------

    /// <summary>
    /// ⭐ The safety property. Sampled per Z rather than per index, because the output ladder has
    /// MORE layers than the input — comparing index-to-index would be meaningless.
    /// </summary>
    [Fact]
    public void No_layer_is_ever_thicker_than_the_rules_asked_for_at_that_height()
    {
        // A deep notch: the rules want 2 mm in the middle of an otherwise 4 mm part.
        var input = Ladder(4, 4, 4, 4, 2, 2, 4, 4, 4, 4);
        float zMax = input[^1];

        var outLadder = LayerHeightSlewLimiter.Apply(input, zMax, 0.5f, Floor, Nominal);
        var inH  = Thicknesses(input);
        var outH = Thicknesses(outLadder);

        // What the rules allowed at a given Z, from the input ladder.
        float AllowedAt(float z)
        {
            for (int i = 1; i < input.Length; i++)
                if (input[i] > z + 1e-4f) return inH[i - 1];
            return inH[^1];
        }

        float zc = outLadder[0];
        for (int i = 0; i < outH.Length; i++)
        {
            Assert.True(outH[i] <= AllowedAt(zc) + 1e-3f,
                $"layer at Z {zc:0.###} came out {outH[i]:0.###} mm but the rules only allowed "
              + $"{AllowedAt(zc):0.###} mm there — thickening to smooth a cliff would undo the "
              + "stairstep tolerance and the overlap guarantee");
            zc += outH[i];
        }

        // Not vacuous: the limiter actually did something.
        Assert.True(outLadder.Length > input.Length,
            "thinning to ramp must emit more layers to cover the same part");
    }

    /// <summary>
    /// ⭐ The requirement Jeff raised: it has to start stepping BEFORE the layer that needs the
    /// thin value, and the thin layer must still receive it.
    /// </summary>
    [Fact]
    public void It_thins_on_the_way_up_and_still_reaches_the_required_thickness()
    {
        var input = Ladder(4, 4, 4, 4, 4, 2, 4, 4, 4, 4);
        var outH  = Thicknesses(LayerHeightSlewLimiter.Apply(input, input[^1], 0.5f, Floor, Nominal));

        // The required 2 mm is actually reached — not clipped up to something thicker.
        Assert.Contains(outH, t => Math.Abs(t - 2f) < 1e-3f);

        // And it is approached, not fallen into: the layer immediately before the 2 mm one is
        // itself already below nominal.
        int at2 = Array.FindIndex(outH, t => Math.Abs(t - 2f) < 1e-3f);
        Assert.True(at2 > 0, "the thin layer should not be the first layer in this fixture");
        Assert.True(outH[at2 - 1] < Nominal - 1e-3f,
            $"the layer below the constraint is {outH[at2 - 1]:0.###} mm — it should already be "
          + "thinning, otherwise the ladder falls into the thin region instead of stepping down");

        // Every step obeys the cap, in both directions. Starts at 2: see the first-layer
        // exemption pinned below.
        for (int i = 2; i < outH.Length; i++)
            Assert.True(Math.Abs(outH[i] - outH[i - 1]) <= 0.5f + 1e-3f,
                $"step {i}: {outH[i-1]:0.###} -> {outH[i]:0.###} exceeds the 0.5 mm cap");
    }

    /// <summary>
    /// ⭐ Regression, from real geometry. Two separate defects lived here, and a hand-made
    /// fixture caught neither — both were found by running a dumped 390-layer ladder through it.
    ///
    /// <list type="number">
    /// <item><b>The bind test read the ceiling.</b> The ceiling only ever constrains DESCENTS
    /// (thinning ahead of a thin region above); a rise is limited in the walk by
    /// <c>prev + cap</c>. So a ladder whose only violations were rises looked like "nothing to
    /// do" and was returned untouched — a 1.136 mm rise survived both a 1.0 mm and a 0.5 mm cap
    /// while the limiter silently did nothing.</item>
    /// <item><b>One plan/walk pass is not enough.</b> The plan is built at one cap per layer of
    /// the ladder it is handed, but the walk emits a DIFFERENT ladder, so the two spaces
    /// disagree. Single-pass left 0.372 mm on a 0.2 mm cap here.</item>
    /// </list>
    ///
    /// These are the real thicknesses of the six layers that reproduce it — the smallest window
    /// of that part which still fails on a single pass. Anything smaller or rounder passes for
    /// the wrong reason.
    /// </summary>
    [Fact]
    public void The_cap_holds_on_real_geometry_that_broke_two_earlier_attempts()
    {
        var input = Ladder(3.4560f, 3.4560f, 3.4280f, 3.3960f, 3.0000f, 2.8280f);

        foreach (float cap in new[] { 0.5f, 0.4f, 0.3f, 0.2f, 0.1f })
        {
            var outLadder = LayerHeightSlewLimiter.Apply(input, input[^1], cap, Floor, Nominal);
            float worst = LayerHeightSlewLimiter.WorstChangeMm(outLadder, skipFirstLayerBoundary: true);

            Assert.True(worst <= cap + 1e-3f,
                $"cap {cap} mm: worst emitted step is {worst:0.####} mm. Either the bind test is "
              + "reading the ceiling instead of the ladder, or the plan is not being re-derived "
              + "from what the walk produced.");

            Assert.True(worst <= LayerHeightSlewLimiter.WorstChangeMm(input) + 1e-3f,
                $"cap {cap} mm made the worst step WORSE than doing nothing");
        }
    }

    /// <summary>
    /// The bind test in isolation: a ladder whose only violation is a RISE must still be
    /// smoothed. This is the shape that was silently passed through.
    /// </summary>
    [Fact]
    public void A_ladder_violating_the_cap_only_on_a_rise_is_still_smoothed()
    {
        // Flat thin run, then a jump straight back to nominal — a pure rise, no descent to plan.
        var input = Ladder(2.86f, 2.86f, 2.86f, 2.86f, 4f, 4f, 4f, 4f);
        Assert.True(LayerHeightSlewLimiter.WorstChangeMm(input) > 1.1f, "fixture must hold a big rise");

        var outLadder = LayerHeightSlewLimiter.Apply(input, input[^1], 0.5f, Floor, Nominal);

        Assert.NotSame(input, outLadder);
        Assert.True(LayerHeightSlewLimiter.WorstChangeMm(outLadder, skipFirstLayerBoundary: true)
                    <= 0.5f + 1e-3f,
            "a rise-only violation was returned untouched — the bind test is reading the ceiling, "
          + "which never constrains rises");
    }

    // -- Behaviour ---------------------------------------------------------------------------

    [Fact]
    public void A_cliff_becomes_a_ramp_and_the_worst_step_falls_with_the_cap()
    {
        var input = Ladder(4, 4, 4, 4, 2, 2, 2, 4, 4, 4);
        float before = LayerHeightSlewLimiter.WorstChangeMm(input);
        Assert.Equal(2f, before, 3);          // the raw 4 -> 2 cliff

        float lastWorst = before;
        foreach (float cap in new[] { 1.0f, 0.5f, 0.25f })
        {
            var outLadder = LayerHeightSlewLimiter.Apply(input, input[^1], cap, Floor, Nominal);
            // skipFirstLayerBoundary: layer 0 keeps FirstLayerHeight, so when the descent has to
            // begin at the very bottom the 0 -> 1 step can exceed the cap. Pinned separately in
            // The_first_layer_boundary_is_the_one_documented_exemption.
            float worst = LayerHeightSlewLimiter.WorstChangeMm(outLadder, skipFirstLayerBoundary: true);
            Assert.True(worst <= cap + 1e-3f,
                $"cap {cap}: worst step came out {worst:0.###} mm");
            Assert.True(worst < lastWorst + 1e-6f, "a tighter cap must not make the worst step worse");
            lastWorst = worst;
        }
    }

    [Fact]
    public void Both_directions_are_smoothed_not_only_rises()
    {
        // Symmetric notch, so a one-directional implementation leaves one side abrupt.
        var input  = Ladder(4, 4, 4, 2, 4, 4, 4);
        var outH   = Thicknesses(LayerHeightSlewLimiter.Apply(input, input[^1], 0.5f, Floor, Nominal));
        int at2    = Array.FindIndex(outH, t => Math.Abs(t - 2f) < 1e-3f);

        Assert.True(at2 > 0);
        Assert.True(outH[at2 - 1] < Nominal - 1e-3f, "descent into the notch is not ramped");
        Assert.True(at2 + 1 < outH.Length && outH[at2 + 1] < Nominal - 1e-3f,
            "climb out of the notch is not ramped");
    }

    [Fact]
    public void The_part_is_still_covered_after_thinning()
    {
        var input = Ladder(4, 4, 4, 4, 2, 2, 4, 4, 4, 4);
        float zMax = input[^1];
        var outLadder = LayerHeightSlewLimiter.Apply(input, zMax, 0.3f, Floor, Nominal);

        // Thinner layers cover less height, so the walk must emit more of them rather than
        // leaving the part short.
        Assert.True(outLadder[^1] >= zMax - 1e-3f,
            $"ladder tops out at {outLadder[^1]:0.###} but the part reaches {zMax:0.###}");
        Assert.True(outLadder.Length > input.Length);
    }

    /// <summary>
    /// The single documented place the cap may be exceeded. Keeping a chosen FirstLayerHeight is
    /// the deliberate trade: it is a bed-adhesion setting, and the bottom of the print is where
    /// the extruder is priming anyway. Pinned so the exemption cannot widen unnoticed, and so it
    /// is impossible to mistake for a smoothing bug.
    /// </summary>
    [Fact]
    public void The_first_layer_boundary_is_the_one_documented_exemption()
    {
        // Cap 0.25 over a 4 -> 2 descent needs 8 layers of ramp, but the notch is only 5 layers
        // in, so the ramp must start at layer 0 -- which is exempt.
        var input = Ladder(4, 4, 4, 4, 2, 2, 4, 4, 4, 4);
        var outLadder = LayerHeightSlewLimiter.Apply(input, input[^1], 0.25f, Floor, Nominal);
        var outH = Thicknesses(outLadder);

        // Layer 0 kept its height, and the step off it is larger than the cap.
        Assert.Equal(4f, outH[0], 3);
        Assert.True(Math.Abs(outH[1] - outH[0]) > 0.25f + 1e-3f,
            "this fixture is meant to force the exemption; if it no longer does, it is not "
          + "testing anything");

        // Everything ABOVE that boundary honours the cap.
        for (int i = 2; i < outH.Length; i++)
            Assert.True(Math.Abs(outH[i] - outH[i - 1]) <= 0.25f + 1e-3f,
                $"step {i}: {outH[i-1]:0.###} -> {outH[i]:0.###} exceeds the cap above the "
              + "first-layer boundary, which is not exempt");

        // And the two reports differ by exactly that boundary.
        Assert.True(LayerHeightSlewLimiter.WorstChangeMm(outLadder) > 0.25f + 1e-3f);
        Assert.True(LayerHeightSlewLimiter.WorstChangeMm(outLadder, true) <= 0.25f + 1e-3f);
    }

    [Fact]
    public void The_first_layer_height_is_never_smoothed_away()
    {
        // First layer deliberately 6 mm on a 4 mm nominal — an adhesion setting, not a cliff.
        var input = Ladder(6, 4, 4, 4, 2, 4, 4, 4);
        var outH  = Thicknesses(LayerHeightSlewLimiter.Apply(input, input[^1], 0.2f, Floor, 6f));

        Assert.Equal(6f, outH[0], 3);
    }

    // -- Off / no-op paths -------------------------------------------------------------------

    [Fact]
    public void Zero_cap_is_off_and_returns_the_same_array()
    {
        var input = Ladder(4, 4, 2, 4, 4);
        Assert.Same(input, LayerHeightSlewLimiter.Apply(input, input[^1], 0f, Floor, Nominal));
        Assert.Same(input, LayerHeightSlewLimiter.Apply(input, input[^1], -1f, Floor, Nominal));
    }

    /// <summary>
    /// A ladder already inside the cap must come out bit-identical, not merely equal — a re-walk
    /// would nudge every boundary by float rounding, which would change a uniform print's output
    /// for no reason.
    /// </summary>
    [Fact]
    public void A_ladder_already_within_the_cap_is_returned_untouched()
    {
        var uniform = Ladder(4, 4, 4, 4, 4, 4);
        Assert.Same(uniform, LayerHeightSlewLimiter.Apply(uniform, uniform[^1], 0.5f, Floor, Nominal));

        var gentle = Ladder(4f, 3.8f, 3.6f, 3.8f, 4f);
        Assert.Same(gentle, LayerHeightSlewLimiter.Apply(gentle, gentle[^1], 0.5f, Floor, Nominal));
    }

    [Fact]
    public void Degenerate_ladders_are_returned_rather_than_indexed_off_the_end()
    {
        var two = Ladder(4);
        Assert.Same(two, LayerHeightSlewLimiter.Apply(two, two[^1], 0.5f, Floor, Nominal));
        var empty = new float[0];
        Assert.Same(empty, LayerHeightSlewLimiter.Apply(empty, 0f, 0.5f, Floor, Nominal));
    }

    [Fact]
    public void The_floor_is_respected_so_smoothing_cannot_thin_past_the_minimum()
    {
        var input = Ladder(4, 4, 4, 4, 2, 2, 4, 4, 4, 4);
        var outH  = Thicknesses(LayerHeightSlewLimiter.Apply(input, input[^1], 0.5f, 2f, Nominal));

        foreach (float t in outH)
            Assert.True(t >= 2f - 1e-3f, $"layer thinned to {t:0.###} mm, below the 2 mm floor");
    }

    // -- The gate ----------------------------------------------------------------------------

    /// <summary>
    /// The limiter moves slice planes, so extrusion flow has to follow it. That is keyed to the
    /// single <see cref="SliceSettings.VariesLayerThickness"/> property precisely so a new rule
    /// cannot be added without flow noticing — the bug that shipped twice when it was keyed to a
    /// list of feature flags instead.
    /// </summary>
    [Fact]
    public void Enabling_it_makes_flow_correction_run()
    {
        Assert.False(new SliceSettings().VariesLayerThickness);
        Assert.True(new SliceSettings { MaxLayerHeightChangeMm = 0.2f }.VariesLayerThickness);
        Assert.False(new SliceSettings { MaxLayerHeightChangeMm = 0f }.VariesLayerThickness);
    }
}
