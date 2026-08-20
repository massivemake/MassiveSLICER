namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Caps how much layer thickness may change between adjacent layers, turning a thickness cliff
/// into a ramp.
///
/// <para><b>Why.</b> <see cref="AdaptiveLayerHeights"/> and <see cref="SupportDrivenLayerHeights"/>
/// each choose a layer's thickness from the geometry at that Z and never look at what the
/// neighbouring layers got, so nothing prevents 4.00 → 2.61 → 4.00 on consecutive layers. Extruder
/// RPM follows real thickness while robot speed usually does not, so a thickness cliff arrives at
/// the machine as an RPM cliff — measured 25.5 RPM points across one layer boundary on a 392-layer
/// column at flat 85 mm/s. Against an extruder transport lag of order seconds, a step that size
/// cannot land where it was commanded.</para>
///
/// <para><b>The one invariant: a layer only ever gets THINNER.</b> Never thicker than the ladder
/// already proposed for it. Both bounds it might be fighting — the stairstep tolerance and the
/// overlap target — are upper bounds on thickness, so thinning can never violate either. That is
/// what makes this free rather than a trade-off, and it is why the layer that genuinely needs to
/// be thin still receives its thickness: the ramp is spent on its NEIGHBOURS.</para>
///
/// <para><b>Why a backward pass is required.</b> Limiting rises alone is not enough. Walking up
/// into a thin region, the thin layer is a hard constraint that arrives suddenly; to reach it
/// gently the layers BELOW must already be thinning. So the descent has to be planned before the
/// walk starts, which is what <see cref="BuildCeiling"/> does — for each Z it answers "how thick
/// may a layer be here and still step down, one cap per layer, to every constraint above it".
/// Smoothing only one direction never converges and reports phantom leftover violations.</para>
/// </summary>
public static class LayerHeightSlewLimiter
{
    /// <summary>
    /// Rewrites a slice-plane ladder so no two adjacent layers differ in thickness by more than
    /// <paramref name="maxChangeMm"/>.
    /// </summary>
    /// <param name="proposed">
    /// The ladder the thickness rules produced. Read as an upper bound per Z, never exceeded.
    /// </param>
    /// <param name="zMax">Top of the part. Thinner layers cover less height, so this emits MORE
    /// layers than it was given — the walk runs until the part is covered.</param>
    /// <param name="maxChangeMm">Cap in mm. &lt;= 0 returns <paramref name="proposed"/> unchanged.</param>
    /// <param name="minLayerHeight">Floor, matching the rules upstream.</param>
    /// <param name="maxLayerHeight">Nominal thickness — the ceiling.</param>
    public static float[] Apply(
        float[] proposed,
        float zMax,
        float maxChangeMm,
        float minLayerHeight,
        float maxLayerHeight)
    {
        if (maxChangeMm <= 1e-4f || proposed.Length < 3) return proposed;

        // ⚠️ Iterated, and it has to be. BuildCeiling plans the descent at one cap per layer of
        // the ladder it is handed, but the walk EMITS a different ladder — thinner layers, more of
        // them. A thick emitted layer can span several thin intervals of the input ladder, so the
        // ceiling drops by more than one cap across a single emitted step and the cap is violated.
        // Measured on a real 390-layer column: a 0.5 mm cap produced a 1.0 mm step, worse than the
        // uncapped ladder it started from.
        //
        // Re-deriving the ceiling from the ladder the previous pass produced makes plan-space and
        // emitted-space agree. Each pass only ever thins, and thickness is bounded below by the
        // floor, so this converges — in practice within two or three passes.
        var current = proposed;
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            int n = current.Length - 1;
            if (n < 2) break;

            var h = new float[n];
            for (int i = 0; i < n; i++) h[i] = current[i + 1] - current[i];

            var ceiling = BuildCeiling(h, maxChangeMm);

            // Already inside the cap. On the first pass return the ORIGINAL array rather than a
            // re-walked copy, so a ladder that needed no smoothing comes out bit-identical
            // instead of nudged by float rounding.
            //
            // ⚠️ Tested on the LADDER, not on the ceiling. The ceiling only ever constrains
            // DESCENTS — thinning ahead of a thin region above — because a rise is handled in the
            // walk by `prev + cap`. So `ceiling[i] < h[i]` is blind to a ladder whose only
            // violations are rises, and reading it as "nothing to do" returned such a ladder
            // untouched. Measured live: a 390-layer column with a 1.136 mm rise came back
            // unchanged at both a 1.0 mm and a 0.5 mm cap, silently doing nothing.
            //
            // Index starts at 1 because the layer-0 -> layer-1 step is the documented exemption.
            bool binds = false;
            for (int i = 2; i < n; i++)
                if (MathF.Abs(h[i] - h[i - 1]) > maxChangeMm + 1e-5f) { binds = true; break; }
            if (!binds) return pass == 0 ? proposed : current;

            current = Walk(current, ceiling, h[0], zMax, maxChangeMm, minLayerHeight, maxLayerHeight);
        }
        return current;
    }

    /// <summary>How many plan/walk passes before giving up. Convergence is monotone; this is a
    /// backstop, not a tuning knob.</summary>
    private const int MaxPasses = 8;

    /// <summary>
    /// Walks the ladder bottom-up, taking the thinner of what the ceiling allows here and one cap
    /// above the previous layer, and keeps going until the part is covered — thinner layers cover
    /// less height, so this emits more of them than it was given.
    /// </summary>
    private static float[] Walk(
        float[] ladder, float[] ceiling, float firstLayerH, float zMax,
        float maxChangeMm, float minLayerHeight, float maxLayerHeight)
    {
        var outZ = new List<float> { ladder[0] };
        float z    = ladder[0];
        float prev = 0f;
        bool  isFirst = true;

        // A thinning walk emits more layers than it was handed; the same headroom
        // SupportDrivenLayerHeights.Refine uses, for the same reason.
        int guard = ladder.Length * 4 + 64;

        while (z < zMax - 1e-4f && outZ.Count < guard)
        {
            float want = CeilingAt(ceiling, ladder, z);

            // The first layer keeps its height exactly. FirstLayerHeight is a deliberate adhesion
            // setting, not a thickness the smoother is entitled to spend on a ramp.
            float thickness = isFirst ? firstLayerH : MathF.Min(want, prev + maxChangeMm);
            thickness = Math.Clamp(thickness, minLayerHeight, maxLayerHeight);

            z += thickness;
            outZ.Add(z);
            prev    = thickness;
            isFirst = false;
        }
        return [.. outZ];
    }

    /// <summary>
    /// For each layer, the greatest thickness it may take while still being able to step down to
    /// every constraint above it at no more than one cap per layer. This is the lookahead: it is
    /// what makes the ladder start thinning BEFORE it reaches the layer that needs to be thin.
    ///
    /// Computed top-down, and only ever reduces — <c>Min(h[i], ceiling[i+1] + cap)</c>.
    /// </summary>
    internal static float[] BuildCeiling(float[] h, float maxChangeMm)
    {
        int n = h.Length;
        var ceiling = new float[n];
        ceiling[n - 1] = h[n - 1];
        for (int i = n - 2; i >= 0; i--)
            ceiling[i] = MathF.Min(h[i], ceiling[i + 1] + maxChangeMm);

        // The first layer is exempt, so a deliberate FirstLayerHeight is never treated as a cliff
        // to be ramped away.
        //
        // ⚠️ This means the layer-0 → layer-1 boundary is the ONE place the cap may be exceeded:
        // if the geometry needs a descent that has to begin at the very bottom, the first layer
        // cannot join the ramp, so the second layer takes a bigger step than the cap. Keeping
        // FirstLayerHeight intact is the deliberate trade — it is a bed-adhesion setting the user
        // chose, the bottom of the print is where the extruder is priming anyway, and the brim
        // sits there. Everything above this boundary does honour the cap.
        ceiling[0] = h[0];
        return ceiling;
    }

    /// <summary>
    /// The ceiling near this height. The ladder shifts as layers are thinned, so the walk no
    /// longer lines up with the input ladder's entries; thickness varies smoothly with Z, so
    /// sampling the interval this Z falls in is accurate enough — the same approximation
    /// <see cref="SupportDrivenLayerHeights"/> documents for the same reason.
    /// </summary>
    private static float CeilingAt(float[] ceiling, float[] ladder, float z)
    {
        for (int i = 1; i < ladder.Length; i++)
            if (ladder[i] > z + 1e-4f)
                return ceiling[Math.Min(i - 1, ceiling.Length - 1)];
        return ceiling[^1];
    }

    /// <summary>
    /// Worst adjacent thickness change in a ladder (mm). Diagnostics.
    /// </summary>
    /// <param name="skipFirstLayerBoundary">
    /// Exclude the layer-0 → layer-1 step, which <see cref="BuildCeiling"/> deliberately leaves
    /// uncapped so a chosen FirstLayerHeight survives. Pass true to measure what the limiter
    /// actually promises; false (the default) reports every boundary honestly.
    /// </param>
    public static float WorstChangeMm(float[] zPositions, bool skipFirstLayerBoundary = false)
    {
        if (zPositions.Length < 3) return 0f;
        float worst = 0f;
        for (int i = skipFirstLayerBoundary ? 3 : 2; i < zPositions.Length; i++)
        {
            float a = zPositions[i - 1] - zPositions[i - 2];
            float b = zPositions[i]     - zPositions[i - 1];
            float d = MathF.Abs(b - a);
            if (d > worst) worst = d;
        }
        return worst;
    }
}
