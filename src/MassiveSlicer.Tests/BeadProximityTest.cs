using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

/// <summary>
/// Bead proximity DETECTION: finding where two beads on the same layer run alongside each other
/// closer than a bead width — internal arms whose spacing the model fixes.
///
/// <para><b>Detection only.</b> Nothing here acts on a finding, and
/// <see cref="Nothing_is_written_to_the_toolpath"/> pins that: what to do about a detected arm is a
/// separate decision that has not been made. There is deliberately no flow factor, no setting and
/// no toggle.</para>
///
/// Three things matter more than the arithmetic, and most of these tests pin those:
///
/// <list type="number">
/// <item><b>A closed contour meeting its own SEAM is not a parallel neighbour.</b> A naive
/// nearest-bead search finds every loop meeting itself and reports a gap of zero. On a real
/// 392-layer part that flagged all 392 layers with gaps down to 0.000 mm — pure artifact.</item>
/// <item><b>All the arms, not just one.</b> An earlier index-based filter found 1 of 4: arms drawn
/// as out/U-turn/back have their two walls only TWO moves apart, so an index skip discards them
/// even though they are ~350 mm apart along the path.</item>
/// <item><b>A near-miss is not a feature.</b> The outer wall clipping past the end of an arm is a
/// few millimetres and is not the same finding as 600 mm of parallel wall.</item>
/// </list>
///
/// No <c>[Collection]</c> needed: the measurement is a pure function with no static state, so
/// nothing here can clobber another test class.
/// </summary>
public class BeadProximityTest
{
    private const float Bead = 8f;

    private static ToolpathLayer Layer(int i, float z) =>
        new(i, z) { Height = 4f, PlaneNormal = Vector3.UnitZ };

    private static void Seg(ToolpathLayer l, float x0, float y0, float x1, float y1, bool brim = false)
        => l.Moves.Add(new ToolpathMove(
            new Vector3(x0, y0, l.Z), new Vector3(x1, y1, l.Z), MoveKind.Extrude) { IsBrim = brim });

    private static void Travel(ToolpathLayer l, float x0, float y0, float x1, float y1)
        => l.Moves.Add(new ToolpathMove(
            new Vector3(x0, y0, l.Z), new Vector3(x1, y1, l.Z), MoveKind.Travel));

    /// <summary>A straight run along X at the given Y, chopped into 10 mm segments.</summary>
    private static void Run(ToolpathLayer l, float y, float x0, float x1)
    {
        for (float x = x0; x < x1 - 1e-6f; x += 10f)
            Seg(l, x, y, MathF.Min(x + 10f, x1), y);
    }

    // -- The guarantee -----------------------------------------------------------------------

    /// <summary>
    /// The contract: measuring must not change the toolpath in any way. The detection reports
    /// geometry and stops, so the finding can be acted on later by whatever mechanism turns out to
    /// be right — which will not be RPM.
    /// </summary>
    [Fact]
    public void Nothing_is_written_to_the_toolpath()
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        Run(l, 0f, 0f, 400f);
        Travel(l, 400f, 0f, 0f, 6f);
        Run(l, 6f, 0f, 400f);
        tp.Layers.Add(l);

        var before = l.Moves.ToList();
        var runs   = BeadProximityReport.Measure(tp, Bead);

        Assert.NotEmpty(runs);   // it really did find something, so this is not a vacuous check
        Assert.Equal(before.Count, l.Moves.Count);
        for (int i = 0; i < before.Count; i++)
            Assert.Equal(before[i], l.Moves[i]);
    }

    // -- The arm case ------------------------------------------------------------------------

    /// <summary>Two long parallel runs at a 6 mm pitch with an 8 mm bead — the real geometry.</summary>
    [Fact]
    public void Two_long_parallel_runs_are_measured_at_their_pitch()
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        Run(l, 0f, 0f, 400f);
        Travel(l, 400f, 0f, 0f, 6f);
        Run(l, 6f, 0f, 400f);
        tp.Layers.Add(l);

        var runs = BeadProximityReport.Measure(tp, Bead);

        Assert.Equal(2, runs.Count);                       // one per wall
        Assert.All(runs, r => Assert.Equal(6f, r.ClosestGapMm, 3));
        Assert.All(runs, r => Assert.True(r.IsLongRun));
        Assert.All(runs, r => Assert.Equal(400f, r.LengthMm, 1));
    }

    /// <summary>A bead with nothing alongside is not measured at all.</summary>
    [Fact]
    public void A_lone_bead_is_not_measured()
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        Run(l, 0f, 0f, 400f);
        tp.Layers.Add(l);

        Assert.Empty(BeadProximityReport.Measure(tp, Bead));
        Assert.All(BeadProximity.MeasureGaps(tp, Bead), g => Assert.True(float.IsNaN(g)));
    }

    /// <summary>
    /// An arm drawn as out / U-turn / back: both walls must be found even though they are only two
    /// moves apart in the path. The tip runs ACROSS the gap, so the direction test excludes it.
    /// </summary>
    [Fact]
    public void An_arm_drawn_as_a_U_turn_is_found_on_both_walls()
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        Seg(l, 0f, 0f, 350f, 0f);       // out along one wall  (index 0)
        Seg(l, 350f, 0f, 350f, 6f);     // U-turn at the tip   (index 1)
        Seg(l, 350f, 6f, 0f, 6f);       // back along the other (index 2)
        tp.Layers.Add(l);

        // The fixture must actually have the walls 2 moves apart, or it is not testing the bug.
        Assert.Equal(3, l.Moves.Count);

        var gaps = BeadProximity.MeasureGaps(tp, Bead);
        Assert.Equal(6f, gaps[0], 3);
        Assert.Equal(6f, gaps[2], 3);
        Assert.True(float.IsNaN(gaps[1]), "the tip crosses the gap and is not running alongside it");
    }

    /// <summary>
    /// The other half of that: four such arms in one layer must ALL be found, not just whichever one
    /// happens to sit far enough away in the move list. This is the case that was found broken on a
    /// real part — 1 of 4.
    /// </summary>
    [Fact]
    public void All_four_U_turn_arms_in_a_layer_are_found()
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        void Arm(float ox, float oy, bool horizontal)
        {
            if (horizontal)
            {
                Seg(l, ox, oy, ox + 350f, oy);
                Seg(l, ox + 350f, oy, ox + 350f, oy + 6f);
                Seg(l, ox + 350f, oy + 6f, ox, oy + 6f);
            }
            else
            {
                Seg(l, ox, oy, ox, oy + 350f);
                Seg(l, ox, oy + 350f, ox + 6f, oy + 350f);
                Seg(l, ox + 6f, oy + 350f, ox + 6f, oy);
            }
        }
        Arm(0f,    0f,    true);
        Arm(0f,    500f,  true);
        Arm(1000f, 0f,    false);
        Arm(1200f, 0f,    false);
        tp.Layers.Add(l);

        var gaps  = BeadProximity.MeasureGaps(tp, Bead);
        int walls = gaps.Count(g => !float.IsNaN(g));
        Assert.Equal(8, walls);            // two walls per arm, four arms

        var runs = BeadProximityReport.Measure(tp, Bead);
        Assert.Equal(8, runs.Count(r => r.IsLongRun));
    }

    // -- The traps ---------------------------------------------------------------------------

    /// <summary>
    /// The seam, tested on the MEASUREMENT rather than through the run grouping — the run-length
    /// threshold would otherwise mask a broken filter by discarding the short false run it creates.
    ///
    /// The fixture matters: the seam must fall mid-edge so the closing segment runs PARALLEL to the
    /// opening one and lands on top of it. A seam at a CORNER is filtered by the perpendicular check
    /// instead, which is how an earlier version of this test passed with the cyclic filter deleted.
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
    /// Crossing beads are a junction, not a parallel run. Tested on the measurement: through the run
    /// grouping the length threshold discards the short run a crossing produces, so a broken
    /// direction filter would go unnoticed.
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
          + "cross are a junction, not crowding"));
    }

    /// <summary>
    /// The control for the two tests above: the measurement must still SEE genuine parallel runs,
    /// otherwise they pass simply because nothing is ever detected.
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

        var seen = BeadProximity.MeasureGaps(tp, Bead).Where(g => !float.IsNaN(g)).ToList();
        Assert.NotEmpty(seen);
        Assert.All(seen, g => Assert.Equal(6f, g, 3));
    }

    /// <summary>
    /// Identical geometry, only the parallel LENGTH differs: 40 mm is an incidental near-miss,
    /// 400 mm is a feature. Both are still reported — the classification is information, not a
    /// filter that hides things.
    /// </summary>
    [Theory]
    [InlineData(40f,  false)]
    [InlineData(400f, true)]
    public void A_near_miss_and_a_feature_are_classified_apart(float overlapLen, bool expectFeature)
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        Run(l, 0f, 0f, 400f);                 // the arm
        Run(l, 6f, 0f, overlapLen);           // something running alongside part of it
        tp.Layers.Add(l);

        var runs = BeadProximityReport.Measure(tp, Bead);
        Assert.NotEmpty(runs);
        Assert.Equal(expectFeature, runs.Any(r => r.IsLongRun));
    }

    /// <summary>Two separate near-misses must not pool their lengths into one false feature.</summary>
    [Fact]
    public void Runs_do_not_merge_across_a_spatial_jump()
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        Run(l, 0f, 0f, 400f);        // the long wall
        Run(l, 6f, 0f, 60f);         // 60 mm alongside it, here...
        Run(l, 6f, 300f, 360f);      // ...and 60 mm alongside it over there
        tp.Layers.Add(l);

        var runs = BeadProximityReport.Measure(tp, Bead);
        Assert.DoesNotContain(runs, r => r.LengthMm > 200f);
        Assert.All(runs, r => Assert.False(r.IsLongRun,
            "two 60 mm stretches pooled into a false 120 mm feature — the spatial-jump break is gone"));
    }

    /// <summary>Brim is laid down on its own and is not a crowded feature.</summary>
    [Fact]
    public void Brim_is_not_reported()
    {
        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        for (float x = 0; x < 400f; x += 10f) Seg(l, x, 0f, x + 10f, 0f, brim: true);
        for (float x = 0; x < 400f; x += 10f) Seg(l, x, 6f, x + 10f, 6f, brim: true);
        tp.Layers.Add(l);

        Assert.Empty(BeadProximityReport.Measure(tp, Bead));
    }

    // -- The reported geometry ---------------------------------------------------------------

    /// <summary>
    /// The excess is pure geometry: two passes at pitch p each deliver a bead of width w, so they
    /// carry w/p - 1 more than the space holds. No flow, no RPM, no prescription.
    /// </summary>
    [Theory]
    [InlineData(6f,   8f, 0.3333f)]   // the real case: 8 mm bead at a 6 mm pitch
    [InlineData(4f,   8f, 1.0f)]      // half the pitch, twice the material
    [InlineData(8f,   8f, 0f)]        // exactly abutting, nothing extra
    [InlineData(12f,  8f, 0f)]        // farther apart than the bead is wide
    public void Excess_is_pure_geometry(float gap, float bead, float expected)
        => Assert.Equal(expected, BeadProximityReport.ExcessRatio(gap, bead), 3);

    [Fact]
    public void Degenerate_inputs_do_not_throw()
    {
        Assert.Empty(BeadProximityReport.Measure(new Toolpath(), Bead));
        Assert.Empty(BeadProximityReport.Measure(new Toolpath(), 0f));

        var tp = new Toolpath();
        var l  = Layer(0, 4f);
        Seg(l, 0f, 0f, 0f, 0f);       // zero-length bead
        Travel(l, 0f, 0f, 10f, 0f);
        tp.Layers.Add(l);
        Assert.Empty(BeadProximityReport.Measure(tp, Bead));
    }
}
