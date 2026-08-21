using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Reduces extrusion flow where two beads on the SAME layer run alongside each other closer than a
/// bead width, so the strip they share is not deposited twice.
///
/// The horizontal counterpart of <see cref="LayerHeightFlowPostProcessor"/>: that one makes flow
/// follow the real layer THICKNESS, this one makes it follow the real bead TERRITORY. It rides on
/// <see cref="ToolpathMove.WidthScale"/>, so every downstream consumer picks it up unchanged —
/// <see cref="IO.ToolpathRpm.MoveScale"/>, the KRL exporter, the RPM view and gradient, and the
/// 99 % export gate.
///
/// <para><b>Only long runs are corrected.</b> A bead alongside another for a few millimetres — the
/// outer wall clipping past the end of an internal arm — is not worth acting on: at 85 mm/s a 12 mm
/// stretch is 0.14 s, far inside the extruder's transport lag, so the RPM change could not land
/// there and would only put the wrong flow somewhere else. Measured on a real part the two
/// populations separate with nothing at all in between: 2237 runs under 15 mm (7.7 % of crowded
/// bead) against 784 runs of 360-366 mm (91.2 %), and an empty band from 60 to 250 mm. Any
/// threshold inside that band gives identical results.</para>
///
/// <para>Scales are always &lt;= 1, so this can only reduce commanded flow — it cannot push a
/// previously-valid job over the export limit. Idempotent: the scale is assigned, never
/// accumulated.</para>
/// </summary>
public static class ProximityFlowPostProcessor
{
    /// <summary>One continuous stretch of bead running alongside a neighbour.</summary>
    public sealed record Run(
        int LayerIndex,
        float Z,
        float LengthMm,
        float ClosestGapMm,
        float MeanScale,
        bool Corrected,
        int FirstFlatIndex);

    /// <summary>What the last <see cref="Apply"/> did. Diagnostics; nothing reads it to decide.</summary>
    public static IReadOnlyList<Run> LastRuns => s_last;
    private static Run[] s_last = [];

    /// <summary>
    /// What the rate limiter did to this pass's targets — how much of the intended correction
    /// actually survived the drive's slew cap. Diagnostics.
    /// </summary>
    public static FlowSlewLimiter.Stats LastSlew { get; private set; } = FlowSlewLimiter.Stats.Empty;

    /// <summary>Drops published runs, so a report cannot describe a slice that is no longer live.</summary>
    public static void ResetRuns()
    {
        s_last   = [];
        LastSlew = FlowSlewLimiter.Stats.Empty;
    }

    /// <summary>
    /// Stamps <see cref="ToolpathMove.WidthScale"/> on crowded bead whose run is long enough to act
    /// on. No-op unless <see cref="SliceSettings.ProximityCorrectionEnabled"/>.
    /// </summary>
    public static void Apply(Toolpath toolpath, SliceSettings settings)
    {
        if (!settings.ProximityCorrectionEnabled) { ResetRuns(); return; }

        float bead = settings.BeadWidth;
        if (bead <= 0f || toolpath.Layers.Count == 0) { ResetRuns(); return; }

        var gaps   = BeadProximity.MeasureGaps(toolpath, bead);
        float minRun = MathF.Max(settings.ProximityMinRunLengthMm, 0f);

        var runs = FindRuns(toolpath, gaps, bead, minRun);

        // Applied only after every run's verdict is settled, so a move's fate is decided by the
        // length of the whole stretch it belongs to rather than by the move itself.
        var qualifying = new HashSet<int>();
        foreach (var r in runs)
            if (r.Corrected)
                for (int i = 0; i < r.MoveCount; i++) qualifying.Add(r.FirstFlatIndex + i);

        // This pass states the TARGET flow per move. How fast the extruder is allowed to travel
        // toward that target is FlowSlewLimiter's business, and keeping the two apart is what keeps
        // both idempotent: the target is always recomputed from geometry, never read back off a move
        // that has already been rate-limited.
        var targets = new float[gaps.Length];
        Array.Fill(targets, 1f);
        foreach (int i in qualifying)
        {
            float scale = BeadProximity.ScaleForGap(gaps[i], bead);
            if (scale < 1f) targets[i] = scale;
        }

        // Hold the reduced flow across the whole structure rather than climbing back out of it
        // between arms. Done HERE, on the target, not in the limiter: the limiter only ramps when
        // target != commanded, so a connector whose target already says 0.75 simply holds. The
        // machine-constraint layer needs no knowledge of structures at all.
        LastHold = settings.ProximityHoldThroughStructure
            ? HoldThroughStructures(toolpath, targets)
            : 0f;

        // What the geometry ASKED for, before any anticipation gives part of it back. The limiter
        // measures its effectiveness against this, never against the reduced ask — lowering the
        // denominator to flatter the result is the exact mistake that once reported 97.4 % for a
        // 49.6 % correction.
        var wanted = settings.ProximityAnticipateExit ? (float[])targets.Clone() : targets;

        LastAnticipated = settings.ProximityAnticipateExit
            ? AnticipateExits(toolpath, targets, settings)
            : 0f;

        s_last   = [.. runs.Select(r => r.ToPublic())];
        LastSlew = FlowSlewLimiter.Apply(toolpath, targets, settings, wanted);
    }

    private static int s_anticipationSkipped;

    /// <summary>
    /// Structures left alone by the exit anticipation because they were shorter than twice the lead —
    /// releasing their tail would have cancelled the correction instead of merely retiming it. Not a
    /// silent cap: <c>flow-slew</c> prints it.
    /// </summary>
    public static int LastAnticipationSkipped => s_anticipationSkipped;

    /// <summary>
    /// Bead length (mm) of CROWDED bead whose target was returned to full flow early so the climb
    /// finishes by the structure exit. This is correction deliberately given up — the cost side of
    /// <see cref="SliceSettings.ProximityAnticipateExit"/>, and it must never be read as delivery.
    /// </summary>
    public static float LastAnticipated { get; private set; }

    /// <summary>Bead length (mm) whose target was HELD rather than measured — uncrowded bead inside a
    /// structure that is deliberately kept at the reduced flow. Diagnostics.</summary>
    public static float LastHold { get; private set; }

    /// <summary>
    /// Extends each crowded target forward across the uncrowded bead that sits INSIDE the same
    /// structure, so flow is reduced once on entering and restored once on leaving.
    ///
    /// <para><b>Why.</b> The arms are the crowded part, but between them the path runs out onto the
    /// angled sleeve, which is not crowded and so asked for full flow. The result was a ramp down
    /// into every arm and back up out of every arm — an oscillation that never settles, when the
    /// right answer is to stay put. The sleeve is not over-deposited by holding, so holding costs
    /// nothing and removes every intermediate transition.</para>
    ///
    /// <para><b>How the structure is bounded, without a length filter.</b> A length or time-based
    /// "keep it down for N mm after an arm" rule would fire in places that have nothing to do with a
    /// structure. Instead the boundary is the <b>contiguous extrusion chain</b>: a travel means the
    /// tool lifted and went somewhere else, and a spatial discontinuity or a layer boundary means the
    /// same. Within one chain, the structure spans from the FIRST qualifying crowded move to the
    /// LAST, and every uncrowded move between them inherits the last crowded target seen. Entering is
    /// therefore exactly Jeff's formulation: the first time crowding shows up on a long run, we are
    /// in the structure.</para>
    ///
    /// <para>⚠️ <b>Measured caveat.</b> On the validation part there are <b>zero travel moves in all
    /// 392 layers</b> — each layer is one continuous chain — so there the chain IS the layer and the
    /// span ends at the fourth arm, which is the intended behaviour. The travel boundary is what
    /// generalises to several structures on one bed. But two genuinely separate structures printed as
    /// ONE continuous chain with no travel between them would be bridged, holding reduced flow across
    /// the gap between them. Nothing in this geometry does that; if a part ever shows it, the chain
    /// rule is the thing to sharpen.</para>
    ///
    /// <para>Brim is never held — it is a bed-adhesion feature and deliberately adjacent.</para>
    /// </summary>
    /// <returns>Bead length (mm) whose target was held rather than measured.</returns>
    /// <summary>
    /// Starts the climb back to full flow EARLY, so flow arrives at full width exactly as the
    /// structure ends rather than a ramp-length after it.
    ///
    /// <para><b>Why.</b> The limiter deliberately has no lookahead: it spends the ramp only on moves
    /// that want the change, so leaving a structure under-extrudes the wall beyond it until flow
    /// recovers. That was 59.7 m of lean wall on the validation part even with the structure hold in
    /// place. Anticipating the exit trades a slice of the correction for a wall that is right from
    /// the first millimetre.</para>
    ///
    /// <para><b>What it costs, and why the rate matters so much.</b> The lead is however far the
    /// machine travels while the flow climbs, so it is set by
    /// <see cref="SliceSettings.MaxFlowChangePercentPerSecond"/> and the print speed — NOT by
    /// geometry. At the old 2 %/s that was ~1,380 mm at 92 mm/s: two arm lengths of crowded bead
    /// deliberately over-fed, to save 59.7 m of lean wall. A losing trade, and why this was parked.
    /// At 15 %/s it is ~245 mm, which is what makes it worth having at all.</para>
    ///
    /// <para><b>Bounded by the structure, never beyond it.</b> The climb may reach back only as far
    /// as the structure's own entry: anticipating past it would run rich on bead belonging to
    /// whatever came before, which no measurement here justifies. Where the structure is shorter
    /// than the lead, flow arrives late and the shortfall is simply reported rather than paid for
    /// out of a neighbouring feature.</para>
    ///
    /// <para>⚠️ The returned length is CROWDED bead given back. It is a cost, not delivery. The
    /// limiter is handed the pre-anticipation targets so its effectiveness still measures against
    /// what the geometry asked for.</para>
    /// </summary>
    /// <returns>Bead length (mm) of crowded bead returned to full flow ahead of the exit.</returns>
    internal static float AnticipateExits(Toolpath toolpath, float[] targets, SliceSettings settings)
    {
        float ratePerSec = MathF.Max(settings.MaxFlowChangePercentPerSecond, 0f) / 100f;
        if (ratePerSec <= 0f) return 0f;

        float nominalMmS = MathF.Max(settings.PrintSpeedMps * 1000f, 1e-3f);
        double given     = 0.0;
        int    layerBase = 0;
        s_anticipationSkipped = 0;

        foreach (var layer in toolpath.Layers)
        {
            var moves = layer.Moves;
            int chainStart = 0;
            Vector3? prevEnd = null;

            void CloseChain(int endExclusive)
            {
                // The structure is the span of reduced target within this chain, same boundary the
                // hold uses. Its LAST reduced move is the exit.
                int first = -1, last = -1;
                for (int i = chainStart; i < endExclusive; i++)
                    if (targets[layerBase + i] < 1f - 1e-5f) { if (first < 0) first = i; last = i; }

                if (first < 0 || last < first) return;

                // Time the climb needs, walked at the same step size the limiter will use, so the
                // schedule the limiter follows lands on full flow exactly at the exit.
                float hold = FlowSlewLimiter.HoldSeconds(ratePerSec);
                float step = MathF.Max(ratePerSec * hold, 1e-4f);
                float from = targets[layerBase + last];
                int   steps = 0;
                for (float v = MathF.Max(from, 1e-3f); v < 1f - 1e-5f; v *= 1f + step) steps++;
                float leadSeconds = steps * hold;
                if (leadSeconds <= 0f) return;

                // ⚠️ Never anticipate a structure that is not comfortably longer than the lead.
                // Releasing the tail of a SHORT structure eats the whole correction: a 120 mm crowded
                // run with a 267 mm lead would be handed back end-to-end and come out at full flow,
                // which is worse than arriving late. Correcting the bead is the point; arriving on
                // time is the refinement, so when the two conflict the correction wins.
                float spanSeconds = 0f;
                for (int i = first; i <= last; i++)
                {
                    var sm = moves[i];
                    if (sm.Kind != MoveKind.Extrude || sm.IsBrim || sm.IsWipe) continue;
                    spanSeconds += Vector3.Distance(sm.From, sm.To)
                                 / MathF.Max(nominalMmS * MathF.Max(sm.PrintSpeedScale, 1e-3f), 1e-3f);
                }
                if (spanSeconds < 2f * leadSeconds) { s_anticipationSkipped++; return; }

                // Walk back from the exit, releasing moves to full flow until the lead is covered.
                // Never past `first`: the structure's own entry is the limit.
                float covered = 0f;
                for (int i = last; i >= first && covered < leadSeconds; i--)
                {
                    var m = moves[i];
                    if (m.Kind != MoveKind.Extrude || m.IsBrim || m.IsWipe) continue;

                    float len = Vector3.Distance(m.From, m.To);
                    float sec = len / MathF.Max(nominalMmS * MathF.Max(m.PrintSpeedScale, 1e-3f), 1e-3f);

                    if (targets[layerBase + i] < 1f - 1e-5f) given += len;
                    targets[layerBase + i] = 1f;
                    covered += sec;
                }
            }

            for (int mi = 0; mi < moves.Count; mi++)
            {
                var move = moves[mi];
                bool breaksChain = move.Kind == MoveKind.Travel
                                   || (prevEnd is { } pe && Vector3.Distance(pe, move.From) > 1e-3f);

                if (breaksChain)
                {
                    CloseChain(mi);
                    chainStart = move.Kind == MoveKind.Travel ? mi + 1 : mi;
                }

                prevEnd = move.Kind == MoveKind.Travel ? null : move.To;
            }

            CloseChain(moves.Count);
            layerBase += moves.Count;
        }

        return (float)given;
    }

    internal static float HoldThroughStructures(Toolpath toolpath, float[] targets)
    {
        double held = 0.0;
        int    layerBase = 0;

        foreach (var layer in toolpath.Layers)
        {
            var moves = layer.Moves;
            int chainStart = 0;                 // LAYER-LOCAL index; chains never cross layers
            Vector3? prevEnd = null;

            // Hold the last crowded target across the uncrowded bead inside this chain's span.
            void CloseChain(int endExclusive)
            {
                int first = -1, last = -1;
                for (int i = chainStart; i < endExclusive; i++)
                    if (targets[layerBase + i] < 1f - 1e-5f) { if (first < 0) first = i; last = i; }

                if (first < 0 || last <= first) return;

                float carry = targets[layerBase + first];
                for (int i = first; i <= last; i++)
                {
                    if (targets[layerBase + i] < 1f - 1e-5f) { carry = targets[layerBase + i]; continue; }

                    var m = moves[i];
                    if (m.IsBrim || m.Kind != MoveKind.Extrude) continue;

                    targets[layerBase + i] = carry;
                    held += Vector3.Distance(m.From, m.To);
                }
            }

            for (int mi = 0; mi < moves.Count; mi++)
            {
                var move = moves[mi];

                bool breaksChain = move.Kind == MoveKind.Travel
                                   || (prevEnd is { } pe && Vector3.Distance(pe, move.From) > 1e-3f);

                if (breaksChain)
                {
                    CloseChain(mi);
                    // A travel is not part of either chain; a spatial jump starts a chain AT this move.
                    chainStart = move.Kind == MoveKind.Travel ? mi + 1 : mi;
                    prevEnd    = move.Kind == MoveKind.Travel ? null : move.To;
                    continue;
                }

                prevEnd = move.To;
            }

            CloseChain(moves.Count);            // a layer boundary ends the chain
            layerBase += moves.Count;
        }

        LastHold = (float)held;
        return (float)held;
    }

    /// <summary>A run under construction — carries the contiguous move count so Apply can reach
    /// every move in it without re-walking.</summary>
    private readonly record struct RunBuild(
        int LayerIndex, float Z, float LengthMm, float ClosestGapMm, float MeanScale,
        bool Corrected, int FirstFlatIndex, int MoveCount)
    {
        public Run ToPublic() =>
            new(LayerIndex, Z, LengthMm, ClosestGapMm, MeanScale, Corrected, FirstFlatIndex);
    }

    /// <summary>
    /// Groups crowded moves into continuous runs. A run ends at a travel, at a non-crowded move, at
    /// a brim move, or at a layer boundary — anywhere the bead genuinely stops running alongside
    /// its neighbour. Contiguous in flat index by construction, which is what lets Apply address
    /// the whole run from its first index and a count.
    /// </summary>
    private static List<RunBuild> FindRuns(
        Toolpath toolpath, float[] gaps, float beadWidthMm, float minRunMm)
    {
        var runs = new List<RunBuild>();

        int    first = -1, count = 0;
        double len = 0.0, scaleSum = 0.0;
        float  closest = float.PositiveInfinity;
        int    layerIndex = 0;
        float  layerZ = 0f;
        Vector3? prevEnd = null;   // where the open run's last bead finished

        void Close()
        {
            if (count > 0)
                runs.Add(new RunBuild(
                    layerIndex, layerZ, (float)len, closest,
                    (float)(scaleSum / count), len >= minRunMm, first, count));
            first = -1; count = 0; len = 0.0; scaleSum = 0.0;
            closest = float.PositiveInfinity;
            prevEnd = null;
        }

        int flat = 0;
        for (int li = 0; li < toolpath.Layers.Count; li++)
        {
            var layer = toolpath.Layers[li];
            layerIndex = li;
            layerZ     = layer.Z;

            foreach (var move in layer.Moves)
            {
                bool crowded = ToolpathMoveKinds.IsCutSegment(move.Kind)
                               && !move.IsBrim
                               && !float.IsNaN(gaps[flat]);

                if (crowded)
                {
                    // Contiguity: a run must be an unbroken index range, so a gap in the indices
                    // starts a new run rather than extending this one.
                    if (count > 0 && flat != first + count) Close();

                    // And unbroken in SPACE. Two different features whose beads happen to be
                    // adjacent in the move list are not one stretch — merging them would pool
                    // their lengths, so two short stretches that each deserve to be left alone
                    // could jointly clear the threshold and both get corrected.
                    if (prevEnd is { } pe && Vector3.Distance(pe, move.From) > 1e-3f) Close();

                    if (count == 0) first = flat;
                    count++;
                    len      += Vector3.Distance(move.From, move.To);
                    scaleSum += BeadProximity.ScaleForGap(gaps[flat], beadWidthMm);
                    if (gaps[flat] < closest) closest = gaps[flat];
                    prevEnd = move.To;
                }
                else Close();

                flat++;
            }
            Close();   // a layer boundary ends any open run
        }
        return runs;
    }

    /// <summary>One-line summary for the console and the status bar.</summary>
    public static string Describe(IReadOnlyList<Run> runs, float beadWidthMm)
    {
        if (runs.Count == 0) return "No bead runs alongside another closer than a bead width.";

        int corrected = 0;
        double corrLen = 0.0, skipLen = 0.0;
        float worst = float.PositiveInfinity;
        foreach (var r in runs)
        {
            if (r.Corrected) { corrected++; corrLen += r.LengthMm; }
            else skipLen += r.LengthMm;
            if (r.ClosestGapMm < worst) worst = r.ClosestGapMm;
        }
        return $"{corrected} of {runs.Count} crowded runs corrected ({corrLen / 1000.0:0.###} m of "
             + $"bead; {skipLen / 1000.0:0.###} m left alone as too short to act on). Closest "
             + $"parallel gap {worst:0.##} mm against a {beadWidthMm:0.##} mm bead.";
    }
}
