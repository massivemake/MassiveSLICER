using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Core.Slicing.Effects;

namespace MassiveSlicer.Tests;

/// <summary>
/// Proximity correction: reduce flow where two beads on the SAME layer run alongside each other
/// closer than a bead width, so the shared strip is not deposited twice.
///
/// Two things matter more than the arithmetic, and most of these tests pin those:
///
/// <list type="number">
/// <item><b>A contour's own SEAM is not a parallel neighbour.</b> A naive nearest-bead search finds
/// every closed loop meeting itself and reports a gap of zero. On a real 392-layer part that flagged
/// all 392 layers with gaps down to 0.000 mm — pure artifact. Built on that, this feature would cut
/// flow at every seam on every layer.</item>
/// <item><b>Short stretches are left alone.</b> The outer wall clipping past the end of an internal
/// arm is a few millimetres — 0.14 s at 85 mm/s, far inside the extruder's transport lag, so the RPM
/// change cannot land there and would only misplace flow further along.</item>
/// </list>
/// </summary>
public class ProximityFlowTest
{
    private const float Bead = 8f;

    private static SliceSettings Settings(bool on = true, float minRun = 100f) => new()
    {
        BeadWidth                  = Bead,
        LayerHeight                = 4f,
        FirstLayerHeight           = 4f,
        ProximityCorrectionEnabled = on,
        ProximityMinRunLengthMm    = minRun,
    };

    private static ToolpathLayer Layer(int i, float z) =>
        new(i, z) { Height = 4f, PlaneNormal = Vector3.UnitZ };

    private static void Seg(ToolpathLayer l, float x0, float y0, float x1, float y1, bool brim = false)
        => l.Moves.Add(new ToolpathMove(
            new Vector3(x0, y0, l.Z), new Vector3(x1, y1, l.Z), MoveKind.Extrude) { IsBrim = brim });

    /// <summary>The travel that separates two distinct walls in a real slice.</summary>
    private static void Travel(ToolpathLayer l, float x0, float y0, float x1, float y1)
        => l.Moves.Add(new ToolpathMove(
            new Vector3(x0, y0, l.Z), new Vector3(x1, y1, l.Z), MoveKind.Travel));

    /// <summary>A straight run along X at the given Y, chopped into 10 mm segments.</summary>
    private static void Run(ToolpathLayer l, float y, float x0, float x1)
    {
        for (float x = x0; x < x1 - 1e-6f; x += 10f)
            Seg(l, x, y, MathF.Min(x + 10f, x1), y);
    }

    // -- The arm case ------------------------------------------------------------------------

    /// <summary>
    /// Two long parallel runs 6 mm apart with an 8 mm bead — the real geometry. Each bead owns
    /// 4 mm on its free side plus 3 mm to the midpoint, so 7 of 8 mm: scale 0.875.
    /// </summary>
    [Fact]
    public void Two_long_runs_six_mm_apart_get_the_territory_scale()
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        Run(l, 0f,  0f, 400f);
        Travel(l, 400f, 0f, 0f, 6f);      // as a real slice separates two walls
        Run(l, 6f,  0f, 400f);
        tp.Layers.Add(l);

        ProximityFlowPostProcessor.Apply(tp, Settings());

        // Every extrude move on both walls takes the territory scale; the travel is untouched.
        var extrudeScales = l.Moves.Where(m => m.Kind == MoveKind.Extrude)
                                   .Select(m => m.WidthScale).Distinct().ToList();
        Assert.Single(extrudeScales);
        Assert.Equal(0.75f, extrudeScales[0], 3);

        // Two walls, so two runs — the travel between them ends the first.
        Assert.Equal(2, ProximityFlowPostProcessor.LastRuns.Count(r => r.Corrected));
    }

    /// <summary>
    /// Extrusion width equals line spacing: the bead owns its PITCH, so the factor is gap/bead.
    /// Not halfBead + gap/2 — that gave 0.875 at a 6 mm pitch and silently made a two-pass feature
    /// 14 mm wide instead of the 12 mm that two abutting 6 mm beads would have produced.
    /// </summary>
    [Theory]
    [InlineData(8f,   1.000f)]   // exactly a bead apart — touching, nothing to correct
    [InlineData(7f,   0.875f)]
    [InlineData(6f,   0.750f)]   // the arms: 2 passes x 6 mm = 12 mm, not 16
    [InlineData(4f,   0.500f)]
    [InlineData(2f,   0.250f)]
    public void The_scale_is_the_pitch_over_the_bead_width(float gap, float expected)
        => Assert.Equal(expected, BeadProximity.ScaleForGap(gap, Bead), 4);

    /// <summary>
    /// The property that makes it right: N passes at pitch p must deliver N x p of material, so a
    /// two-pass arm at 6 mm pitch delivers 12 mm worth — exactly what two abutting 6 mm beads did.
    /// </summary>
    [Theory]
    [InlineData(6f, 8f, 12f)]    // Jeff's arms: designed for two 6 mm beads
    [InlineData(6f, 6f, 12f)]    // the original bead: already correct, no change
    [InlineData(4f, 8f,  8f)]
    [InlineData(3f, 6f,  6f)]
    public void Two_passes_deliver_exactly_pitch_times_two(float pitch, float bead, float expectedMm)
    {
        float perBead = bead * BeadProximity.ScaleForGap(pitch, bead);
        Assert.Equal(expectedMm, 2f * perBead, 3);
    }

    [Fact]
    public void A_bead_with_nothing_alongside_is_left_at_full_flow()
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        Run(l, 0f,   0f, 400f);
        Run(l, 60f,  0f, 400f);   // far away
        tp.Layers.Add(l);

        ProximityFlowPostProcessor.Apply(tp, Settings());

        Assert.All(l.Moves, m => Assert.Equal(1f, m.WidthScale, 5));
        Assert.Empty(ProximityFlowPostProcessor.LastRuns);
    }

    // -- The two traps -----------------------------------------------------------------------

    /// <summary>
    /// ⭐ The seam, tested on the MEASUREMENT rather than through the post-processor — the run-length
    /// threshold would otherwise mask a broken filter by discarding the short false run it creates.
    ///
    /// The fixture matters: the seam must fall mid-edge so the closing segment runs PARALLEL to the
    /// opening one and lands right on top of it. A seam at a corner is filtered by the
    /// perpendicular-direction check instead, which is how an earlier version of this test passed
    /// with the cyclic filter deleted.
    ///
    /// Measured on a real 392-layer part, missing this filter flagged all 392 layers with gaps down
    /// to 0.000 mm — pure artifact. Built on that, the feature would cut flow at every seam.
    /// </summary>
    [Fact]
    public void A_closed_loop_meeting_its_own_seam_mid_edge_is_not_crowding()
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        // 200 mm square walked as one closed loop, STARTING mid-way along the bottom edge, so the
        // path closes by running back over its own start in the same direction.
        for (float x = 100; x < 200; x += 10) Seg(l, x, 0f, x + 10, 0f);
        for (float y = 0;   y < 200; y += 10) Seg(l, 200f, y, 200f, y + 10);
        for (float x = 200; x > 0;   x -= 10) Seg(l, x, 200f, x - 10, 200f);
        for (float y = 200; y > 0;   y -= 10) Seg(l, 0f, y, 0f, y - 10);
        for (float x = 0;   x < 100; x += 10) Seg(l, x, 0f, x + 10, 0f);
        tp.Layers.Add(l);

        // Sanity: the fixture really does close on itself in the same direction.
        var firstDir = Vector3.Normalize(l.Moves[0].To - l.Moves[0].From);
        var lastDir  = Vector3.Normalize(l.Moves[^1].To - l.Moves[^1].From);
        Assert.True(Vector3.Dot(firstDir, lastDir) > 0.99f,
            "fixture must close PARALLEL to its start, or the perpendicular filter hides the seam");

        var gaps = BeadProximity.MeasureGaps(tp, Bead);
        Assert.All(gaps, g => Assert.True(float.IsNaN(g),
            $"a loop meeting its own seam was reported as a parallel neighbour at {g:0.###} mm — "
          + "the arc-distance filter must be CYCLIC"));
    }

    /// <summary>
    /// Crossing beads are a junction, not a parallel run, and genuinely need full flow. Tested on the
    /// measurement: through the post-processor the run-length threshold discards the short run a
    /// crossing produces, so a broken direction filter would go unnoticed.
    /// </summary>
    [Fact]
    public void Crossing_beads_are_not_measured_as_running_alongside()
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        Run(l, 0f, 0f, 400f);                              // along X
        for (float y = -200; y < 200; y += 10)             // along Y, straight through it
            Seg(l, 200f, y, 200f, y + 10);
        tp.Layers.Add(l);

        var gaps = BeadProximity.MeasureGaps(tp, Bead);
        Assert.All(gaps, g => Assert.True(float.IsNaN(g),
            $"a crossing bead was measured as a parallel neighbour at {g:0.###} mm — beads that "
          + "cross are a junction and need full flow"));
    }

    /// <summary>
    /// The measurement must still SEE genuine parallel runs — otherwise the two tests above pass
    /// simply because nothing is ever detected.
    /// </summary>
    [Fact]
    public void The_measurement_is_not_blind_to_a_real_parallel_run()
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        Run(l, 0f, 0f, 400f);
        Travel(l, 400f, 0f, 0f, 6f);
        Run(l, 6f, 0f, 400f);
        tp.Layers.Add(l);

        var gaps = BeadProximity.MeasureGaps(tp, Bead);
        var seen = gaps.Where(g => !float.IsNaN(g)).ToList();
        Assert.NotEmpty(seen);
        Assert.All(seen, g => Assert.Equal(6f, g, 3));
    }

    /// <summary>
    /// ⭐ Short stretches. Identical geometry, only the overlap LENGTH differs — 40 mm is left alone
    /// against a 100 mm threshold, 400 mm is corrected.
    /// </summary>
    [Fact]
    public void A_short_near_meetup_is_left_alone_and_a_long_run_is_corrected()
    {
        Toolpath Build(float overlapLen)
        {
            var tp = new Toolpath();
            var l  = Layer(0, 4f);
            Run(l, 0f, 0f, 400f);                 // the arm
            Run(l, 6f, 0f, overlapLen);           // something running alongside part of it
            tp.Layers.Add(l);
            return tp;
        }

        var shortTp = Build(40f);
        ProximityFlowPostProcessor.Apply(shortTp, Settings(minRun: 100f));
        Assert.All(shortTp.Layers[0].Moves, m => Assert.Equal(1f, m.WidthScale, 5));
        Assert.True(ProximityFlowPostProcessor.LastRuns.Count > 0, "runs should be FOUND, just not corrected");
        Assert.DoesNotContain(ProximityFlowPostProcessor.LastRuns, r => r.Corrected);

        var longTp = Build(400f);
        ProximityFlowPostProcessor.Apply(longTp, Settings(minRun: 100f));
        Assert.Contains(longTp.Layers[0].Moves, m => m.WidthScale < 0.9f);
        Assert.Contains(ProximityFlowPostProcessor.LastRuns, r => r.Corrected);
    }

    /// <summary>
    /// The verdict belongs to the whole run, not to the move being looked at — otherwise a long run
    /// would be corrected only in the middle, leaving its ends at full flow.
    /// </summary>
    [Fact]
    public void Every_move_in_a_qualifying_run_is_corrected_including_its_ends()
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        Run(l, 0f, 0f, 300f);
        Travel(l, 300f, 0f, 0f, 6f);
        Run(l, 6f, 0f, 300f);
        tp.Layers.Add(l);

        ProximityFlowPostProcessor.Apply(tp, Settings());

        var extrudes = l.Moves.Where(m => m.Kind == MoveKind.Extrude).ToList();
        Assert.All(extrudes, m => Assert.True(m.WidthScale < 0.9f,
            $"a move inside a qualifying run was left at {m.WidthScale:0.###} — the run's verdict "
          + "must apply to every move in it, ends included"));
    }

    // -- Interaction and plumbing ------------------------------------------------------------

    /// <summary>
    /// Brim loops are deliberately adjacent AND are bed adhesion — correcting them would be wrong
    /// twice over.
    /// </summary>
    [Fact]
    public void Brim_is_never_corrected()
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        for (float x = 0; x < 400; x += 10) Seg(l, x, 0f, x + 10, 0f, brim: true);
        for (float x = 0; x < 400; x += 10) Seg(l, x, 6f, x + 10, 6f, brim: true);
        tp.Layers.Add(l);

        ProximityFlowPostProcessor.Apply(tp, Settings());

        Assert.All(l.Moves, m => Assert.Equal(1f, m.WidthScale, 5));
    }

    [Fact]
    public void Off_by_default_and_the_toggle_is_a_true_no_op()
    {
        Assert.False(new SliceSettings().ProximityCorrectionEnabled);
        Assert.Equal(100f, new SliceSettings().ProximityMinRunLengthMm, 3);

        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        Run(l, 0f, 0f, 400f);
        Run(l, 6f, 0f, 400f);
        tp.Layers.Add(l);

        ProximityFlowPostProcessor.Apply(tp, Settings(on: false));
        Assert.All(l.Moves, m => Assert.Equal(1f, m.WidthScale, 5));
        Assert.Empty(ProximityFlowPostProcessor.LastRuns);
    }

    /// <summary>
    /// It must reach commanded RPM, and it must MULTIPLY with the layer-thickness correction rather
    /// than replace it — a thinned layer that is also crowded is wrong by both factors at once.
    /// </summary>
    [Fact]
    public void It_reaches_RPM_and_multiplies_with_the_thickness_correction()
    {
        var plain    = new ToolpathMove(Vector3.Zero, Vector3.UnitX, MoveKind.Extrude);
        var crowded  = plain with { WidthScale = 0.75f };
        var thin     = plain with { HeightScale = 0.5f };
        var both     = plain with { HeightScale = 0.5f, WidthScale = 0.75f };

        Assert.Equal(1f,       MassiveSlicer.Core.IO.ToolpathRpm.MoveScale(plain),   4);
        Assert.Equal(0.75f,    MassiveSlicer.Core.IO.ToolpathRpm.MoveScale(crowded), 4);
        Assert.Equal(0.5f,     MassiveSlicer.Core.IO.ToolpathRpm.MoveScale(thin),    4);
        Assert.Equal(0.375f,   MassiveSlicer.Core.IO.ToolpathRpm.MoveScale(both),    4);
    }

    /// <summary>
    /// ToolpathClone rebuilds each move from an explicit field list, so a new field is silently
    /// dropped unless it is added there. That has already happened once, with IsBrim.
    /// </summary>
    [Fact]
    public void The_clone_carries_the_scale()
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        Seg(l, 0f, 0f, 10f, 0f);
        l.Moves[0] = l.Moves[0] with { WidthScale = 0.75f };
        tp.Layers.Add(l);

        Assert.Equal(0.75f, ToolpathClone.Copy(tp).Layers[0].Moves[0].WidthScale, 4);
    }

    [Fact]
    public void Applying_twice_does_not_compound()
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        Run(l, 0f, 0f, 400f);
        Run(l, 6f, 0f, 400f);
        tp.Layers.Add(l);

        ProximityFlowPostProcessor.Apply(tp, Settings());
        var once = l.Moves.Select(m => m.WidthScale).ToArray();
        ProximityFlowPostProcessor.Apply(tp, Settings());

        for (int i = 0; i < once.Length; i++)
            Assert.Equal(once[i], l.Moves[i].WidthScale, 5);
    }

    [Fact]
    public void It_can_only_reduce_flow_so_it_cannot_trip_the_export_gate()
    {
        foreach (float gap in new[] { 0f, 1f, 3f, 6f, 7.9f, 8f, 20f })
            Assert.InRange(BeadProximity.ScaleForGap(gap, Bead), 0.05f, 1f);
    }

    [Fact]
    public void Degenerate_inputs_do_not_throw()
    {
        Assert.Equal(1f, BeadProximity.ScaleForGap(float.NaN, Bead), 4);
        Assert.Equal(1f, BeadProximity.ScaleForGap(6f, 0f), 4);
        ProximityFlowPostProcessor.Apply(new Toolpath(), Settings());
        Assert.Empty(ProximityFlowPostProcessor.LastRuns);
    }
}
