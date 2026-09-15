using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>How the pattern coordinate wraps around the part.</summary>
public enum PatternMappingMode
{
    /// <summary>Distance along the printed contour — cycles are evenly spaced in mm
    /// along the whole path regardless of shape.</summary>
    ArcLength,
    /// <summary>Polar angle around the part centre (classic OGcode cylindrical wrap) —
    /// stretches where the wall is far from centre, compresses where it is close.</summary>
    Radial,
    /// <summary>Fixed physical wavelength in mm along the path, phase-anchored at the
    /// seam — identical cycle size on every layer; the fractional remainder lands at
    /// the seam line instead of smearing into diagonal bands.</summary>
    Wavelength,
}

/// <summary>Decorative wall patterns (ported from the MassiveCODE effector project).</summary>
public enum PatternType
{
    Smooth, Sine, Diamond, Polygon, Ripple, HWave, VWave, Bumps,
    Bubbles, Pleats, Voronoi, Hexagon, Triangle, Guilloche, Hammered, Sunflower
}

/// <summary>
/// Toolpath post-processor that displaces contour walls with a decorative pattern —
/// a direct port of the MassiveCODE (OGcode) pattern engine. Each point is displaced
/// along the horizontal contour normal by <c>amplitude · fade(z) · P(θ + twist·z − offset, z)</c>,
/// where θ is the polar angle around the part centre and P is one of 16 pattern functions.
/// Runs after <see cref="WaveEffect"/> in the slicing pipeline.
/// </summary>
public static class PatternEffect
{
    private const float TwoPi = 2f * MathF.PI;

    /// <summary>
    /// How much better the best outline fit has to be than a typical one before it is
    /// believed. 1.0 means no better than average — the outlines fit equally well anywhere
    /// and the shift is meaningless.
    /// </summary>
    private const float MinSharpness = 1.6f;

    /// <summary>
    /// How many places around the loop the shift is measured. Enough to follow the wall
    /// stretching unevenly, few enough that each window still holds sufficient outline to
    /// match on.
    /// </summary>
    /// <summary>
    /// How far a layer's wave may sit from the one below before it stops reading as
    /// alternating, as a fraction of a turn.
    ///
    /// Half a turn is the real limit: beyond 180 degrees there is no telling opposed from
    /// stacked, so the pattern has lost the plot whatever it does. A quarter turn sounds
    /// safer and is wrong — on this part the loop already moves 97 degrees' worth at 40% up,
    /// in a stretch that looks right, so a quarter-turn ceiling starts easing off over half
    /// the part for no reason.
    /// </summary>
    private const float MaxPhaseErrorTurns = 0.5f;

    /// <summary>
    /// The most a single change may coarsen the pattern, as a fraction of the count it is
    /// leaving. A quarter still reads as the part narrowing; going several times over in one
    /// layer reads as a different pattern starting, however well it lines up.
    /// </summary>
    private const float MaxStepFraction = 0.25f;

    /// <summary>How long instability must persist before it counts as the taper, in layers.</summary>
    private const int Sustained = 25;

    /// <summary>
    /// How much of what remains above the strict limit to keep the requested count for
    /// anyway. The pattern drifts there rather than breaking, and drifting as the pattern
    /// asked for beats handing over early to something else.
    /// </summary>
    private const float WavyHandoverDelay = 0.5f;

    /// <summary>Where each candidate count would hand over: (count, layer, total).</summary>
    public static readonly List<(int Count, int Layer, int Total)> HandoverByCount = [];

    /// <summary>The cycle count each layer was given, for `sinecheck`.</summary>
    public static readonly List<(int Layer, int Cycles)> CyclePlan = [];

    /// <summary>Layers that gave up on a cycle count and went wavy: (layer, long, short).</summary>
    public static readonly List<(int Layer, int Long, int Short)> WavyLayers = [];

    /// <summary>
    /// Layers where tracking the wall below did NOT land on the requested cycle count, as
    /// (layer index, count that came out). Tracking should produce the right number by
    /// itself; a mismatch means the reading went wrong, and that layer keeps evenly spaced
    /// cycles instead of a correction built on a bad measurement. Read by `sinecheck`.
    /// </summary>
    public static readonly List<(int Layer, float Cycles)> CycleCountWarnings = [];

    /// <summary>
    /// Layers whose outline could not be matched against the one below with confidence,
    /// as (layer index, how sharp the best fit was). These carry the previous phase forward
    /// instead of trusting a match that means nothing. Read by `sinecheck`.
    /// </summary>
    public static readonly List<(int Layer, float Sharpness)> RegistrationWarnings = [];

    /// <summary>
    /// Per layer: the outline shift found, and how sharp that fit was. The shift should
    /// change SMOOTHLY from layer to layer, because the wall does. Jitter here is the fit
    /// being noisy — and the wave multiplies it by the cycle count, so a jitter of a
    /// thousandth of a loop is most of a cycle of phase.
    /// </summary>
    public static readonly List<(int Layer, float Shift, float Sharpness)> ShiftLog = [];

    public static Toolpath Apply(Toolpath toolpath, SliceSettings settings)
    {
        bool effectorActive = settings.EffectorPoints.Count > 0
            && (settings.EffectorMode == EffectorMode.Erase || settings.EffectorStrengthMm > 0f);
        if (settings.PatternType == PatternType.Smooth ||
            (settings.PatternAmplitude <= 0f && !effectorActive))
            return toolpath;
        if (toolpath.Layers.Count == 0) return toolpath;
        CycleCountWarnings.Clear();
        RegistrationWarnings.Clear();
        ShiftLog.Clear();

        // -- Model frame: XY centre, z range, mean radius --------------------
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var layer in toolpath.Layers)
            foreach (var m in layer.Moves)
            {
                if (m.Kind != MoveKind.Extrude) continue;
                minX = MathF.Min(minX, m.To.X); maxX = MathF.Max(maxX, m.To.X);
                minY = MathF.Min(minY, m.To.Y); maxY = MathF.Max(maxY, m.To.Y);
                minZ = MathF.Min(minZ, m.To.Z); maxZ = MathF.Max(maxZ, m.To.Z);
            }
        if (minX > maxX) return toolpath;

        var ctx = new PatternContext
        {
            Type      = settings.PatternType,
            Amplitude = settings.PatternAmplitude,
            Frequency = MathF.Max(0.5f, settings.PatternFrequency),
            TwistRad  = settings.PatternTwistDegPerMm * MathF.PI / 180f,
            OffsetRad = settings.PatternOffsetDeg * MathF.PI / 180f,
            FadeIn    = MathF.Max(0f, settings.PatternFadeInMm),
            FadeOut   = MathF.Max(0f, settings.PatternFadeOutMm),
            Effectors = settings.EffectorPoints,
            EffectorRadius   = MathF.Max(1e-3f, settings.EffectorRadiusMm),
            EffectorStrength = MathF.Max(0f, settings.EffectorStrengthMm),
            EffectorMode     = settings.EffectorMode,
            Cx        = (minX + maxX) * 0.5f,
            Cy        = (minY + maxY) * 0.5f,
            ZMin      = minZ,
            Height    = MathF.Max(1f, maxZ - minZ),
            Radius    = MathF.Max(1f, ((maxX - minX) + (maxY - minY)) * 0.25f),
        };
        // Cell size (mm) used by the tiled patterns — one cell per frequency division.
        ctx.CellMm = MathF.Max(2f, TwoPi * ctx.Radius / ctx.Frequency);
        if (ctx.Type == PatternType.Sunflower) ctx.BuildSunflower();

        // Sine cycles-per-layer owns the whole mapping: a fixed number of whole cycles
        // spread evenly along each layer's own path. That IS even/path-length mapping with
        // the count pinned, so it overrides the Distribution setting rather than sitting
        // beside it — anything else would let Distribution silently break the guarantee.
        bool sineCycles = settings.PatternType == PatternType.Sine
                       && settings.PatternSineCyclesPerLayer > 0;
        // Cycle counts are planned for the whole part up front, not decided layer by layer:
        // knowing what is above is the only way to start easing off early enough.
        int[]? cyclePlan = sineCycles
            ? PlanCycleCounts(toolpath, settings, settings.PatternSineCyclesPerLayer)
            : null;

        // The wavelength the sine was running at where the taper took over, so the wavy
        // ending can continue at that size rather than arriving at a different scale.
        float handoverWavelength = 0f;
        if (cyclePlan is not null)
            for (int i = 0; i < cyclePlan.Length; i++)
                if (cyclePlan[i] < 0)
                {
                    int from = Math.Clamp(-cyclePlan[i], 0, toolpath.Layers.Count - 1);
                    float loop = 0f;
                    foreach (var (cs, ce) in Chains(toolpath.Layers[from]))
                    {
                        float len = 0f;
                        for (int k = cs; k < ce; k++)
                            len += Vector3.Distance(toolpath.Layers[from].Moves[k].From,
                                                    toolpath.Layers[from].Moves[k].To);
                        if (len > loop) loop = len;
                    }
                    handoverWavelength = loop / MathF.Max(1, settings.PatternSineCyclesPerLayer);
                    break;
                }
        CyclePlan.Clear();
        WavyLayers.Clear();
        if (sineCycles) ctx.Frequency = settings.PatternSineCyclesPerLayer;

        bool arcMode = sineCycles || settings.PatternMapping != PatternMappingMode.Radial;
        bool wavelengthMode = !sineCycles && settings.PatternMapping == PatternMappingMode.Wavelength;
        float wavelength = MathF.Max(2f, settings.PatternWavelengthMm);
        if (wavelengthMode)
            ctx.CellMm = wavelength;   // square texture cells: z cell = path cell

        var scope = settings.PatternScope;

        var result = new Toolpath();
        for (int layerOrdinal = 0; layerOrdinal < toolpath.Layers.Count; layerOrdinal++)
        {
            var layer = toolpath.Layers[layerOrdinal];

            var newLayer = new ToolpathLayer(layer.Index, layer.Z)
                { Height = layer.Height, PlaneNormal = layer.PlaneNormal, ThermalTempC = layer.ThermalTempC };
            newLayer.Contours.AddRange(layer.Contours);

            // Arc-length mode: pre-walk the layer's contour chains so each sample knows
            // its distance along the loop. u = 2π·(distance − anchor)/total gives evenly
            // spaced cycles; the anchor (vertex nearest world +X from the part centre)
            // keeps the phase aligned layer over layer even as seams wander.
            ChainInfo[]? chainOf = arcMode ? BuildChains(layer, ctx, wavelengthMode, wavelength) : null;

            // Where this layer's sine opens.
            //
            // Straight off the layer's own parity, against an origin pinned to world geometry:
            // the exact point where the loop crosses the +X ray, interpolated rather than
            // snapped to a vertex. Every layer measures that for itself.
            //
            // It used to be inherited instead — each layer registered its outline against the
            // one below and took that phase plus half a cycle. The fits were excellent, but
            // the answer was a chain, and the wall's parameterisation genuinely slides about
            // 0.06% of the loop per layer (0.4% at the 90th percentile), which at 200 cycles
            // is 46 degrees of phase typically and 282 at the tail. Each link carried a little
            // error and 798 links compounded it into a random walk: mostly wrong, occasionally
            // back near right by luck. Reading the origin from world geometry breaks the chain
            // — a layer can only be as wrong as its own crossing, never as wrong as every
            // layer below it put together.
            if (sineCycles)
            {

                // Once the count the layer can carry has fallen to a quarter of what was
                // asked for, the pattern has stopped being the pattern anyway — holding the
                // pretence just means a coarse sine that reads as a different object bolted
                // on. Past that, stop counting cycles and simply be wavy: two wave lengths
                // set from the bead rather than from a count, added together so it does not
                // read as one repeating thing, each closing on the loop so the seam still
                // meets. It sells the ending instead of arguing with it.
                ctx.WavyLong = ctx.WavyShort = 0;
                int planned = cyclePlan[layerOrdinal];
                if (planned < 0)
                {
                    int from = -planned;                       // layer the taper took over
                    float loop = 0f;
                    foreach (var (cs, ce) in Chains(layer))
                    {
                        float len = 0f;
                        for (int k = cs; k < ce; k++)
                            len += Vector3.Distance(layer.Moves[k].From, layer.Moves[k].To);
                        if (len > loop) loop = len;
                    }
                    float bead = MathF.Max(1f, settings.BeadWidth);

                    // Carry on at the size the sine was, so the changeover is a change of
                    // character and not of scale — the eye forgives the first and not the
                    // second. The wave only grows once the wall can no longer hold that size
                    // against the bead.
                    float wanted = MathF.Max(handoverWavelength, 2f * bead);
                    int lng  = Math.Max(3, (int)MathF.Round(loop / wanted));
                    int shrt = Math.Max(lng + 1, (int)MathF.Round(lng * 1.6f));
                    if (shrt % lng == 0) shrt++;
                    ctx.WavyLong = lng;
                    ctx.WavyShort = shrt;
                    ctx.Frequency = shrt;
                    WavyLayers.Add((layer.Index, lng, shrt));
                    CyclePlan.Add((layer.Index, -lng));
                }
                else
                {
                    ctx.Frequency = planned;
                    CyclePlan.Add((layer.Index, planned));
                }

                // Both layers put phase 0 at the anchor, so where the count changes the two
                // waves stay opposed AT the anchor and drift apart away from it — the one
                // stacked spot lands opposite it. That is a fixed line in space either way,
                // so the changes stack up one line of the part instead of scattering.
                ctx.SinePhase = (layerOrdinal & 1) == 1 ? MathF.PI : 0f;
            }

            // VisibleSkin asks a whole-layer question — "could a horizontal ray reach this" —
            // so the answer is computed once here and indexed per move below. Penetration is
            // one bead width: the geometry within a bead behind the first hit is the same
            // surface the pattern is pushing in and out.
            bool[]? interior = scope == PatternScope.VisibleSkin
                ? SkinRaycastVisibility.BuildInteriorMask(
                      layer.Moves, settings.BeadWidth, settings.BeadWidth)
                : null;

            // Displacement normals, shared at every vertex. A per-segment normal flips with
            // the tangent, so wherever the path turns a corner the two sides of that corner
            // push apart and the wall splits — 2.8 mm at a right angle under a 2 mm pattern.
            // Averaging the two adjoining normals at each shared vertex makes the wall one
            // continuous surface. (No miter scaling: a corner offsets by cos(half-angle)
            // less than a flat run, which is the safe direction — a true miter runs away.)
            var (perpFrom, perpTo) = BuildNormals(layer);

            // Skin-only: displace the wall, then carry the structure's ENDS along with it so
            // braces stay attached without being bowed. The wall is walked first to learn where
            // it moved, so this pass has to run before the emit loop below.
            var wallField = new SkinOnlyBracing.WallField();
            (Vector3 AtFrom, Vector3 AtTo)[]? structureBlend = null;
            if (scope != PatternScope.Everything)
            {
                for (int mi = 0; mi < layer.Moves.Count; mi++)
                {
                    var m = layer.Moves[mi];
                    if (SkinOnlyBracing.IsStructure(m, scope, interior, mi)) continue;
                    if (m.Kind != MoveKind.Extrude || m.IsLayerStitch) continue;
                    if (Vector3.Distance(m.From, m.To) < 1e-4f) continue;
                    var pp = perpFrom[mi];
                    if (pp.LengthSquared() < 1e-9f) continue;
                    var chainW = chainOf?[mi];
                    float? thetaW = ChainTheta(chainW, ctx, wavelengthMode,
                                               Vector3.Distance(m.From, m.To), 0f);
                    wallField.Record(m.From, m.From + pp * ctx.Displacement(m.From, thetaW));
                }
                structureBlend = SkinOnlyBracing.BlendForStructure(layer.Moves, wallField, scope, interior);
            }

            for (int mi = 0; mi < layer.Moves.Count; mi++)
            {
                var move = layer.Moves[mi];
                if (move.Kind != MoveKind.Extrude || move.IsLayerStitch)
                {
                    newLayer.Moves.Add(move);
                    continue;
                }

                // Structure under skin-only: one straight segment, ends riding the wall.
                if (structureBlend is not null && SkinOnlyBracing.IsStructure(move, scope, interior, mi))
                {
                    var (dFrom, dTo) = structureBlend[mi];
                    newLayer.Moves.Add(move with { From = move.From + dFrom, To = move.To + dTo });
                    continue;
                }

                float len = Vector3.Distance(move.From, move.To);

                var chain = chainOf?[mi];

                // Sample finely enough for the pattern's detail along the path.
                float pathPerCycle = wavelengthMode
                    ? wavelength
                    : arcMode && chain is { Total: > 1f }
                        ? chain.Total / ctx.Frequency
                        : TwoPi * ctx.Radius / ctx.Frequency;
                float spacing  = Math.Clamp(pathPerCycle / 12f, 1.0f, 6f);
                int   segments = Math.Clamp((int)MathF.Ceiling(len / spacing), 1, 2000);

                // A degenerate move has no tangent of its own, but BuildNormals already
                // gave it its neighbours'. It still has to travel with them: left where it
                // was while the wall moves out from under it, it opens a gap the width of
                // the pattern and splits the loop, which costs the layer its spiral.
                var pFrom = perpFrom[mi];
                var pTo   = perpTo[mi];
                if (pFrom.LengthSquared() < 1e-9f) { newLayer.Moves.Add(move); continue; }

                Vector3 Displaced(float t)
                {
                    var pt   = Vector3.Lerp(move.From, move.To, t);
                    var perp = Vector3.Lerp(pFrom, pTo, t);
                    perp = perp.LengthSquared() < 1e-9f ? pFrom : Vector3.Normalize(perp);
                    return pt + perp * ctx.Displacement(
                        pt, ChainTheta(chain, ctx, wavelengthMode, len, t));
                }

                for (int seg = 0; seg < segments; seg++)
                {
                    newLayer.Moves.Add(move with
                    {
                        From = Displaced(seg / (float)segments),
                        To   = Displaced((seg + 1) / (float)segments),
                    });
                }
            }
            result.Layers.Add(newLayer);

        }
        return result;
    }

    /// <summary>
    /// Pattern phase at parameter <paramref name="t"/> along a move, or null outside arc modes.
    /// Shared so the wall sampling pass and the emit pass evaluate identical phase.
    /// </summary>
    private static float? ChainTheta(ChainInfo? chain, PatternContext ctx,
        bool wavelengthMode, float len, float t)
    {
        if (chain is not { Total: > 1f }) return null;

        if (wavelengthMode)
        {
            // Constant mm wavelength, phase 0 at the chain start (the seam): theta advances
            // 2π per Frequency·λ of path, so P(θ·f) completes one cycle every λ mm.
            float d = chain.CumStart + t * len;
            return TwoPi * d / (ctx.Frequency * chain.Lambda);
        }

        float dist = chain.CumStart + t * len - chain.Anchor;
        dist -= MathF.Floor(dist / chain.Total) * chain.Total;
        float u = dist / chain.Total;

        // Corrected: cycles are no longer evenly spaced. Each has been nudged so its start
        // sits on the layer below, with the nudge blended between neighbours. Divided back
        // out by Frequency because the caller multiplies by it and then adds the start phase.
        if (chain.Warp is { Length: > 1 } warp)
        {
            float f = u * (warp.Length - 1);
            int   i = Math.Clamp((int)f, 0, warp.Length - 2);
            float phase = warp[i] + (warp[i + 1] - warp[i]) * (f - i);
            return phase / MathF.Max(1e-3f, ctx.Frequency);
        }

        return TwoPi * u;
    }

    /// <summary>
    /// Horizontal displacement normal at each move's two ends. Inside a contiguous extrude
    /// chain a shared vertex gets one normal — the mean of the two segments meeting there —
    /// so neighbouring moves push that vertex to the same place and the wall stays closed.
    /// A chain's own two ends are averaged together too when the chain is a loop.
    /// </summary>
    private static (Vector3[] From, Vector3[] To) BuildNormals(ToolpathLayer layer)
    {
        int n = layer.Moves.Count;
        var seg = new Vector3[n];          // per-segment normal, zero where undefined
        for (int i = 0; i < n; i++)
        {
            var m = layer.Moves[i];
            if (m.Kind != MoveKind.Extrude || m.IsLayerStitch) continue;
            var d = m.To - m.From;
            if (d.LengthSquared() < 1e-8f) continue;
            var p = Vector3.Cross(Vector3.Normalize(d), Vector3.UnitZ);
            if (p.LengthSquared() < 1e-9f) continue;
            seg[i] = Vector3.Normalize(p);
        }

        var from = new Vector3[n];
        var to   = new Vector3[n];
        int i0 = 0;
        while (i0 < n)
        {
            var m = layer.Moves[i0];
            if (m.Kind != MoveKind.Extrude || m.IsLayerStitch) { i0++; continue; }

            int start = i0, j = i0;
            var prevTo = m.From;
            while (j < n)
            {
                var mv = layer.Moves[j];
                if (mv.Kind != MoveKind.Extrude || mv.IsLayerStitch) break;
                if (Vector3.DistanceSquared(mv.From, prevTo) > 1.0f) break;
                prevTo = mv.To;
                j++;
            }
            bool loop = j - start > 2
                && Vector3.DistanceSquared(layer.Moves[start].From, layer.Moves[j - 1].To) <= 1.0f;

            static Vector3 Mean(Vector3 a, Vector3 b)
            {
                if (a.LengthSquared() < 1e-9f) return b;
                if (b.LengthSquared() < 1e-9f) return a;
                var s = a + b;
                return s.LengthSquared() < 1e-9f ? a : Vector3.Normalize(s);
            }

            for (int k = start; k < j; k++)
            {
                var prev = k > start ? seg[k - 1] : (loop ? seg[j - 1] : Vector3.Zero);
                var next = k + 1 < j ? seg[k + 1] : (loop ? seg[start] : Vector3.Zero);
                from[k] = Mean(seg[k], prev);
                to[k]   = Mean(seg[k], next);
            }
            i0 = Math.Max(j, i0 + 1);
        }
        return (from, to);
    }

    /// <summary>
    /// A layer's outline, resampled at evenly spaced fractions of its own loop starting from
    /// its anchor, so two layers can be compared fraction against fraction regardless of how
    /// long their paths are or where their seams fell.
    /// </summary>
    private sealed class ContourOutline
    {
        private const int N = 1024;                 // samples around the loop
        public Vector2[] P = [];

        public static ContourOutline? Build(ToolpathLayer layer, ChainInfo[]? chainOf)
        {
            if (chainOf is null) return null;

            // The longest closed loop is the wall the pattern is for.
            int bestStart = -1, bestEnd = -1; float bestLen = 0f;
            foreach (var (cs, ce) in Chains(layer))
                if (chainOf[cs] is { Closed: true, Total: > 1f } ci && ci.Total > bestLen)
                    { bestLen = ci.Total; bestStart = cs; bestEnd = ce; }
            if (bestStart < 0) return null;

            var chain = chainOf[bestStart];
            var pts = new Vector2[N];
            float walked = 0f;
            int mi = bestStart;
            float lastWant = -1f;
            for (int i = 0; i < N; i++)
            {
                float want = chain.Anchor + chain.Total * i / N;
                if (want > chain.Total) want -= chain.Total;
                if (want < lastWant) { mi = bestStart; walked = 0f; }   // sampling wraps round
                lastWant = want;
                while (mi < bestEnd - 1 &&
                       walked + Vector3.Distance(layer.Moves[mi].From, layer.Moves[mi].To) < want)
                {
                    walked += Vector3.Distance(layer.Moves[mi].From, layer.Moves[mi].To);
                    mi++;
                }
                var q = layer.Moves[Math.Clamp(mi, bestStart, bestEnd - 1)].From;
                pts[i] = new Vector2(q.X, q.Y);
            }
            return new ContourOutline { P = pts };
        }

        /// <summary>
        /// The shift, measured separately in each of several windows around the loop, rather
        /// than once for the whole thing.
        ///
        /// One shift can only turn this layer against the one below. It cannot answer a loop
        /// whose path has stretched by different amounts in different places — and that is
        /// what leaves a layer sitting a quarter of a cycle out instead of half, in some
        /// regions and not others. Measuring locally lets the answer vary the way the wall
        /// actually does.
        ///
        /// This is measured on SHAPE, not on the wave. The outline is distinctive at the scale
        /// of a window, so matching it is unambiguous; reading the wave means reading something
        /// that repeats every 20 mm, which at this density cannot tell one cycle from the next.
        /// That is why this works where sampling the phase did not.
        ///
        /// Returns one shift per window, smoothed and made to join up at the seam so the loop
        /// still carries the same whole number of cycles.
        /// </summary>
        public static float[]? FindLocalShifts(ContourOutline now, ContourOutline below,
                                               float globalShift, int windows)
        {
            int n = N;
            int half = Math.Max(8, n / windows);          // window reach either side
            int span = Math.Max(2, n / 40);               // search +/- this many samples
            int centre = (int)MathF.Round(globalShift * n);

            var shifts = new float[windows + 1];
            for (int w = 0; w < windows; w++)
            {
                int mid = (int)((long)w * n / windows);
                float bestCost = float.MaxValue; int bestS = centre;
                var costs = new float[2 * span + 1];
                for (int d = -span; d <= span; d++)
                {
                    int sft = centre + d;
                    float sum = 0f;
                    for (int k = -half; k <= half; k += 2)
                    {
                        int i = ((mid + k) % n + n) % n;
                        var a = now.P[i];
                        var b = below.P[((i + sft) % n + n) % n];
                        float dx = a.X - b.X, dy = a.Y - b.Y;
                        sum += dx * dx + dy * dy;
                    }
                    costs[d + span] = sum;
                    if (sum < bestCost) { bestCost = sum; bestS = sft; }
                }

                int bi = bestS - centre + span;
                float refine = 0f;
                if (bi > 0 && bi < 2 * span)
                {
                    float c0 = costs[bi - 1], c1 = costs[bi], c2 = costs[bi + 1];
                    float den = c0 - 2f * c1 + c2;
                    if (MathF.Abs(den) > 1e-9f) refine = Math.Clamp(0.5f * (c0 - c2) / den, -1f, 1f);
                }
                shifts[w] = (bestS + refine) / n;
            }

            // Smooth: neighbouring windows should agree closely, and a single bad one must
            // not put a kink in the wave.
            var sm = new float[windows + 1];
            for (int w = 0; w < windows; w++)
            {
                float sum = 0f; int c = 0;
                for (int k = -1; k <= 1; k++) { sum += shifts[((w + k) % windows + windows) % windows]; c++; }
                sm[w] = sum / c;
            }

            // Join up at the seam. Taking the drift back out is what keeps the cycle count
            // whole: the loop still advances exactly the requested number of cycles.
            float drift = sm[0] - sm[windows - 1];
            for (int w = 0; w < windows; w++) sm[w] -= drift * w / windows;
            sm[windows] = sm[0];
            return sm;
        }

        /// <summary>
        /// The fraction of a loop this outline has to slide to sit on top of the one below.
        ///
        /// Every candidate shift is scored by how far apart the two outlines are all the way
        /// round, so the answer is decided by the whole shape at once. A few millimetres of
        /// error at any one place barely moves a sum over a thousand of them, which is what
        /// makes this hold where asking point by point did not.
        ///
        /// <paramref name="sharpness"/> reports how much better the winner was than a typical
        /// candidate. Near 1 the outline fits equally well anywhere — a circle, say, which has
        /// no distinguishing feature to line up — and the winner means nothing.
        /// </summary>
        public static float FindShift(ContourOutline now, ContourOutline below, out float sharpness)
        {
            sharpness = 0f;
            int n = N;
            var cost = new float[n];
            const int Step = 4;                     // every 4th sample is plenty for the scan
            for (int sft = 0; sft < n; sft++)
            {
                float sum = 0f;
                for (int i = 0; i < n; i += Step)
                {
                    var a = now.P[i];
                    var b = below.P[(i + sft) & (N - 1)];
                    float dx = a.X - b.X, dy = a.Y - b.Y;
                    sum += dx * dx + dy * dy;
                }
                cost[sft] = sum;
            }

            int best = 0;
            float bestCost = float.MaxValue, mean = 0f;
            for (int i = 0; i < n; i++)
            {
                mean += cost[i];
                if (cost[i] < bestCost) { bestCost = cost[i]; best = i; }
            }
            mean /= n;
            if (bestCost <= 1e-6f) { sharpness = float.MaxValue; return best / (float)n; }
            sharpness = mean / bestCost;            // how much better than a typical fit

            // Interpolate between neighbours: the true minimum rarely lands on a whole sample,
            // and at this cycle count a fraction of a sample is still a visible slice of phase.
            float c0 = cost[(best - 1 + n) % n], c1 = cost[best], c2 = cost[(best + 1) % n];
            float denom = c0 - 2f * c1 + c2;
            float refine = MathF.Abs(denom) > 1e-9f ? 0.5f * (c0 - c2) / denom : 0f;
            refine = Math.Clamp(refine, -1f, 1f);

            float shift = (best + refine) / n;
            return shift - MathF.Floor(shift);
        }
    }

    /// <summary>
    /// How many cycles each layer may carry, worked out for the whole part before slicing
    /// any of it.
    ///
    /// The requested count holds wherever the loop is steady. It cannot hold where the loop
    /// is changing fast: if the perimeter moves 0.6% between layers, then at 200 cycles more
    /// than a whole cycle of path appears from one layer to the next, and there is simply no
    /// correspondence left for the wave to line up with — no anchor or measurement recovers
    /// it. Fewer cycles make that same movement a smaller share of a cycle, which is the only
    /// thing that actually attacks it.
    ///
    /// Two ceilings then: one so the wave stays trackable, one so a cycle stays wider than a
    /// couple of beads and can physically be drawn. The schedule is planned BACKWARDS from
    /// the top, letting the count rise at most one per layer going down. That way it never
    /// has to fall faster than one per layer — each change costs a single sweep of
    /// misalignment rather than several — and it begins dropping exactly early enough to
    /// arrive in time, and no earlier, so the requested count survives as far up as it can.
    /// </summary>
    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

    private static int[] PlanCycleCounts(Toolpath toolpath, SliceSettings settings, int requested)
    {
        int n = toolpath.Layers.Count;
        var plan = new int[n];
        if (n == 0) return plan;

        // Perimeter of each layer's main loop.
        var perim = new float[n];
        for (int i = 0; i < n; i++)
        {
            float best = 0f;
            foreach (var (cs, ce) in Chains(toolpath.Layers[i]))
            {
                float len = 0f;
                for (int k = cs; k < ce; k++)
                    len += Vector3.Distance(toolpath.Layers[i].Moves[k].From, toolpath.Layers[i].Moves[k].To);
                if (len > best) best = len;
            }
            perim[i] = best;
        }

        // Median-smoothed, so a single odd layer cannot start the whole part dropping.
        var smooth = new float[n];
        var window = new List<float>();
        for (int i = 0; i < n; i++)
        {
            window.Clear();
            for (int k = Math.Max(0, i - 7); k <= Math.Min(n - 1, i + 7); k++) window.Add(perim[k]);
            window.Sort();
            smooth[i] = window[window.Count / 2];
        }

        float bead = MathF.Max(1f, settings.BeadWidth);

        // The widest layer. Below it the part is still growing, so a loop that is changing
        // fast there is a part being built, not a part ending — nothing above needs to be
        // paid for down here, and thinning the base only spoils good geometry.
        int widest = 0;
        for (int i = 1; i < n; i++) if (smooth[i] > smooth[widest]) widest = i;

        // Above the widest point, the most cycles that stay trackable and printable.
        var ceiling = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (i <= widest || smooth[i] <= 1f) { ceiling[i] = requested; continue; }
            float change = MathF.Abs(smooth[i] - smooth[i - 1]) / smooth[i];
            if (i + 1 < n) change = MathF.Max(change, MathF.Abs(smooth[i + 1] - smooth[i]) / smooth[i]);
            int steady    = change > 1e-6f ? (int)(MaxPhaseErrorTurns / change) : requested;
            int printable = (int)(smooth[i] / (1.25f * bead));
            ceiling[i] = Math.Clamp(Math.Min(Math.Min(steady, printable), requested), 1, requested);
        }

        // One change, not a slow retreat. Easing off a cycle at a time puts a disturbance in
        // EVERY layer above where it starts, so the pattern never settles again — worse than
        // the problem. So: hold the requested count until it genuinely stops working, drop
        // straight to a number that works for the rest of the part, and stay there. One
        // visible event, and everything above it steady.
        // Trigger only where the requested count stops working AND stays not working. A part
        // can lurch for a layer or two in the middle of an otherwise steady stretch, and
        // dropping on that would coarsen a large span of perfectly good wall for nothing.
        int start = -1;
        for (int i = widest + 1; i < n; i++)
        {
            if (ceiling[i] >= requested) continue;
            int run = 0;
            for (int j = i; j < n && ceiling[j] < requested; j++) run++;
            if (run >= Sustained || i + Sustained >= n) { start = i; break; }
            i += run;                       // a passing wobble — skip past it
        }

        if (start < 0)
        {
            for (int i = 0; i < n; i++) plan[i] = requested;
            return plan;
        }

        // Hold on past the point the count strictly stops working. The wall does not fall
        // apart the instant the phase budget is spent — it drifts, and drifting while still
        // the pattern you asked for reads better than handing over early to something else.
        // So wait out a share of what is left before changing anything.
        start += (int)((n - start) * WavyHandoverDelay);
        start = Math.Min(start, n - 1);

        // What a different requested count would buy: a smaller one spends its phase budget
        // more slowly and so carries further up the part. Recorded so the answer can be read
        // off rather than guessed at.
        HandoverByCount.Clear();
        foreach (int candidate in new[] { 250, 200, 175, 150, 125, 100, 75, 50 })
        {
            int where = n;
            for (int i = widest + 1; i < n; i++)
            {
                if (smooth[i] <= 1f) continue;
                float ch = MathF.Abs(smooth[i] - smooth[i - 1]) / smooth[i];
                if (i + 1 < n) ch = MathF.Max(ch, MathF.Abs(smooth[i + 1] - smooth[i]) / smooth[i]);
                int steadyHere = ch > 1e-6f ? (int)(MaxPhaseErrorTurns / ch) : candidate;
                if (steadyHere >= candidate) continue;
                int run = 0;
                for (int j = i; j < n; j++)
                {
                    float c2 = MathF.Abs(smooth[j] - smooth[j - 1]) / MathF.Max(1f, smooth[j]);
                    int st2 = c2 > 1e-6f ? (int)(MaxPhaseErrorTurns / c2) : candidate;
                    if (st2 >= candidate) break;
                    run++;
                }
                if (run >= Sustained) { where = i; break; }
                i += run;
            }
            HandoverByCount.Add((candidate, where, n));
        }

        // One transition, not two. Stepping the count down and THEN switching to a wavy
        // ending gave two visible changes on the part — the steps bursting through the start
        // of the taper, then the switch later on. So the stepped sine goes: the requested
        // count is held while the wall supports it, and where it stops, the wavy ending takes
        // over directly. `start` is where that happens.
        for (int i = 0; i < n; i++) plan[i] = i < start ? requested : -start;
        return plan;
    }

    /// <summary>The [start, end) move ranges of each contiguous extrude chain in a layer.</summary>
    private static IEnumerable<(int Start, int End)> Chains(ToolpathLayer layer)
    {
        int i = 0;
        while (i < layer.Moves.Count)
        {
            var m = layer.Moves[i];
            if (m.Kind != MoveKind.Extrude || m.IsLayerStitch) { i++; continue; }
            int start = i, j = i;
            var prevTo = m.From;
            while (j < layer.Moves.Count)
            {
                var mv = layer.Moves[j];
                if (mv.Kind != MoveKind.Extrude || mv.IsLayerStitch) break;
                if (Vector3.DistanceSquared(mv.From, prevTo) > 1.0f) break;
                prevTo = mv.To;
                j++;
            }
            if (j > start) yield return (start, j);
            i = Math.Max(j, i + 1);
        }
    }

    /// <summary>Per-move chain data for arc-length mapping.</summary>
    private sealed class ChainInfo
    {
        public float CumStart;   // path distance at this move's From
        public float Total;      // full chain length
        public float Anchor;     // path distance of the vertex nearest world +X
        public bool  Closed;     // the chain's last point returns to its first
        public float Lambda;     // wavelength actually used (snapped on closed loops)
        public float[]? Warp;    // per-cycle correction: phase at evenly spaced path fractions
    }

    /// <summary>
    /// Segments a layer's moves into contiguous extrude chains and computes, per move,
    /// its cumulative start distance, the chain total, and the phase anchor.
    /// </summary>
    private static ChainInfo[] BuildChains(ToolpathLayer layer, PatternContext ctx,
                                           bool wavelengthMode, float wavelength)
    {
        var infos = new ChainInfo[layer.Moves.Count];
        int i = 0;
        while (i < layer.Moves.Count)
        {
            var m = layer.Moves[i];
            if (m.Kind != MoveKind.Extrude || m.IsLayerStitch) { i++; continue; }

            // Collect the contiguous chain starting here.
            int start = i;
            var cum = new List<float> { 0f };
            float total = 0f;
            float bestAngle = float.MaxValue, anchor = 0f;

            // Where the loop crosses the world +X ray out of the part centre, to the exact
            // point rather than the nearest vertex.
            //
            // This is the phase's origin, and at 200 cycles one cycle is only a couple of
            // hundredths of a loop — so snapping the origin to whichever vertex happened to
            // fall closest moves the whole wave by a large part of a cycle, differently on
            // every layer. Interpolating the crossing makes the origin a smooth function of
            // the wall, which is what lets each layer work out its own phase from world
            // geometry instead of inheriting it from the layer beneath. Nothing accumulates
            // that way: a layer can only be as wrong as its own crossing, never as wrong as
            // every layer below it added up.
            //
            // The outermost crossing is taken, so a wall that doubles back past the ray —
            // a sheet has two faces — always answers with the same one.
            float bestRadius = -1f;
            float prevAng = 0f, prevRad = 0f, prevDist = 0f;
            bool havePrev = false;

            void ConsiderAnchor(Vector3 p, float distAlong)
            {
                float dx = p.X - ctx.Cx, dy = p.Y - ctx.Cy;
                float ang = MathF.Atan2(dy, dx);
                float rad = MathF.Sqrt(dx * dx + dy * dy);
                if (havePrev && dx > 0f)
                {
                    // Sign change in Y on the +X side means the segment crossed the ray.
                    bool crossed = (prevAng <= 0f && ang >= 0f) || (prevAng >= 0f && ang <= 0f);
                    if (crossed && MathF.Abs(ang - prevAng) < MathF.PI)
                    {
                        float denom = ang - prevAng;
                        float t = MathF.Abs(denom) > 1e-9f ? -prevAng / denom : 0f;
                        t = Math.Clamp(t, 0f, 1f);
                        float r = prevRad + (rad - prevRad) * t;
                        if (r > bestRadius)
                        {
                            bestRadius = r;
                            anchor = prevDist + (distAlong - prevDist) * t;
                            bestAngle = 0f;
                        }
                    }
                }
                // Fallback for a loop that never crosses the ray: nearest vertex, as before.
                if (bestRadius < 0f)
                {
                    float a = MathF.Abs(ang);
                    if (a < bestAngle) { bestAngle = a; anchor = distAlong; }
                }
                prevAng = ang; prevRad = rad; prevDist = distAlong; havePrev = true;
            }

            ConsiderAnchor(m.From, 0f);
            int j = i;
            var prevTo = m.From;
            while (j < layer.Moves.Count)
            {
                var mv = layer.Moves[j];
                if (mv.Kind != MoveKind.Extrude || mv.IsLayerStitch) break;
                if (Vector3.DistanceSquared(mv.From, prevTo) > 1.0f) break;   // path jump ends the chain
                total += Vector3.Distance(mv.From, mv.To);
                cum.Add(total);
                ConsiderAnchor(mv.To, total);
                prevTo = mv.To;
                j++;
            }

            // A loop has to carry a whole number of cycles or the wave steps at the seam.
            // Snap the wavelength to the nearest whole fit: the cycle size drifts by at
            // most half a cycle spread over the loop, and the wall closes on itself.
            bool closed = total > 1f
                && Vector3.DistanceSquared(layer.Moves[start].From, layer.Moves[j - 1].To) <= 1.0f;
            float lambda = wavelength;
            if (wavelengthMode && closed)
            {
                float cycles = MathF.Max(1f, MathF.Round(total / wavelength));
                lambda = total / cycles;
            }

            for (int k = start; k < j; k++)
                infos[k] = new ChainInfo
                {
                    CumStart = cum[k - start], Total = total, Anchor = anchor,
                    Closed = closed, Lambda = lambda,
                };
            i = Math.Max(j, i + 1);
        }
        return infos;
    }

    // ── Pattern evaluation ──────────────────────────────────────────────────

    private sealed class PatternContext
    {
        public PatternType Type;
        public float Amplitude, Frequency, TwistRad, OffsetRad, FadeIn, FadeOut;
        public IReadOnlyList<Vector3> Effectors = [];
        public float EffectorRadius = 400f, EffectorStrength;
        public EffectorMode EffectorMode = EffectorMode.Amplify;
        public float Cx, Cy, ZMin, Height, Radius, CellMm;
        /// <summary>Half-cycle phase flip applied to Sine, alternating layer to layer.</summary>
        public float SinePhase;

        /// <summary>
        /// Past the point where an honest cycle count stops fitting the layer, the wave stops
        /// being one sine and becomes two of different lengths added together. Both are pinned
        /// to a size the nozzle can actually draw and both close on the loop, so it still meets
        /// itself at the seam — but it no longer pretends to a cycle count that the geometry
        /// cannot carry. Zero means the ordinary single sine.
        /// </summary>
        public int WavyLong, WavyShort;
        private (float theta, float z)[] _sunflower = [];
        private float _sunBumpR;

        public float Displacement(Vector3 p, float? loopTheta = null)
        {
            float z = p.Z - ZMin;
            float theta = loopTheta ?? MathF.Atan2(p.Y - Cy, p.X - Cx);
            float fade = 1f;
            if (FadeIn  > 0f) fade *= Math.Clamp(z / FadeIn, 0f, 1f);
            if (FadeOut > 0f) fade *= Math.Clamp((Height - z) / FadeOut, 0f, 1f);
            if (fade <= 0f) return 0f;
            // Live effector. Amplify: smoothstep bell boosts the local amplitude
            // (OGcode model). Erase: the pattern is simply not applied inside the
            // influence area — the amplitude is zeroed before displacement through
            // the inner region and blends back over the outer edge of the radius.
            float amp = EffectorMode == EffectorMode.Erase
                ? Amplitude * (1f - EffectorErase(p))
                : Amplitude + EffectorStrength * EffectorBell(p);
            if (amp <= 0f) return 0f;
            return amp * fade * Value(theta + TwistRad * z - OffsetRad, z);
        }

        /// <summary>Max smoothstep falloff t²(3−2t) over all effector points; 0 outside radius.</summary>
        /// <summary>
        /// Erase suppression in [0,1]: 1 (pattern fully off) anywhere within the inner
        /// 60% of the influence radius, smoothstep-blending to 0 across the outer band.
        /// The bell curve used by Amplify only hits 1 exactly AT the point — an effector
        /// hovering off the wall would never fully erase; this profile guarantees a
        /// clean flat core with a seamless transition at the boundary.
        /// </summary>
        private float EffectorErase(Vector3 p)
        {
            if (Effectors.Count == 0) return 0f;
            float best = 0f;
            foreach (var e in Effectors)
            {
                float dist = Vector3.Distance(p, e);
                if (dist >= EffectorRadius) continue;
                float prox = 1f - dist / EffectorRadius;    // 1 at the point → 0 at the edge
                float t = Math.Clamp(prox / 0.4f, 0f, 1f);  // saturates at 60% of the radius
                float s = t * t * (3f - 2f * t);
                if (s > best) best = s;
            }
            return best;
        }

        private float EffectorBell(Vector3 p)
        {
            if (Effectors.Count == 0) return 0f;
            if (EffectorStrength <= 0f) return 0f;
            float best = 0f;
            foreach (var e in Effectors)
            {
                float dist = Vector3.Distance(p, e);
                if (dist >= EffectorRadius) continue;
                float t = 1f - dist / EffectorRadius;
                float bell = t * t * (3f - 2f * t);
                if (bell > best) best = bell;
            }
            return best;
        }

        private float Value(float theta, float z) => Type switch
        {
            PatternType.Sine      => WavyLong > 0
                ? 0.62f * MathF.Sin(theta * WavyLong  + SinePhase)
                + 0.38f * MathF.Sin(theta * WavyShort + SinePhase * 0.5f)
                : MathF.Sin(theta * Frequency + SinePhase),
            PatternType.Ripple    => MathF.Sin(z / Height * Frequency * TwoPi),
            PatternType.Guilloche => Guilloche(theta, z),
            PatternType.HWave     => HWave(theta, z),
            PatternType.VWave     => VWave(theta, z),
            PatternType.Pleats    => Pleats(theta),
            PatternType.Polygon   => Polygon(theta),
            PatternType.Diamond   => SmoothTriWave(theta * Frequency / TwoPi, 0.25f) * SmoothTriWave(z / CellMm, 0.25f),
            PatternType.Bumps     => Bumps(theta, z),
            PatternType.Bubbles   => Bubbles(theta, z),
            PatternType.Voronoi   => Voronoi(theta, z),
            PatternType.Hexagon   => Hexagon(theta, z),
            PatternType.Triangle  => Triangle(theta, z),
            PatternType.Hammered  => Hammered(theta, z),
            PatternType.Sunflower => Sunflower(theta, z),
            _ => 0f,
        };

        private float Guilloche(float theta, float z)
        {
            float k = z / Height * TwoPi * MathF.Max(1f, MathF.Round(Frequency * 0.6f));
            return 0.5f * (MathF.Sin(theta * Frequency + k) + MathF.Sin(theta * Frequency - k));
        }

        private float HWave(float theta, float z)
        {
            float band   = MathF.Max(2f, CellMm);
            float swings = MathF.Max(1f, MathF.Round(Frequency));
            float zShift = 0.6f * band * MathF.Sin(theta * swings);
            return MathF.Sin((z + zShift) / band * TwoPi);
        }

        private float VWave(float theta, float z)
        {
            float swings = MathF.Max(1f, MathF.Round(Frequency));
            float thetaShift = 0.6f * MathF.Sin(z / MathF.Max(2f, CellMm) * TwoPi);
            return MathF.Sin((theta + thetaShift) * swings);
        }

        private float Pleats(float theta)
        {
            float u = Frac(theta * Frequency / TwoPi);
            const float aPos = 0.22f;
            float v = u < aPos ? u / aPos : 1f - (u - aPos) / (1f - aPos);
            return v * 2f - 1f;
        }

        private float Polygon(float theta)
        {
            int n = Math.Max(3, (int)MathF.Round(Frequency));
            float sect = TwoPi / n;
            float a = ((theta % TwoPi) + TwoPi) % TwoPi;
            float local = a - MathF.Floor(a / sect) * sect - sect / 2f;
            float cosL = MathF.Cos(local), cosS = MathF.Cos(sect / 2f);
            float rawPoly = (cosS / cosL - 1f) / (1f - cosS);
            float cosTerm = MathF.Cos(MathF.PI * local / sect);
            return rawPoly * 0.6f + (-cosTerm * cosTerm) * 0.4f;
        }

        private float Bumps(float theta, float z)
        {
            float u = Frac(theta * Frequency / TwoPi);
            float v = Frac(z / CellMm);
            float du = u - 0.5f, dv = v - 0.5f;
            float d = MathF.Sqrt(du * du + dv * dv);
            return MathF.Cos(MathF.Min(d, 0.5f) * TwoPi);
        }

        private float Bubbles(float theta, float z)
        {
            float uRaw = theta * Frequency / TwoPi;
            float vRaw = z / CellMm;
            int rowIdx = (int)MathF.Floor(vRaw);
            const float bumpR = 0.75f;
            float maxBump = -1f;
            for (int dRow = -1; dRow <= 1; dRow++)
            {
                int r = rowIdx + dRow;
                float rowOffset = (r & 1) != 0 ? 0.5f : 0f;
                float dv = vRaw - (r + 0.5f);
                int kAnchor = (int)MathF.Round(uRaw - rowOffset);
                for (int dk = -1; dk <= 1; dk++)
                {
                    float du = uRaw - (kAnchor + dk + rowOffset);
                    du -= MathF.Round(du);
                    float dist = MathF.Sqrt(du * du + dv * dv);
                    if (dist < bumpR)
                    {
                        float bump = MathF.Cos(dist / bumpR * MathF.PI / 2f);
                        maxBump = MathF.Max(maxBump, 2f * bump * bump - 1f);
                    }
                }
            }
            return maxBump;
        }

        private float Voronoi(float theta, float z)
        {
            int n = Math.Max(3, (int)MathF.Round(Frequency));
            float u = Frac(theta / TwoPi) * n;
            float v = z / CellMm;
            int iu = (int)MathF.Floor(u), iv = (int)MathF.Floor(v);
            float f1 = 1e9f, f2 = 1e9f;
            for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    int ci = iu + di, cj = iv + dj;
                    int ciW = ((ci % n) + n) % n;
                    float sx = ci + 0.5f + 0.42f * VnHash(ciW, cj, 17);
                    float sy = cj + 0.5f + 0.42f * VnHash(ciW, cj, 53);
                    float dx = u - sx, dy = v - sy;
                    float d = dx * dx + dy * dy;
                    if (d < f1) { f2 = f1; f1 = d; }
                    else if (d < f2) f2 = d;
                }
            float edge = MathF.Sqrt(f2) - MathF.Sqrt(f1);
            return MathF.Min(1f, edge * 2.2f) * 2f - 1f;
        }

        private float Hexagon(float theta, float z)
        {
            int n = Math.Max(4, 2 * (int)MathF.Round(MathF.Max(2f, Frequency) / 2f));
            float rowH = CellMm * (MathF.Sqrt(3f) / 2f);
            float u = Frac(theta / TwoPi) * n;
            float v = z / rowH;
            int iu = (int)MathF.Floor(u), iv = (int)MathF.Floor(v);
            float f1 = 1e9f, f2 = 1e9f;
            for (int dj = -2; dj <= 2; dj++)
                for (int di = -2; di <= 2; di++)
                {
                    int ci = iu + di, cj = iv + dj;
                    int rowOdd = ((cj % 2) + 2) % 2;
                    float sx = ci + 0.5f + (rowOdd != 0 ? 0.5f : 0f);
                    float dx = u - sx;
                    dx -= MathF.Round(dx / n) * n;
                    float dy = v - (cj + 0.5f);
                    float d = dx * dx + dy * dy;
                    if (d < f1) { f2 = f1; f1 = d; }
                    else if (d < f2) f2 = d;
                }
            float hexEdge = MathF.Sqrt(f2) - MathF.Sqrt(f1);
            return MathF.Min(1f, hexEdge * 2.4f) * 2f - 1f;
        }

        private float Triangle(float theta, float z)
        {
            int n = Math.Max(3, (int)MathF.Round(MathF.Max(2f, Frequency)));
            float rowH = CellMm * (MathF.Sqrt(3f) / 2f);
            float u = Frac(theta / TwoPi) * n;
            float v = z / rowH;
            float a = u - v * 0.5f, b = v;
            float fa = a - MathF.Floor(a), fb = b - MathF.Floor(b);
            float w0, w1, w2;
            if (fa + fb < 1f) { w0 = 1f - fa - fb; w1 = fa; w2 = fb; }
            else              { w0 = 1f - fb; w1 = 1f - fa; w2 = fa + fb - 1f; }
            return MathF.Min(w0, MathF.Min(w1, w2)) * 6f - 1f;
        }

        private float Hammered(float theta, float z)
        {
            float q = MathF.Max(1f, Frequency) * 0.35f;
            float noise = ValueNoise3(MathF.Cos(theta) * q, MathF.Sin(theta) * q, z * (q / Radius));
            return Math.Clamp(noise * 1.6f, -1f, 1f);
        }

        // Sunflower / phyllotaxis: golden-angle seed points over the wall, cos² bumps.
        public void BuildSunflower()
        {
            int n = Math.Max(8, (int)MathF.Round(Frequency * Frequency * 0.8f));
            _sunflower = new (float, float)[n];
            const float golden = 2.399963f;   // golden angle (rad)
            for (int i = 0; i < n; i++)
                _sunflower[i] = (i * golden % TwoPi, (i + 0.5f) / n * Height);
            float area = TwoPi * Radius * Height;
            _sunBumpR = MathF.Sqrt(area / n) * 0.45f;
        }

        private float Sunflower(float theta, float z)
        {
            float best = -1f;
            foreach (var (st, sz) in _sunflower)
            {
                float dv = z - sz;
                if (MathF.Abs(dv) > _sunBumpR) continue;
                float dth = theta - st;
                dth -= MathF.Round(dth / TwoPi) * TwoPi;
                float du = dth * Radius;
                float dist = MathF.Sqrt(du * du + dv * dv);
                if (dist < _sunBumpR)
                {
                    float bump = MathF.Cos(dist / _sunBumpR * MathF.PI / 2f);
                    best = MathF.Max(best, 2f * bump * bump - 1f);
                }
            }
            return best;
        }

        // ── Helpers (ports of the effector noise/utility functions) ─────────

        private static float Frac(float x) => ((x % 1f) + 1f) % 1f;

        private static float SmoothTriWave(float x, float eps)
        {
            float t = Frac(x);
            float tri = MathF.Abs(t * 2f - 1f) * 2f - 1f;
            if (eps <= 0f) return tri;
            float cos = -MathF.Cos(TwoPi * t);
            return tri * (1f - eps) + cos * eps;
        }

        private static float VnHash(int ix, int iy, int iz)
        {
            int h = unchecked(ix * 374761393 + iy * 668265263 + iz * 1440662683);
            h = unchecked((h ^ (h >>> 13)) * 1274126177);
            h ^= h >>> 16;
            return (h & 65535) / 65535f * 2f - 1f;
        }

        private static float VnLayer(float x, float y, float z)
        {
            int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y), iz = (int)MathF.Floor(z);
            float fx = x - ix, fy = y - iy, fz = z - iz;
            float sx = fx * fx * (3f - 2f * fx), sy = fy * fy * (3f - 2f * fy), sz = fz * fz * (3f - 2f * fz);
            float c000 = VnHash(ix, iy, iz),     c100 = VnHash(ix + 1, iy, iz);
            float c010 = VnHash(ix, iy + 1, iz), c110 = VnHash(ix + 1, iy + 1, iz);
            float c001 = VnHash(ix, iy, iz + 1),     c101 = VnHash(ix + 1, iy, iz + 1);
            float c011 = VnHash(ix, iy + 1, iz + 1), c111 = VnHash(ix + 1, iy + 1, iz + 1);
            float x00 = c000 + (c100 - c000) * sx, x10 = c010 + (c110 - c010) * sx;
            float x01 = c001 + (c101 - c001) * sx, x11 = c011 + (c111 - c011) * sx;
            float y0 = x00 + (x10 - x00) * sy, y1 = x01 + (x11 - x01) * sy;
            return y0 + (y1 - y0) * sz;
        }

        private static float ValueNoise3(float x, float y, float z)
            => VnLayer(x, y, z) * 0.7f + VnLayer(x * 2.13f + 7.3f, y * 2.13f + 3.1f, z * 2.13f + 5.7f) * 0.3f;
    }
}
