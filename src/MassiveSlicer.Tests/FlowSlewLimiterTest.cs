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
/// <summary>
/// Joins the "AdaptiveLayerHeights" collection for the reason that collection exists: xUnit runs test
/// CLASSES in parallel, and the slicer publishes diagnostics through statics. Left outside it, this
/// class's parallel load was enough to make LayerLadderAgreementTest fail in the full suite while
/// passing in isolation - a flaky test, not a bug.
/// </summary>
[Collection("AdaptiveLayerHeights")]
public class FlowSlewLimiterTest
{
    /// <summary>
    /// ⭐ <b>The real invariant.</b> A written value may differ from the previous one by at most
    /// <c>rate x (the time the PREVIOUS value was HELD)</c>.
    ///
    /// <para>⚠️ Not "by the duration of the segment it enters" — that is a different and wrong rule,
    /// and it is what an earlier version of these tests asserted. It happened to pass while every
    /// move took its own micro-step, and it started failing the moment the ramp began holding a
    /// value across moves, which is the correct behaviour. The first step is unconstrained on
    /// purpose: full flow was in force for the whole preceding stretch.</para>
    /// </summary>
    private static void AssertRateRespected(ToolpathLayer l, float ratePctPerSec, float speedMmS)
    {
        float prev = 1f;
        float held = float.PositiveInfinity;

        foreach (var m in l.Moves)
        {
            if (m.Kind != MoveKind.Extrude) continue;
            float len = Len(m);

            if (MathF.Abs(m.WidthScale - prev) > 1e-6f)
            {
                float allowed = (ratePctPerSec / 100f) * (held / speedMmS);
                float step    = MathF.Abs(m.WidthScale - prev) / MathF.Max(prev, 1e-6f);
                Assert.True(step <= allowed + 1e-3f,
                    $"stepped {step * 100f:0.##} % after holding only {held:0.#} mm "
                  + $"({held / speedMmS:0.###} s), which licenses {allowed * 100f:0.##} %");
                prev = m.WidthScale;
                held = len;
            }
            else held += len;
        }
    }

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

    /// <summary>
    /// ⭐⭐ <b>The write-cadence test, and the most load-bearing one here.</b> A long ramp built from
    /// MANY SHORT MOVES must step about once per <see cref="FlowSlewLimiter.RampStepSeconds"/> of
    /// travel — not once per move.
    ///
    /// <para>Letting each move take its own step keeps the RATE correct while writing RPM on every
    /// bead: on a real part the median move is 3.48 mm, and it produced 95,057 writes against the
    /// known-good reference file's 1312. On the URM path there is no whole-percent rounding to
    /// collapse them (<c>"0.#####"</c>), so nearly all of them reach the machine as distinct
    /// commands between consecutive LINs — the <c>$ADVANCE</c> stutter KrlExporter warns about.</para>
    /// </summary>
    [Fact]
    public void The_ramp_steps_once_per_hold_time_not_once_per_move()
    {
        // 3000 mm of ramp delivered as 1000 moves of 3 mm — the real crumb structure.
        var l = Layer();
        for (int i = 0; i < 1000; i++)
            l.Moves.Add(new ToolpathMove(new Vector3(i * 3f, 0f, l.Z),
                                         new Vector3((i + 1) * 3f, 0f, l.Z), MoveKind.Extrude));
        var targets = new float[1000];
        Array.Fill(targets, 0.75f);

        var stats = FlowSlewLimiter.Apply(One(l), targets, S(2f, 100f));

        // Steps are a constant 5 % (2 %/s x 2.5 s), so 1.0 -> 0.75 takes 0.95^n <= 0.75, n = 6.
        // Six steps across 1000 moves - not one per move, and not one per 2.5 s forever either,
        // because it ARRIVES and then holds.
        Assert.InRange(stats.Steps, 4, 10);

        int distinct = l.Moves.Select(m => MathF.Round(m.WidthScale, 5)).Distinct().Count();
        Assert.InRange(distinct, 4, 12);

        // CONTROL: without accumulation every move would carry its own value. If this ever holds,
        // the cadence has regressed even though the RATE assertions all still pass.
        Assert.True(distinct < 100,
            $"{distinct} distinct flow values across 1000 short moves — the ramp is stepping per "
          + "move again, which writes RPM between every LIN");

        // 3000 mm at 100 mm/s is 30 s - ample - so it reaches target exactly and holds there.
        Assert.Equal(0.75f, l.Moves[^1].WidthScale, 4);
        AssertRateRespected(l, 2f, 100f);
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

        AssertRateRespected(l, 2f, 100f);

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
        // An explicit range, not Assert.Equal(.., 2) — that ROUNDS, and 0.8735 vs 0.875 once failed
        // on a decimal boundary rather than on a real change. 608 mm at 92 mm/s is 2.6 hold periods
        // of 230 mm, and the first step is immediate, so three 5 % steps land: 0.95^3 = 0.857.
        Assert.InRange(landed, 0.84f, 0.88f);
        Assert.InRange(stats.Effectiveness, 0.20f, 0.45f);
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

        AssertRateRespected(l, 2f, 100f);

        float prev = 1f;
        bool sawRampUp = false;
        foreach (var m in l.Moves)
        {
            if (m.WidthScale > prev + 1e-6f) sawRampUp = true;
            prev = m.WidthScale;
        }
        Assert.True(sawRampUp, "flow never walked back up after the crowded run ended");
        Assert.True(l.Moves[^1].WidthScale < 1f,
            "600 mm is 6 s, not enough to recover the whole drop — reaching exactly 1.0 means the "
          + "up-ramp was not rate-limited");
    }

    /// <summary>
    /// ⚠️ A layer stitch is an ordinary extruding move that still writes an RPM value, so leaving it
    /// out of the ramp makes it a cliff in BOTH directions — up into it and back down out of it.
    /// Excluding stitches and resume ramps cost 806 steps over the cap on a real 392-layer column:
    /// 391 across boundaries and 415 within layers, almost exactly two per layer.
    /// </summary>
    [Fact]
    public void A_layer_stitch_is_rate_limited_like_any_other_extruding_move()
    {
        var l = Layer();
        Move(l, 600f, y: 0f);                       // crowded — ramps down
        l.Moves.Add(new ToolpathMove(new Vector3(600, 0, 4), new Vector3(0, 6, 4), MoveKind.Extrude)
                    { IsLayerStitch = true });      // used to sit at 1.0 and cliff both ways
        Move(l, 600f, y: 6f);

        FlowSlewLimiter.Apply(One(l), [0.75f, 1f, 1f], S());

        AssertRateRespected(l, 2f, 100f);

        // And it was genuinely ramped, not merely left alone: a skipped stitch would still be 1.0.
        var stitches = l.Moves.Where(m => m.IsLayerStitch).ToList();
        Assert.NotEmpty(stitches);
        Assert.All(stitches, m => Assert.True(m.WidthScale < 1f,
            "a stitch segment came back at full flow — it was skipped, not rate-limited"));
    }

    /// <summary>
    /// ⚠️ A zero-length extrude move still writes an RPM value, so leaving it at full flow makes it a
    /// cliff in BOTH directions. These are real — degenerate segments survive in the path — and they
    /// were every one of the 11 remaining over-10 % steps on a real 392-layer column: a 14 % jump to
    /// exactly 1.000 sitting between neighbours at 0.882 and 0.885.
    /// </summary>
    [Fact]
    public void A_zero_length_move_holds_the_current_flow_instead_of_resetting_to_full()
    {
        var l = Layer();
        Move(l, 600f, y: 0f);      // ramps down toward 0.75
        var p = new Vector3(600f, 0f, l.Z);
        l.Moves.Add(new ToolpathMove(p, p, MoveKind.Extrude));   // degenerate, mid-path
        Move(l, 600f, y: 0f);

        FlowSlewLimiter.Apply(One(l), [0.75f, 0.75f, 0.75f], S());

        var degenerate = l.Moves.Single(m => Vector3.Distance(m.From, m.To) <= 1e-4f);
        Assert.True(degenerate.WidthScale < 0.99f,
            $"the zero-length move came back at {degenerate.WidthScale:0.###} — it reset to full flow "
          + "and is a cliff at the machine");

        // It must match its neighbours, not merely be under 1.0: no time passes, so no change is legal.
        int i = l.Moves.IndexOf(degenerate);
        Assert.Equal(l.Moves[i - 1].WidthScale, degenerate.WidthScale, 5);
    }

    /// <summary>
    /// ⭐⭐ <b>The end-to-end check: what the EXPORTED PROGRAM actually contains.</b> Everything else
    /// here measures the toolpath; this measures the <c>.src</c>.
    ///
    /// <para>It has to be done on the <b>URM / Digital Start-Stop</b> path, because that is the one
    /// the Caracol uses and the one with no safety net: <c>FormatCaracolRpm</c> writes
    /// <c>"0.#####"</c> — five decimals, NO rounding to whole motor percent — so unlike the ANOUT
    /// path nothing downstream collapses near-identical values. A ramp that micro-steps per move
    /// therefore lands an <c>RPM =</c> line between consecutive LINs, which is exactly the
    /// "$ADVANCE continuous path / robot stutter on dense clusters" failure KrlExporter warns about.
    /// The known-good reference export writes 1312 RPM changes over 506.7 m — one per ~386 mm.</para>
    /// </summary>
    [Fact]
    public void The_exported_program_does_not_write_RPM_between_every_move()
    {
        // 3000 mm of crowded bead delivered as 1000 moves of 3 mm - the real crumb structure.
        var l = Layer();
        for (int i = 0; i < 1000; i++)
            l.Moves.Add(new ToolpathMove(new Vector3(i * 3f, 0f, l.Z),
                                         new Vector3((i + 1) * 3f, 0f, l.Z), MoveKind.Extrude));
        var targets = new float[1000];
        Array.Fill(targets, 0.75f);

        var tp = One(l);
        FlowSlewLimiter.Apply(tp, targets, S(2f, 92f));

        string src = MassiveSlicer.Core.IO.KrlExporter.Export(tp, new MassiveSlicer.Core.IO.KrlExportSettings
        {
            ProgramName             = "FlowSlewCadence",
            DigitalStartStopEnabled = true,      // URM: "RPM = n", five decimals, no percent rounding
            BeadWidthMm             = 8f,
            LayerHeightMm           = 4f,
            PrintSpeedMps           = 0.092f,
            FlowRate                = 0.463f,
        });

        var lines    = src.Split((char)10);
        int rpmLines = lines.Count(x => x.TrimStart().StartsWith("RPM =", StringComparison.Ordinal));
        int linLines = lines.Count(x => x.Contains("LIN ", StringComparison.Ordinal));

        Assert.True(linLines > 500, $"only {linLines} LIN lines — the fixture did not export properly");

        // The ramp is 1.0 -> 0.75 in 5 % steps = 6 steps, plus start/stop bookkeeping. Nowhere near
        // one per move. Generous ceiling so this fails on a REGRESSION, not on a formatting tweak.
        Assert.True(rpmLines < 40,
            $"{rpmLines} RPM writes across {linLines} LIN moves — the ramp is writing RPM between "
          + "consecutive LINs, which kills $ADVANCE. Before the allowance accumulated across moves "
          + "this was ~1 per move.");

        // And state it as the ratio, which is the thing that actually matters to the controller.
        double perLin = (double)rpmLines / linLines;
        Assert.True(perLin < 0.05,
            $"one RPM write per {1.0 / perLin:0.#} LIN moves — far denser than the reference export");
    }

    // -- What must not be touched ------------------------------------------------------------

    /// <summary>
    /// Only the moves WidthScale cannot reach are exempt: travel deposits nothing, the brim's
    /// absolute RpmPercentOverride bypasses every scale, and ToolpathRpm.MoveScale returns
    /// WipeRpmScale outright for a wipe. Note these three therefore remain unmanaged RPM steps —
    /// pre-existing, and not something this limiter can fix.
    /// </summary>
    [Fact]
    public void Travel_brim_and_wipe_pass_straight_through()
    {
        var l = Layer();
        l.Moves.Add(new ToolpathMove(new Vector3(0, 0, 4), new Vector3(500, 0, 4), MoveKind.Travel));
        l.Moves.Add(new ToolpathMove(new Vector3(0, 1, 4), new Vector3(500, 1, 4), MoveKind.Extrude)
                    { IsBrim = true, RpmPercentOverride = 60f });
        l.Moves.Add(new ToolpathMove(new Vector3(0, 2, 4), new Vector3(500, 2, 4), MoveKind.Extrude)
                    { IsWipe = true });

        FlowSlewLimiter.Apply(One(l), [0.75f, 0.75f, 0.75f], S());

        Assert.Equal(3, l.Moves.Count);
        Assert.All(l.Moves, m => Assert.Equal(1f, m.WidthScale, 5));
    }

    /// <summary>
    /// A resume ramp, by contrast, IS rate-limited now. It is a real extruding move that writes RPM,
    /// and its own ResumeRpmScale multiplies alongside WidthScale rather than replacing it.
    /// </summary>
    [Fact]
    public void A_resume_ramp_is_rate_limited_too()
    {
        var l = Layer();
        l.Moves.Add(new ToolpathMove(new Vector3(0, 0, 4), new Vector3(600, 0, 4), MoveKind.Extrude)
                    { IsResumeRamp = true });

        FlowSlewLimiter.Apply(One(l), [0.75f], S());

        Assert.True(l.Moves.Count > 1, "the resume ramp was skipped rather than rate-limited");
        Assert.True(l.Moves[0].WidthScale > 0.9f, "it should walk down, not slam");
        Assert.All(l.Moves, m => Assert.True(m.IsResumeRamp, "the flag must survive subdivision"));
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

    /// <summary>
    /// ⚠️ <b>The metric bug that must never come back.</b> Reduction the ramp spills onto bead that was
    /// never crowded is under-extruded WALL — a cost — not delivered correction. Counting it as
    /// delivery reported <b>97.4 %</b> on a real 392-layer column where the toolpath said
    /// <b>49.6 %</b>: only 0.3 % of bead reached the 0.75 target while 885 m of uncrowded wall ran
    /// reduced purely because the ramp had not finished climbing.
    /// </summary>
    [Fact]
    public void Reduction_spilled_onto_uncrowded_bead_is_collateral_not_delivery()
    {
        var l = Layer();
        Move(l, 600f, y: 0f);      // crowded: wants 0.75
        Move(l, 600f, y: 6f);      // NOT crowded: wants 1.0, but the ramp is still climbing
        var s = FlowSlewLimiter.Apply(One(l), [0.75f, 1f], S());

        Assert.True(s.CollateralOnFreeMm > 1f,
            "the second move is uncrowded and cannot be at full flow yet, so there must be "
          + "collateral — if there is none, the split is not being made at all");

        // Effectiveness must ignore the collateral entirely.
        Assert.Equal(s.DeliveredOnCrowdedMm / s.WantedReductionMm, s.Effectiveness, 4);
        Assert.True(s.Effectiveness < 1f);

        // And the old, wrong metric — everything summed together — would have overstated it.
        float inflated = (s.DeliveredOnCrowdedMm + s.CollateralOnFreeMm) / s.WantedReductionMm;
        Assert.True(inflated > s.Effectiveness,
            "if lumping collateral in does not inflate the figure, this test proves nothing");

        // Delivery can never exceed what was asked for: the ramp lags the target, never overshoots.
        Assert.True(s.DeliveredOnCrowdedMm <= s.WantedReductionMm + 1e-3f);
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
