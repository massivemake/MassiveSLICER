using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Caps how fast commanded extrusion flow may change ALONG the path, turning a flow cliff into a
/// ramp. The in-layer counterpart of <see cref="LayerHeightSlewLimiter"/>.
///
/// <para><b>Why.</b> <see cref="ProximityFlowPostProcessor"/> knows what flow each move SHOULD carry
/// but not how fast the extruder can get there. Stamping the target on the first crowded move put a
/// 0.75 factor into the program as an instant ~24-point RPM drop, about 300 times in one job; the
/// drive saturated, real speed stopped following set, and the booth stopped accepting manual
/// offsets. A coworker's known-good export makes BIGGER swings — 39 points — and the controller
/// follows every one, because it walks them at roughly 5 % per 2.5 s. Same write form, same
/// handshake count. Only the step size differed. See
/// <see cref="SliceSettings.MaxFlowChangePercentPerSecond"/>.</para>
///
/// <para><b>Why this has to SPLIT moves.</b> An arm wall is one single move 100-800 mm long — on a
/// measured real part, 5096 segments over 100 mm carried 23 % of the whole path from 0.086 % of the
/// moves. The exporter writes one RPM per move (<see cref="IO.ToolpathRpm.WritesRpm"/>), so on
/// exactly the moves that matter there is nowhere to put an intermediate value and a per-move cap
/// would do nothing whatsoever. So a move that must ramp is subdivided into
/// <see cref="RampStepSeconds"/>-long pieces, and the remainder — once flow has reached target —
/// stays a single segment. This is not the global segment-length cap that was costed at +28 % file
/// size and declined: only the ramp portion of crowded moves is touched.</para>
///
/// <para><b>Both directions.</b> The drive saturated on slams UP as well as down (71.26 to 95.01,
/// ~300 times), so leaving an arm is rate-limited too. What is deliberately NOT done here is
/// lookahead: the ramp is only ever spent on the moves that want the change, never started early on
/// the move before. So a run's ENTRY over-extrudes until flow catches up, and its EXIT
/// under-extrudes until flow recovers.</para>
///
/// <para><b>⚠️ The known, accepted shortfall.</b> A compliant ramp cannot reach target inside one
/// arm at current speeds: 608 mm at 92 mm/s is 6.6 s, and 2 %/s reaches ~0.88 of nominal rather than
/// 0.75 just as the arm ends, whereupon the outer wall pulls it back up and the next arm restarts
/// from part-way. It oscillates and never settles, cancelling roughly a quarter of the over-deposit.
/// That is accepted for now. <see cref="Stats.Effectiveness"/> reports the fraction actually
/// delivered so this never has to be guessed at again.</para>
///
/// <para><b>Owed, and it changes everything:</b> nobody has found where the drive ACTUALLY stops
/// tracking. 2 %/s is one person's self-imposed guess that happened to work. If the real limit is
/// nearer 10 %/s the shortfall above disappears.</para>
///
/// <para><b>⚠️ Relative %, or RPM points? The reference file cannot tell us.</b> Measured over its
/// 1311 printing-range changes, the coefficient of variation is <b>2.04 for points/s and 2.00 for
/// relative %/s</b> — indistinguishable, and both enormous. That file is not a controlled rate
/// limiter; it is merely gentle on average. So relative is a CHOICE, not a finding. The reason to
/// prefer it: its RPM runs 43.74-82.62, and a fixed points/s cap would be proportionally harshest at
/// the bottom of that range, which is exactly where flow is nearest its calibration floor and least
/// trustworthy. Relative eases off there instead. If a real machine measurement ever shows the drive
/// cares about points, this is the assumption to revisit first.</para>
///
/// <para><b>⚠️ Three caveats before comparing anything to that file.</b>
/// <list type="number">
/// <item>Its RPM values carry a <b>manual +10 offset</b> — a hand-added boost, to be replaced
/// properly by dialling in the HV. Backing it out moves its median rate from 1.70 to
/// <b>1.98 %/s</b>, which is what the 2 %/s default is actually matched against. Reading the rate
/// off the file as written would have set this cap ~15 % too slack.</item>
/// <item>It <b>misses an entire arm.</b> Its 1312 changes are the cost of an INCOMPLETE correction,
/// so its change count is not a ceiling — correcting all four arms legitimately needs more writes
/// than correcting three.</item>
/// <item>It was <b>hand post-processed after export</b>, which is exactly what we are NOT doing:
/// this limiter runs in the slicer, so the program comes out correct by construction.</item>
/// </list>
/// Compare RATE and SHAPE against it. Never absolute RPM, never coverage, never write count.</para>
/// </summary>
public static class FlowSlewLimiter
{
    /// <summary>
    /// How long one ramp step is held, in seconds. With the default 2 %/s rate this is 5 % per step
    /// at ~230 mm spacing (at 92 mm/s), which reproduces the known-good reference export almost
    /// exactly.
    ///
    /// <para><b>Measured on that file</b> (<c>2026_0820 - Glider_Capital_01_TEST.src</c> — an export
    /// that was hand post-processed AFTER leaving the slicer, and which the machine then ran
    /// smoothly): 1312 RPM changes over 506.7 m, median step <b>3.22 points = 4.77 % relative</b>,
    /// median spacing <b>219.9 mm</b>. Only 3 steps (0.23 %) exceed 10 % relative, and the 2 over
    /// 20 % are the <c>1.00 -> 82.62</c> start/stop transitions. It is a reference for what our own
    /// EXPORT should look like — not something to imitate by post-processing our <c>.src</c>; this
    /// limiter runs in the slicer, so the program comes out right by construction.</para>
    ///
    /// <para>⚠️ <b>So "never step more than 5 %" is NOT the rule</b> — 622 of its 1312 steps, 47 %,
    /// are over 5 % relative. What actually holds is the RATE: ~4.8 % per ~220 mm at 92 mm/s, i.e.
    /// ~2.1 %/s. Judge a program by its rate and by the 10 % line, not by a 5 % step count.</para>
    ///
    /// <para>Finer would be smoother, and an earlier version used 1.0 s. It was changed to match the
    /// reference because finer is NOT free: at a nominal ~76 % RPM one whole motor percent is 1.3 %
    /// relative, so 2 % steps are distinct commands and get written. 1.0 s spacing writes RPM 2.4x
    /// more often than the proven file, and <c>KrlExporter</c> warns in its own comment that
    /// re-writing ANOUT between every LIN "kills $ADVANCE continuous path and makes the robot stutter
    /// on dense clusters". Reproducing what is known to work beats out-smoothing it on a guess.</para>
    ///
    /// <para>Not a tuning knob: the rate is the physical constraint; this is only how finely the ramp
    /// is discretised on a long move. On short moves the move's own length is the step.</para>
    /// </summary>
    public const float RampStepSeconds = 2.5f;

    /// <summary>What the last <see cref="Apply"/> did. Diagnostics; nothing reads it to decide.</summary>
    /// <param name="WantedReductionMm">
    /// What the correction asked for: sum of (1 - target) x length over crowded bead.
    /// </param>
    /// <param name="DeliveredOnCrowdedMm">
    /// What actually arrived ON the crowded bead that wanted it. Always &lt;= wanted, because the
    /// ramp can only lag the target, never overshoot it.
    /// </param>
    /// <param name="CollateralOnFreeMm">
    /// ⚠️ Flow reduction that landed on bead which was NOT crowded, because the ramp was still
    /// walking back up when the crowded run ended. This is a COST — under-extruded outer wall — not
    /// part of the correction, and it must never be added to the delivered figure.
    /// </param>
    public sealed record Stats(
        int   MovesRamped,
        int   SegmentsAdded,
        int   Steps,
        float WorstStepFraction,
        float WantedReductionMm,
        float DeliveredOnCrowdedMm,
        float CollateralOnFreeMm)
    {
        /// <summary>
        /// Fraction of the intended reduction that actually reached the bead that wanted it.
        ///
        /// <para>⚠️ This deliberately does NOT credit <see cref="CollateralOnFreeMm"/>. An earlier
        /// version summed every reduction the limiter commanded anywhere and reported <b>97.4 %</b> on
        /// a real 392-layer column. Measured against the toolpath, the truth was <b>49.6 %</b>: only
        /// 0.3 % of bead ever reached the 0.75 target, and 885 m of UNCROWDED wall was running
        /// reduced simply because the ramp had not finished climbing. The old figure was counting
        /// that under-extrusion as success.</para>
        /// </summary>
        public float Effectiveness =>
            WantedReductionMm <= 1e-4f ? 1f : DeliveredOnCrowdedMm / WantedReductionMm;

        public static readonly Stats Empty = new(0, 0, 0, 0f, 0f, 0f, 0f);
    }

    /// <summary>
    /// Rewrites <see cref="ToolpathMove.WidthScale"/> so it never changes faster than
    /// <see cref="SliceSettings.MaxFlowChangePercentPerSecond"/>, subdividing moves where a ramp
    /// needs room to walk.
    /// </summary>
    /// <param name="toolpath">Mutated in place, including <see cref="ToolpathLayer.Contours"/>.</param>
    /// <param name="targetScale">
    /// Desired scale per move, flat-indexed across all layers in order — what the correction WANTS,
    /// which is deliberately not read back off the moves. Passing the target in is what keeps this
    /// idempotent: re-running recomputes targets from geometry rather than treating an
    /// already-limited value as the goal.
    /// </param>
    public static Stats Apply(Toolpath toolpath, float[] targetScale, SliceSettings settings)
    {
        float ratePerSec = MathF.Max(settings.MaxFlowChangePercentPerSecond, 0f) / 100f;
        float nominalMmS = MathF.Max(settings.PrintSpeedMps * 1000f, 1e-3f);

        int   movesRamped = 0, segmentsAdded = 0, steps = 0;
        float worstStep   = 0f;
        double wanted = 0.0, onCrowded = 0.0, collateral = 0.0;

        // The drive is one physical thing and never forgets, so the commanded value carries across
        // travels and across layer boundaries rather than resetting.
        float commanded = 1f;
        int   flat      = 0;

        foreach (var layer in toolpath.Layers)
        {
            // Original move index -> how many segments it became. Contour spans index into Moves,
            // so they must be shifted by whatever was inserted before and inside them, or re-seaming
            // silently addresses the wrong moves.
            var expansion = new int[layer.Moves.Count];
            var rebuilt   = new List<ToolpathMove>(layer.Moves.Count);

            for (int mi = 0; mi < layer.Moves.Count; mi++, flat++)
            {
                var   move   = layer.Moves[mi];
                float target = flat < targetScale.Length ? targetScale[flat] : 1f;

                // ⚠️ Only moves whose commanded flow this cannot reach are exempt. Excluding a move
                // from a CORRECTION is reasonable; excluding it from a MACHINE CONSTRAINT is not,
                // because the exporter still writes an RPM value for it and the drive still has to
                // follow that step.
                //
                // Layer stitches and resume ramps were excluded here at first and it cost 806 steps
                // over the cap on a real 392-layer column — 391 across layer boundaries and 415
                // within layers, almost exactly two per layer. PlanarSlicer inserts one stitch at
                // index 0 of every layer; left unramped it held WidthScale 1.0 and became a cliff
                // BOTH ways, up into it and back down out of it, while the ramp walked on behind it.
                // Both are ordinary extruding moves, so both are rate-limited now.
                //
                // Still exempt, because WidthScale genuinely cannot reach them: travel deposits
                // nothing, the brim's absolute RpmPercentOverride bypasses every scale, and
                // ToolpathRpm.MoveScale returns WipeRpmScale outright for a wipe. The wipe and brim
                // transitions are therefore still unmanaged steps — pre-existing, and not this
                // limiter's to fix.
                bool rampable = move.Kind == MoveKind.Extrude
                                && !move.IsBrim && !move.IsWipe
                                && move.RpmPercentOverride is null;

                float length = Vector3.Distance(move.From, move.To);

                if (rampable) wanted += (1f - target) * length;

                if (!rampable)
                {
                    rebuilt.Add(move);
                    expansion[mi] = 1;
                    continue;
                }

                // ⚠️ A zero-length extrude move still WRITES AN RPM VALUE. No time passes, so it
                // cannot ramp — but it must HOLD the value in force, not reset to full flow. Passing
                // these through untouched left them at WidthScale 1.0 and produced a 14 % cliff in
                // both directions, sandwiched between neighbours at 0.882 and 0.885. That was every
                // one of the 11 remaining over-10 % steps on a real 392-layer column, and the
                // signature is unmistakable in a dump: len = 0.00 mm, w = exactly 1.000.
                if (length <= 1e-4f)
                {
                    rebuilt.Add(move with { WidthScale = commanded });
                    expansion[mi] = 1;
                    continue;
                }

                // No limit configured: stamp the target outright. Same behaviour as before this
                // limiter existed, so turning the cap off restores the old path exactly.
                if (ratePerSec <= 0f)
                {
                    if (MathF.Abs(target - commanded) > 1e-5f) { steps++; commanded = target; }
                    rebuilt.Add(move with { WidthScale = target });
                    expansion[mi] = 1;
                    Account(target, target, length);
                    continue;
                }

                float speed   = nominalMmS * MathF.Max(move.PrintSpeedScale, 1e-3f);
                float stepLen = MathF.Max(speed * RampStepSeconds, 1e-3f);

                // Already where it needs to be — the overwhelmingly common case, and it must not
                // split anything.
                if (MathF.Abs(target - commanded) <= 1e-5f)
                {
                    rebuilt.Add(move with { WidthScale = commanded });
                    expansion[mi] = 1;
                    Account(target, commanded, length);
                    continue;
                }

                int   emitted   = 0;
                float travelled = 0f;

                while (travelled < length - 1e-4f && MathF.Abs(target - commanded) > 1e-5f)
                {
                    float segLen  = MathF.Min(stepLen, length - travelled);
                    float allowed = ratePerSec * (segLen / speed);      // relative, over this segment

                    float next = target > commanded
                        ? MathF.Min(target, commanded * (1f + allowed))
                        : MathF.Max(target, commanded * (1f - allowed));
                    next = Math.Clamp(next, BeadProximity.MinScale, 1f);

                    float stepFrac = MathF.Abs(next - commanded) / MathF.Max(commanded, 1e-6f);
                    if (stepFrac > worstStep) worstStep = stepFrac;
                    steps++;

                    rebuilt.Add(SliceSegment(move, length, travelled, travelled + segLen)
                                with { WidthScale = next });
                    emitted++;
                    Account(target, next, segLen);

                    commanded  = next;
                    travelled += segLen;
                }

                // Whatever is left runs at the value the ramp arrived at — one segment, however
                // long. Only the ramp itself gets subdivided.
                if (travelled < length - 1e-4f)
                {
                    float segLen = length - travelled;
                    rebuilt.Add(SliceSegment(move, length, travelled, length)
                                with { WidthScale = commanded });
                    emitted++;
                    Account(target, commanded, segLen);
                }

                expansion[mi]  = emitted;
                segmentsAdded += emitted - 1;
                if (emitted > 1) movesRamped++;
            }

            if (rebuilt.Count != layer.Moves.Count)
            {
                RemapContours(layer, expansion);
                layer.Moves.Clear();
                layer.Moves.AddRange(rebuilt);
            }
            else
            {
                for (int i = 0; i < rebuilt.Count; i++) layer.Moves[i] = rebuilt[i];
            }
        }

        return new Stats(movesRamped, segmentsAdded, steps, worstStep,
                         (float)wanted, (float)onCrowded, (float)collateral);

        // Reduction on bead the correction asked to reduce is DELIVERY; the identical reduction on
        // bead it never asked about is COLLATERAL — under-extruded wall. Conflating the two is what
        // turned a 49.6 % result into a reported 97.4 %.
        void Account(float target, float commandedNow, float lengthMm)
        {
            double cut = (1f - commandedNow) * (double)lengthMm;
            if (cut <= 0.0) return;
            if (target < 1f - 1e-5f) onCrowded  += cut;
            else                     collateral += cut;
        }
    }

    /// <summary>
    /// The piece of <paramref name="move"/> between two arc distances, carrying every other field
    /// forward. Endpoints are taken verbatim at the ends rather than interpolated, so a subdivided
    /// move starts and finishes exactly where the original did and the path cannot drift.
    /// </summary>
    private static ToolpathMove SliceSegment(ToolpathMove move, float length, float from, float to)
    {
        Vector3 a = from <= 1e-4f          ? move.From : Vector3.Lerp(move.From, move.To, from / length);
        Vector3 b = to   >= length - 1e-4f ? move.To   : Vector3.Lerp(move.From, move.To, to   / length);
        return move with { From = a, To = b };
    }

    /// <summary>
    /// Shifts every <see cref="ContourSpan"/> to account for inserted segments. A span's Start moves
    /// by everything inserted before it; its Count grows by everything inserted inside it.
    /// </summary>
    private static void RemapContours(ToolpathLayer layer, int[] expansion)
    {
        if (layer.Contours.Count == 0) return;

        // prefix[i] = index in the rebuilt list where original move i now begins.
        var prefix = new int[expansion.Length + 1];
        for (int i = 0; i < expansion.Length; i++) prefix[i + 1] = prefix[i] + expansion[i];

        for (int k = 0; k < layer.Contours.Count; k++)
        {
            var c = layer.Contours[k];
            if (c.Start < 0 || c.Start > expansion.Length) continue;

            int end      = Math.Min(c.Start + c.Count, expansion.Length);
            int newStart = prefix[Math.Min(c.Start, expansion.Length)];
            int newCount = prefix[end] - newStart;
            int newEntry = c.EntryTravelIndex >= 0 && c.EntryTravelIndex < expansion.Length
                ? prefix[c.EntryTravelIndex]
                : c.EntryTravelIndex;

            layer.Contours[k] = new ContourSpan(newStart, newCount, c.Closed, newEntry);
        }
    }

    /// <summary>One-line summary for the console and the status bar.</summary>
    public static string Describe(Stats s, SliceSettings settings)
    {
        if (settings.MaxFlowChangePercentPerSecond <= 0f)
            return "Flow slew cap OFF — the full correction is stamped on the first crowded move. "
                 + "That is what saturated the extruder drive; set MaxFlowChangePercentPerSecond.";

        if (s.Steps == 0) return "No flow changes to rate-limit.";

        return $"Flow capped at {settings.MaxFlowChangePercentPerSecond:0.##} %/s: {s.Steps} steps, "
             + $"worst {s.WorstStepFraction * 100f:0.##} % relative. {s.MovesRamped} moves subdivided "
             + $"(+{s.SegmentsAdded} segments). Delivers {s.DeliveredOnCrowdedMm / 1000.0:0.###} m of "
             + $"the {s.WantedReductionMm / 1000.0:0.###} m the correction wanted "
             + $"= {s.Effectiveness * 100f:0.#} %, and spills {s.CollateralOnFreeMm / 1000.0:0.###} m "
             + "of reduction onto bead that was not crowded (under-extruded wall).";
    }
}
