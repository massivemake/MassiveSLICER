using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;

namespace MassiveSlicer.Tests;

/// <summary>
/// The in-layer flow rate cap. Tested against <see cref="FlowSlewLimiter.Apply"/> DIRECTLY with an
/// explicit target array, so the ramp is measured apart from the measurement that produced the
/// target — the two mask each other otherwise, which is how three control tests in this feature's
/// history passed when they should have failed.
///
/// <para>The one that matters most is
/// <see cref="A_long_move_is_subdivided_so_the_ramp_has_somewhere_to_live"/>. An arm wall is a
/// SINGLE move of 100-800 mm and the exporter writes one RPM per move, so a limiter that refused to
/// subdivide would be a silent no-op on exactly the geometry it exists for — it would pass a
/// "flow never steps more than 2 %" assertion by never changing flow at all.</para>
/// </summary>
public class FlowSlewLimiterTest
{
    private static SliceSettings S(float ratePctPerSec = 2f, float speedMmS = 100f) => new()
    {
        BeadWidth                     = 8f,
        LayerHeight                   = 4f,
        FirstLayerHeight              = 4f,
        PrintSpeedMps                 = speedMmS / 1000f,
        MaxFlowChangePercentPerSecond = ratePctPerSec,
    };

    private static ToolpathLayer Layer() => new(0, 4f) { Height = 4f, PlaneNormal = Vector3.UnitZ };

    /// <summary>One straight extrude move along X of the given length — an arm wall, undivided.</summary>
    private static void Move(ToolpathLayer l, float lengthMm, float y = 0f)
        => l.Moves.Add(new ToolpathMove(
            new Vector3(0f, y, l.Z), new Vector3(lengthMm, y, l.Z), MoveKind.Extrude));

    private static Toolpath One(ToolpathLayer l)
    {
        var tp = new Toolpath();
        tp.Layers.Add(l);
        return tp;
    }

    private static float Len(ToolpathMove m) => Vector3.Distance(m.From, m.To);

    // -- Subdivision: the thing without which none of this does anything ----------------------

    /// <summary>
    /// A 1000 mm arm wall is one move. The cap can only act by splitting it, so if this comes back
    /// as a single segment the whole feature is inert no matter what the flow values say.
    /// </summary>
    [Fact]
    public void A_long_move_is_subdivided_so_the_ramp_has_somewhere_to_live()
    {
        var l = Layer();
        Move(l, 1000f);
        var tp = One(l);

        var stats = FlowSlewLimiter.Apply(tp, [0.75f], S());

        Assert.True(l.Moves.Count > 1,
            $"a 1000 mm move came back as {l.Moves.Count} segment(s) — the exporter writes one RPM "
          + "per move, so the ramp had nowhere to go and the cap did nothing");
        Assert.Equal(l.Moves.Count - 1, stats.SegmentsAdded);
        Assert.Equal(1, stats.MovesRamped);

        // CONTROL: the split must be caused by the cap, not by something splitting unconditionally.
        var l2 = Layer();
        Move(l2, 1000f);
        FlowSlewLimiter.Apply(One(l2), [0.75f], S(ratePctPerSec: 0f));
        Assert.Single(l2.Moves);
        Assert.Equal(0.75f, l2.Moves[0].WidthScale, 4);
    }

    /// <summary>
    /// Subdivision rewrites path geometry, so it has to be exact: same start, same end, contiguous,
    /// same total length. A drifting split would move the bead.
    /// </summary>
    [Fact]
    public void Subdivision_preserves_the_path_exactly()
    {
        var l = Layer();
        Move(l, 1000f);
        var from = l.Moves[0].From;
        var to   = l.Moves[0].To;

        FlowSlewLimiter.Apply(One(l), [0.75f], S());

        Assert.Equal(from, l.Moves[0].From);
        Assert.Equal(to,   l.Moves[^1].To);

        float total = 0f;
        for (int i = 0; i < l.Moves.Count; i++)
        {
            total += Len(l.Moves[i]);
            if (i > 0)
                Assert.True(Vector3.Distance(l.Moves[i - 1].To, l.Moves[i].From) < 1e-3f,
                    $"segment {i} does not start where segment {i - 1} ended");
        }
        Assert.Equal(1000f, total, 2);
    }

    // -- The cap itself ----------------------------------------------------------------------

    /// <summary>
    /// No written value may differ from the previous one by more than the rate allows over the time
    /// that value is held. This is the machine constraint; everything else here is bookkeeping.
    /// </summary>
    [Fact]
    public void No_written_step_exceeds_the_cap()
    {
        var l = Layer();
        Move(l, 3000f);
        FlowSlewLimiter.Apply(One(l), [0.75f], S(ratePctPerSec: 2f, speedMmS: 100f));

        float prev = 1f;
        foreach (var m in l.Moves)
        {
            float dt      = Len(m) / 100f;
            float allowed = 0.02f * dt;
            float step    = MathF.Abs(m.WidthScale - prev) / prev;
            Assert.True(step <= allowed + 1e-4f,
                $"step {step * 100f:0.###} % over {dt:0.###} s exceeds the {allowed * 100f:0.###} % allowed");
            prev = m.WidthScale;
        }

        // CONTROL: the same assertion must FAIL on an uncapped run, or it is proving nothing.
        var l2 = Layer();
        Move(l2, 3000f);
        FlowSlewLimiter.Apply(One(l2), [0.75f], S(ratePctPerSec: 0f));
        float firstStep = MathF.Abs(l2.Moves[0].WidthScale - 1f);
        Assert.True(firstStep > 0.2f,
            "uncapped, the first move should take the whole 25 % in one step — if it does not, the "
          + "assertion above is not measuring what it claims to");
    }

    /// <summary>Given enough distance the ramp arrives exactly on target and stops splitting.</summary>
    [Fact]
    public void It_reaches_the_target_when_the_move_is_long_enough()
    {
        var l = Layer();
        Move(l, 10000f);        // 100 s at 100 mm/s; 25 % at 2 %/s needs ~14.2 s
        FlowSlewLimiter.Apply(One(l), [0.75f], S());

        Assert.Equal(0.75f, l.Moves[^1].WidthScale, 4);

        // The tail, once on target, is ONE segment however long — only the ramp is subdivided.
        Assert.True(Len(l.Moves[^1]) > 5000f,
            $"the on-target remainder came back as {Len(l.Moves[^1]):0.#} mm — it should be a single "
          + "long segment, not chopped up, or every straightaway inflates the file");
    }

    /// <summary>
    /// ⭐ The accepted shortfall, pinned. A 608 mm arm at 92 mm/s is 6.6 s, and 2 %/s cannot get from
    /// 1.0 to 0.75 in that time — it arrives at ~0.875 just as the arm ends. Roughly 29 % of the
    /// intended reduction is delivered, which is the "cancels about a quarter" figure measured on the
    /// real column. If this number ever moves, the trade behind this feature has changed.
    /// </summary>
    [Fact]
    public void A_real_arm_cannot_reach_the_target_and_this_pins_how_far_short()
    {
        var l = Layer();
        Move(l, 608f);
        var stats = FlowSlewLimiter.Apply(One(l), [0.75f], S(ratePctPerSec: 2f, speedMmS: 92f));

        float landed = l.Moves[^1].WidthScale;

        Assert.True(landed > 0.75f + 1e-3f,
            $"the arm reached {landed:0.####}, so a compliant ramp DID reach target in 6.6 s — "
          + "either the rate or the arithmetic changed, and the accepted shortfall is now wrong");
        Assert.Equal(0.875f, landed, 2);
        Assert.InRange(stats.Effectiveness, 0.20f, 0.40f);
    }

    /// <summary>
    /// Leaving a crowded run is rate-limited too. The drive saturated on slams UP as well as down,
    /// so an instant return to full flow is the same machine event.
    /// </summary>
    [Fact]
    public void It_ramps_back_up_when_the_crowding_ends()
    {
        var l = Layer();
        Move(l, 600f, y: 0f);      // crowded
        Move(l, 600f, y: 6f);      // free again
        FlowSlewLimiter.Apply(One(l), [0.75f, 1f], S());

        // Nothing anywhere jumps straight back to full flow.
        float prev = 1f;
        bool sawRampUp = false;
        foreach (var m in l.Moves)
        {
            float step = MathF.Abs(m.WidthScale - prev) / prev;
            Assert.True(step <= 0.02f + 1e-4f, $"a {step * 100f:0.##} % step slipped through");
            if (m.WidthScale > prev + 1e-6f) sawRampUp = true;
            prev = m.WidthScale;
        }
        Assert.True(sawRampUp, "flow never walked back up after the crowded run ended");
        Assert.True(l.Moves[^1].WidthScale < 1f,
            "600 mm is 6 s, not enough to recover the whole drop — reaching exactly 1.0 means the "
          + "up-ramp was not rate-limited");
    }

    // -- What must not be touched ------------------------------------------------------------

    /// <summary>
    /// Travel deposits nothing, the brim carries an absolute RpmPercentOverride that bypasses every
    /// scale, and wipes and resume ramps are deliberate ramps of their own.
    /// </summary>
    [Fact]
    public void Travel_brim_wipe_and_resume_ramps_pass_straight_through()
    {
        var l = Layer();
        l.Moves.Add(new ToolpathMove(new Vector3(0, 0, 4), new Vector3(500, 0, 4), MoveKind.Travel));
        l.Moves.Add(new ToolpathMove(new Vector3(0, 1, 4), new Vector3(500, 1, 4), MoveKind.Extrude)
                    { IsBrim = true, RpmPercentOverride = 60f });
        l.Moves.Add(new ToolpathMove(new Vector3(0, 2, 4), new Vector3(500, 2, 4), MoveKind.Extrude)
                    { IsWipe = true });
        l.Moves.Add(new ToolpathMove(new Vector3(0, 3, 4), new Vector3(500, 3, 4), MoveKind.Extrude)
                    { IsResumeRamp = true });

        FlowSlewLimiter.Apply(One(l), [0.75f, 0.75f, 0.75f, 0.75f], S());

        Assert.Equal(4, l.Moves.Count);
        Assert.All(l.Moves, m => Assert.Equal(1f, m.WidthScale, 5));
    }

    /// <summary>
    /// ⚠️ <see cref="ContourSpan"/> holds INDICES into <see cref="ToolpathLayer.Moves"/>, so
    /// inserting segments silently repoints every span after the insertion — re-seaming would then
    /// rotate the wrong moves. Nothing in the UI would report this; the part would just come out
    /// wrong.
    /// </summary>
    [Fact]
    public void Contour_spans_still_cover_the_same_path_after_subdivision()
    {
        var l = Layer();
        l.Moves.Add(new ToolpathMove(new Vector3(0, 0, 4), new Vector3(0, 0, 4), MoveKind.Travel));
        Move(l, 400f, y: 0f);                                     // index 1
        l.Moves.Add(new ToolpathMove(new Vector3(400, 0, 4), new Vector3(400, 6, 4), MoveKind.Extrude));
        l.Moves.Add(new ToolpathMove(new Vector3(400, 6, 4), new Vector3(0, 6, 4), MoveKind.Extrude));
        l.Contours.Add(new ContourSpan(Start: 1, Count: 3, Closed: true, EntryTravelIndex: 0));

        var firstPoint = l.Moves[1].From;
        var lastPoint  = l.Moves[3].To;

        FlowSlewLimiter.Apply(One(l), [1f, 0.75f, 0.75f, 0.75f], S());

        var span = l.Contours[0];

        // It DID have to move — otherwise this test would pass on a limiter that never split.
        Assert.True(span.Count > 3,
            "nothing was subdivided, so this test is not exercising the remap at all");

        Assert.Equal(0, span.EntryTravelIndex);
        Assert.Equal(firstPoint, l.Moves[span.Start].From);
        Assert.Equal(lastPoint,  l.Moves[span.Start + span.Count - 1].To);
        Assert.Equal(l.Moves.Count, span.Start + span.Count);
        Assert.True(span.Closed);
    }

    // -- Reporting ---------------------------------------------------------------------------

    /// <summary>
    /// Effectiveness is the number that says whether this correction is worth anything, so it has to
    /// be arithmetic rather than assertion: full reduction delivered when there is room, a known
    /// fraction when there is not.
    /// </summary>
    [Fact]
    public void Effectiveness_reports_how_much_of_the_correction_actually_landed()
    {
        var roomy = Layer();
        Move(roomy, 40000f);
        var full = FlowSlewLimiter.Apply(One(roomy), [0.75f], S());
        Assert.True(full.Effectiveness > 0.95f,
            $"with 400 s of travel the ramp is a rounding error, but effectiveness read "
          + $"{full.Effectiveness:0.###}");

        var cramped = Layer();
        Move(cramped, 608f);
        var partial = FlowSlewLimiter.Apply(One(cramped), [0.75f], S(2f, 92f));
        Assert.True(partial.Effectiveness < full.Effectiveness);

        Assert.Equal(0f, FlowSlewLimiter.Stats.Empty.WantedReductionMm, 5);
        Assert.Equal(1f, FlowSlewLimiter.Stats.Empty.Effectiveness, 5);
    }

    [Fact]
    public void Nothing_is_ever_pushed_above_full_flow_or_below_the_floor()
    {
        var l = Layer();
        Move(l, 5000f, y: 0f);
        Move(l, 5000f, y: 6f);
        FlowSlewLimiter.Apply(One(l), [0.05f, 1f], S());

        Assert.All(l.Moves, m => Assert.InRange(m.WidthScale, MassiveSlicer.Core.Slicing.BeadProximity.MinScale, 1f));
    }
}
