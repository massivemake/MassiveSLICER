using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Post-processing effect that displaces each extrude contour perpendicular to its travel
/// direction using a periodic waveform (sine, sawtooth, or triangle).
///
/// Two phase methods are supported (SliceSettings.WavePhaseMethod):
///
///   Method A — seam-anchored (default, original behaviour):
///     Phase is driven by arc length from each layer's contour start. Simple and predictable,
///     but as the part's cross-section morphs with height, the seam-to-point distance changes
///     per layer, so the layer-to-layer wave alignment drifts — producing visible texture
///     bands where the drift happens to hit a full wavelength ("resonance").
///
///   Method B — phase inheritance:
///     Each layer's phase is continued from the nearest point on the layer below, plus the
///     per-layer stagger. Drift is eliminated by construction; shape change is absorbed as a
///     tiny (bounded ±10%) local wavelength flex spread smoothly along the contour. Layer 1
///     (or any contour with nothing below it) falls back to seam-anchored phase.
///
/// Travel and layer-stitch moves pass through unchanged.
/// </summary>
public static class WaveEffect
{
    private static readonly float TwoPi = 2f * MathF.PI;

    public static Toolpath Apply(Toolpath toolpath, SliceSettings settings)
    {
        if (settings.WaveEffect == WaveEffectType.None) return toolpath;

        float baseAmplitude  = settings.WaveAmplitude;
        float baseWavelength = MathF.Max(settings.WaveWavelength, 1f);
        int   fixedCycles    = Math.Max(0, settings.WaveCycles);
        float shape          = Math.Clamp(settings.WaveShape, 0.01f, 1f);
        float stagger        = settings.WaveStagger;
        var   waveType       = settings.WaveEffect;
        bool  skinOnly       = settings.PatternSkinOnly;

        // Method B requires free-wavelength mode; fixed-cycles always uses Method A.
        bool inherit = settings.WavePhaseMethod == "B" && fixedCycles == 0;
        var  inheritor = inherit ? new PhaseInheritor() : null;

        // Gradient setup: derive zMin/zMax from the first/last layer.
        bool  gradient = settings.WaveGradient && toolpath.Layers.Count > 1;
        float zMin = 0f, zRange = 0f;
        if (gradient)
        {
            zMin   = toolpath.Layers[0].Z - toolpath.Layers[0].Height;
            float zMax = toolpath.Layers[^1].Z;
            zRange = zMax - zMin;
            if (zRange < 1e-4f) gradient = false;
        }

        var result = new Toolpath();
        foreach (var layer in toolpath.Layers)
        {
            float amplitude, wavelength;
            if (gradient)
            {
                float t = Math.Clamp((layer.Z - zMin) / zRange, 0f, 1f);
                t = GradientCenter(t, settings.WaveGradientCenter);
                t = GradientCurve(t, settings.WaveGradientCurve);
                amplitude  = settings.WaveAmplitudeBottom + (settings.WaveAmplitudeTop   - settings.WaveAmplitudeBottom)   * t;
                wavelength = MathF.Max(settings.WaveWavelengthBottom + (settings.WaveWavelengthTop - settings.WaveWavelengthBottom) * t, 1f);
            }
            else
            {
                amplitude  = baseAmplitude;
                wavelength = baseWavelength;
            }

            float spacing = Math.Clamp(wavelength / 16f, 1.0f, 5f);

            var newLayer = new ToolpathLayer(layer.Index, layer.Z) { PlaneNormal = layer.PlaneNormal };
            float phaseOffset = layer.Z * stagger;

            inheritor?.BeginLayer();

            // Skin-only: structure runs are copied through untouched on this pass and patched
            // below, once the whole layer's wall displacement is known. Copying first keeps the
            // moves in print order — emitting them after the walls would reorder the layer and
            // rewrite its travels.
            var wallField = skinOnly ? new SkinOnlyBracing.WallField() : null;
            var deferred  = skinOnly ? new List<(int NewStart, int OrigStart, int Count)>() : null;

            int i = 0;
            while (i < layer.Moves.Count)
            {
                var move = layer.Moves[i];

                if (move.Kind == MoveKind.Travel || move.IsLayerStitch)
                {
                    newLayer.Moves.Add(move);
                    i++;
                    continue;
                }

                int contourStart = i;
                while (i < layer.Moves.Count &&
                       layer.Moves[i].Kind != MoveKind.Travel &&
                       !layer.Moves[i].IsLayerStitch)
                    i++;

                if (deferred is not null && !layer.Moves[contourStart].IsWall)
                {
                    deferred.Add((newLayer.Moves.Count, contourStart, i - contourStart));
                    for (int k = contourStart; k < i; k++) newLayer.Moves.Add(layer.Moves[k]);
                    continue;
                }

                if (inheritor is not null)
                    ApplyToContourInherited(layer.Moves, contourStart, i, newLayer,
                        amplitude, wavelength, shape, spacing, waveType,
                        layer.Z, stagger, inheritor, wallField);
                else
                    ApplyToContour(layer.Moves, contourStart, i, newLayer,
                        amplitude, wavelength, fixedCycles, shape, spacing, waveType, phaseOffset,
                        wallField);
            }

            if (deferred is not null && wallField is { IsEmpty: false })
            {
                foreach (var (newStart, origStart, count) in deferred)
                {
                    var run   = new ToolpathMove[count];
                    for (int k = 0; k < count; k++) run[k] = layer.Moves[origStart + k];
                    var blend = SkinOnlyBracing.BlendForStructure(run, wallField);

                    for (int k = 0; k < count; k++)
                    {
                        if (!SkinOnlyBracing.IsStructure(run[k])) continue;
                        newLayer.Moves[newStart + k] = run[k] with
                        {
                            From = run[k].From + blend[k].AtFrom,
                            To   = run[k].To   + blend[k].AtTo,
                        };
                    }
                }
            }

            inheritor?.EndLayer(layer.Z);
            result.Layers.Add(newLayer);
        }

        return result;
    }

    // -- Gradient helpers ---------------------------------------------------------

    // Piecewise-linear centre-shift: maps t=center → 0.5, preserving endpoints at 0 and 1.
    private static float GradientCenter(float t, float center)
    {
        center = Math.Clamp(center, 0.001f, 0.999f);
        return t <= center
            ? 0.5f * (t / center)
            : 0.5f + 0.5f * ((t - center) / (1f - center));
    }

    private static float GradientCurve(float t, WaveGradientCurveType curve) => curve switch
    {
        WaveGradientCurveType.Smooth  => t * t * (3f - 2f * t),
        WaveGradientCurveType.EaseIn  => t * t,
        WaveGradientCurveType.EaseOut => 1f - (1f - t) * (1f - t),
        _                             => t,
    };

    // == Method A: seam-anchored (original behaviour, byte-identical output) ========

    private static void ApplyToContour(
        List<ToolpathMove> moves, int start, int end,
        ToolpathLayer newLayer,
        float amplitude, float wavelength, int fixedCycles, float shape, float spacing,
        WaveEffectType waveType, float phaseOffset,
        SkinOnlyBracing.WallField? wallField = null)
    {
        float totalLength = 0f;
        for (int i = start; i < end; i++)
            totalLength += Vector3.Distance(moves[i].From, moves[i].To);

        if (totalLength < 1e-4f)
        {
            for (int i = start; i < end; i++) newLayer.Moves.Add(moves[i]);
            return;
        }

        // Use a constant frequency so wavelength stays consistent across all layers.
        // Fixed-cycles mode still forces exactly k cycles over the contour length.
        // Free-wavelength mode uses the exact desired wavelength — phase may not return
        // to zero at the seam, but the wave pattern is visually uniform across layers.
        float freqPerMm = fixedCycles > 0
            ? TwoPi * fixedCycles / totalLength
            : TwoPi / wavelength;

        float arcSoFar = 0f;

        for (int mi = start; mi < end; mi++)
        {
            var   move = moves[mi];
            float len  = Vector3.Distance(move.From, move.To);
            if (len < 1e-4f) { newLayer.Moves.Add(move); arcSoFar += len; continue; }

            int segments = Math.Clamp((int)MathF.Ceiling(len / spacing), 1, 2000);
            var tangent  = Vector3.Normalize(move.To - move.From);
            var perp     = SafeNorm(Vector3.Cross(tangent, Vector3.UnitZ));

            Vector3 DisplacedPoint(int seg)
            {
                float t    = seg / (float)segments;
                var   pt   = Vector3.Lerp(move.From, move.To, t);
                float arc  = arcSoFar + t * len;
                float wave = WaveValue(arc * freqPerMm + phaseOffset, waveType, shape);
                return pt + perp * (wave * amplitude);
            }

            for (int seg = 0; seg < segments; seg++)
            {
                var a = DisplacedPoint(seg);
                var b = DisplacedPoint(seg + 1);
                if (move.IsWall)
                    wallField?.Record(Vector3.Lerp(move.From, move.To, seg / (float)segments), a);
                newLayer.Moves.Add(move with { From = a, To = b });
            }

            arcSoFar += len;
        }
    }

    // == Method B: phase inheritance ================================================

    /// <summary>
    /// Carries wave phase samples from one layer to the next.  Samples are stored on the
    /// BASE (pre-displacement) contour with winding-normalised tangents, so the phase
    /// field is a world-space quantity independent of seam position or loop direction.
    /// </summary>
    private sealed class PhaseInheritor
    {
        private const float CellSize    = 12f;   // spatial hash cell (mm)
        private const float MaxMatchDist = 15f;  // beyond this, no parent → fallback

        private List<(Vector3 pos, Vector3 tan, double phase)> _prev = [];
        private List<(Vector3 pos, Vector3 tan, double phase)> _curr = [];
        private Dictionary<(int, int), List<int>> _grid = [];
        private float _prevZ;

        public bool  HasPrev => _prev.Count > 0;
        public float PrevZ   => _prevZ;

        public void BeginLayer() => _curr = [];

        public void EndLayer(float z)
        {
            if (_curr.Count == 0) return;   // keep older samples if this layer had no contours
            _prev  = _curr;
            _prevZ = z;
            _grid  = [];
            for (int i = 0; i < _prev.Count; i++)
            {
                var key = Key(_prev[i].pos);
                if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = [];
                list.Add(i);
            }
        }

        public void AddSample(Vector3 pos, Vector3 tan, double phase) => _curr.Add((pos, tan, phase));

        private static (int, int) Key(Vector3 p) =>
            ((int)MathF.Floor(p.X / CellSize), (int)MathF.Floor(p.Y / CellSize));

        /// <summary>
        /// Phase of the nearest same-direction sample on the previous layer, or null.
        /// The direction gate distinguishes the two near-coincident passes of a
        /// single-bead wall (front/back face travel in opposite directions).
        /// </summary>
        public double? TryTarget(Vector3 p, Vector3 tan)
        {
            var (cx, cy) = Key(p);
            float bestD = MaxMatchDist * MaxMatchDist;
            double? best = null;
            for (int gx = cx - 2; gx <= cx + 2; gx++)
            for (int gy = cy - 2; gy <= cy + 2; gy++)
            {
                if (!_grid.TryGetValue((gx, gy), out var list)) continue;
                foreach (int i in list)
                {
                    var s = _prev[i];
                    if (s.tan.X * tan.X + s.tan.Y * tan.Y <= 0f) continue;
                    float dx = s.pos.X - p.X, dy = s.pos.Y - p.Y;
                    float d = dx * dx + dy * dy;
                    if (d < bestD) { bestD = d; best = s.phase; }
                }
            }
            return best;
        }
    }

    private static void ApplyToContourInherited(
        List<ToolpathMove> moves, int start, int end,
        ToolpathLayer newLayer,
        float amplitude, float wavelength, float shape, float spacing,
        WaveEffectType waveType, float z, float stagger, PhaseInheritor inheritor,
        SkinOnlyBracing.WallField? wallField = null)
    {
        float totalLength = 0f;
        for (int i = start; i < end; i++)
            totalLength += Vector3.Distance(moves[i].From, moves[i].To);

        if (totalLength < 1e-4f)
        {
            for (int i = start; i < end; i++) newLayer.Moves.Add(moves[i]);
            return;
        }

        float omega = TwoPi / wavelength;

        // Winding orientation (shoelace).  Tangents and displacement are normalised by
        // this so the phase field is consistent regardless of loop direction.
        float area2 = 0f;
        for (int i = start; i < end; i++)
            area2 += moves[i].From.X * moves[i].To.Y - moves[i].To.X * moves[i].From.Y;
        float orient = area2 >= 0f ? 1f : -1f;

        // -- 1. Sample the base contour every ~5 mm: arc, position, folded tangent.
        const float SampleStep = 5f;
        var sArr   = new List<float>();
        var pArr   = new List<Vector3>();
        var tArr   = new List<Vector3>();
        {
            float acc = 0f, next = 0f;
            for (int mi = start; mi < end; mi++)
            {
                var m   = moves[mi];
                float len = Vector3.Distance(m.From, m.To);
                if (len < 1e-6f) continue;
                var tan = (m.To - m.From) / len;
                var tanF = tan * orient;
                while (next <= acc + len)
                {
                    float t = (next - acc) / len;
                    sArr.Add(next);
                    pArr.Add(Vector3.Lerp(m.From, m.To, t));
                    tArr.Add(tanF);
                    next += SampleStep;
                }
                acc += len;
            }
            // always include the end point
            sArr.Add(totalLength);
            pArr.Add(moves[end - 1].To);
            tArr.Add((moves[end - 1].To - moves[end - 1].From) is var d && d.LengthSquared() > 1e-12f
                ? Vector3.Normalize(d) * orient : Vector3.UnitX);
        }
        int k = sArr.Count;

        // -- 2. Target phase-correction c(s) = targetPhase - omega*s from the layer below.
        //       Missing samples (no parent nearby) are interpolated/extended afterwards.
        double omegaS = omega * orient;      // signed phase rate along this traversal
        var c    = new double[k];
        var has  = new bool[k];
        double staggerAdd = inheritor.HasPrev ? (z - inheritor.PrevZ) * stagger : 0.0;
        if (inheritor.HasPrev)
        {
            double? prevC = null;
            for (int i = 0; i < k; i++)
            {
                var tp = inheritor.TryTarget(pArr[i], tArr[i]);
                if (tp is double parentPhase)
                {
                    double ci = parentPhase + staggerAdd - omegaS * sArr[i];
                    // unwrap against the previous valid correction
                    if (prevC is double pc)
                        ci += TwoPi * Math.Round((pc - ci) / TwoPi);
                    c[i] = ci; has[i] = true; prevC = ci;
                }
            }
        }

        // -- 3. Fill gaps.  No parents at all → seam-anchored fallback (Method A phase).
        int firstHas = Array.IndexOf(has, true);
        if (firstHas < 0)
        {
            for (int i = 0; i < k; i++) c[i] = z * stagger;
        }
        else
        {
            // extend edges
            for (int i = 0; i < firstHas; i++) c[i] = c[firstHas];
            int lastHas = Array.LastIndexOf(has, true);
            for (int i = lastHas + 1; i < k; i++) c[i] = c[lastHas];
            // interpolate interior gaps
            int a = firstHas;
            for (int i = firstHas + 1; i <= lastHas; i++)
            {
                if (!has[i]) continue;
                if (i > a + 1)
                {
                    for (int j = a + 1; j < i; j++)
                    {
                        double t = (sArr[j] - sArr[a]) / (sArr[i] - sArr[a]);
                        c[j] = c[a] + (c[i] - c[a]) * t;
                    }
                }
                a = i;
            }
        }

        // -- 4. Robust fit of the correction field:
        //       (a) moving median over ±1 wavelength — rejects bad nearest-point matches;
        //       (b) moving average over ±1 wavelength — smooths sample quantisation;
        //       (c) symmetric slope clamp (forward AND backward passes, averaged) —
        //           bounds local wavelength flex to ±35% without the directional lag
        //           that a single forward pass accumulates in fast-stretching regions.
        var cs = new double[k];
        {
            var med = new double[k];
            var buf = new List<double>(32);
            int mlo = 0, mhi = 0;
            for (int i = 0; i < k; i++)
            {
                while (mhi < k && sArr[mhi] <= sArr[i] + wavelength) mhi++;
                while (sArr[mlo] < sArr[i] - wavelength) mlo++;
                buf.Clear();
                for (int j = mlo; j < mhi; j++) buf.Add(c[j]);
                buf.Sort();
                med[i] = buf[buf.Count / 2];
            }

            int lo = 0, hi = 0; double sum = 0; int cnt = 0;
            for (int i = 0; i < k; i++)
            {
                while (hi < k && sArr[hi] <= sArr[i] + wavelength) { sum += med[hi]; cnt++; hi++; }
                while (sArr[lo] < sArr[i] - wavelength)            { sum -= med[lo]; cnt--; lo++; }
                cs[i] = sum / cnt;
            }

            double maxSlope = 0.35 * omega;   // rad per mm of local wavelength flex
            var fwd = (double[])cs.Clone();
            for (int i = 1; i < k; i++)
            {
                double ds = sArr[i] - sArr[i - 1];
                fwd[i] = Math.Clamp(fwd[i], fwd[i - 1] - maxSlope * ds, fwd[i - 1] + maxSlope * ds);
            }
            var bwd = (double[])cs.Clone();
            for (int i = k - 2; i >= 0; i--)
            {
                double ds = sArr[i + 1] - sArr[i];
                bwd[i] = Math.Clamp(bwd[i], bwd[i + 1] - maxSlope * ds, bwd[i + 1] + maxSlope * ds);
            }
            for (int i = 0; i < k; i++) cs[i] = 0.5 * (fwd[i] + bwd[i]);
        }

        // Phase lookup with linear interpolation over the samples.
        int cursor = 0;
        double PhaseAt(float s)
        {
            while (cursor < k - 1 && sArr[cursor + 1] < s) cursor++;
            while (cursor > 0 && sArr[cursor] > s) cursor--;
            int j = Math.Min(cursor + 1, k - 1);
            double t = sArr[j] > sArr[cursor]
                ? (s - sArr[cursor]) / (sArr[j] - sArr[cursor]) : 0.0;
            double cval = cs[cursor] + (cs[j] - cs[cursor]) * Math.Clamp(t, 0.0, 1.0);
            return omegaS * s + cval;
        }

        // -- 5. Displace, and record this layer's samples for the next layer.
        float arcSoFar = 0f;
        for (int mi = start; mi < end; mi++)
        {
            var   move = moves[mi];
            float len  = Vector3.Distance(move.From, move.To);
            if (len < 1e-4f) { newLayer.Moves.Add(move); arcSoFar += len; continue; }

            int segments = Math.Clamp((int)MathF.Ceiling(len / spacing), 1, 2000);
            var tangent  = Vector3.Normalize(move.To - move.From);
            var perp     = SafeNorm(Vector3.Cross(tangent, Vector3.UnitZ));

            Vector3 DisplacedPoint(int seg)
            {
                float t    = seg / (float)segments;
                var   pt   = Vector3.Lerp(move.From, move.To, t);
                float arc  = arcSoFar + t * len;
                float wave = WaveValue((float)PhaseAt(arc), waveType, shape);
                // orient folds the winding into the displacement so the world-space
                // bulge direction is consistent regardless of loop direction.
                return pt + perp * (wave * amplitude * orient);
            }

            for (int seg = 0; seg < segments; seg++)
            {
                var a = DisplacedPoint(seg);
                var b = DisplacedPoint(seg + 1);
                if (move.IsWall)
                    wallField?.Record(Vector3.Lerp(move.From, move.To, seg / (float)segments), a);
                newLayer.Moves.Add(move with { From = a, To = b });
            }

            arcSoFar += len;
        }

        for (int i = 0; i < k; i++)
            inheritor.AddSample(pArr[i], tArr[i], omegaS * sArr[i] + cs[i]);
    }

    // -- Waveforms ----------------------------------------------------------------

    /// <summary>
    /// Returns a wave value in [-1, 1] for the given phase (radians), with optional shape clipping.
    /// All waveforms cross zero at phase = 0 and phase = 2πk, so a contour using integer cycle
    /// count starts and ends at zero displacement — the seam closes cleanly.
    ///
    /// Sawtooth: rises 0→+1 over the first half-cycle, jumps to -1, rises back to 0.
    /// Triangle: rises 0→+1 at quarter-cycle, falls to -1 at three-quarter-cycle, back to 0.
    /// </summary>
    private static float WaveValue(float phase, WaveEffectType type, float shape)
    {
        float wave;

        if (type == WaveEffectType.Sine)
        {
            wave = MathF.Sin(phase);
        }
        else
        {
            float t = (phase % TwoPi) / TwoPi; // normalised phase in [0, 1)
            if (t < 0f) t += 1f;               // handle negative phase (Method B / reversed winding)
            wave = type switch
            {
                // Rises 0→1 in [0, 0.5), jumps to -1 at 0.5, rises back to 0 at 1.
                WaveEffectType.Sawtooth => t < 0.5f ? 2f * t : 2f * t - 2f,

                // Rises 0→1 at t=0.25, falls to -1 at t=0.75, returns to 0 at t=1.
                WaveEffectType.Triangle => 1f - 4f * MathF.Abs(((t + 0.25f) % 1f) - 0.5f),

                _ => 0f,
            };
        }

        // Shape clipping: clamp to [-shape, +shape] then rescale to [-1, 1].
        // At shape=1 the waveform is unmodified; lower values flatten peaks toward a square wave.
        if (shape < 1f - 1e-4f)
            wave = Math.Clamp(wave, -shape, shape) / shape;

        return wave;
    }

    // -- Helpers ------------------------------------------------------------------

    private static Vector3 SafeNorm(Vector3 v)
    {
        float len = v.Length();
        return len > 1e-6f ? v / len : Vector3.UnitX;
    }
}
