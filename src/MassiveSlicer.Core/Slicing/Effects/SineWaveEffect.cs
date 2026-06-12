using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Post-processing effect that displaces each extrude contour perpendicular to its travel
/// direction using a periodic waveform (sine, sawtooth, or triangle).
///
/// Phase is driven by normalised arc length: (arc / totalContourLength) × 2π × k, where k is
/// the nearest integer number of cycles that fits the contour. This guarantees:
///   • Start and end displacement are both zero — the seam always closes cleanly.
///   • Points at the same arc-fraction on different layers share the same phase — vertically
///     stacked seams stay aligned regardless of floating-point variation in contour length.
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

            float spacing = Math.Clamp(wavelength / 16f, 0.5f, 2f);

            var newLayer = new ToolpathLayer(layer.Index, layer.Z) { PlaneNormal = layer.PlaneNormal };
            float phaseOffset = layer.Index * stagger * TwoPi;

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

                ApplyToContour(layer.Moves, contourStart, i, newLayer,
                               amplitude, wavelength, fixedCycles, shape, spacing, waveType, phaseOffset);
            }

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

    // -- Contour processing -------------------------------------------------------

    private static void ApplyToContour(
        List<ToolpathMove> moves, int start, int end,
        ToolpathLayer newLayer,
        float amplitude, float wavelength, int fixedCycles, float shape, float spacing,
        WaveEffectType waveType, float phaseOffset)
    {
        float totalLength = 0f;
        for (int i = start; i < end; i++)
            totalLength += Vector3.Distance(moves[i].From, moves[i].To);

        if (totalLength < 1e-4f)
        {
            for (int i = start; i < end; i++) newLayer.Moves.Add(moves[i]);
            return;
        }

        // Cycle count: fixed by user (adaptive mode) or derived from wavelength (default).
        // In both cases the wave completes exactly k full cycles so the seam closes cleanly.
        int k = fixedCycles > 0
            ? fixedCycles
            : Math.Max(1, (int)MathF.Round(totalLength / wavelength));
        float freqPerMm = TwoPi * k / totalLength;

        float arcSoFar = 0f;

        for (int mi = start; mi < end; mi++)
        {
            var   move = moves[mi];
            float len  = Vector3.Distance(move.From, move.To);
            if (len < 1e-4f) { newLayer.Moves.Add(move); arcSoFar += len; continue; }

            int segments = Math.Max(1, (int)MathF.Ceiling(len / spacing));
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
                newLayer.Moves.Add(new ToolpathMove(
                    DisplacedPoint(seg),
                    DisplacedPoint(seg + 1),
                    MoveKind.Extrude)
                {
                    Normal        = move.Normal,
                    IsLayerChange = move.IsLayerChange,
                });
            }

            arcSoFar += len;
        }
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
