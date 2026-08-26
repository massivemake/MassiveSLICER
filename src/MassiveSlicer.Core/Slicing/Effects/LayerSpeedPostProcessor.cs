using System.Globalization;
using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Assigns per-move <see cref="ToolpathMove.PrintSpeedScale"/> from a layer metric,
/// then applies live print-feedback notes.
/// Cut length / layer time still map shortest → min and longest → max (file rank).
/// Print feedback uses that metric only to slow short layers; the process ceiling
/// is print speed. Capital.mass layer 63 ran at 54 mm/s on a 10–100 file stretch
/// and needed to be ~20 % slower — back at the 40 mm/s print speed.
/// RPM scales with the same factor as robot speed for KRL export.
/// </summary>
public static class LayerSpeedPostProcessor
{
    public static Toolpath Apply(Toolpath toolpath, SliceSettings settings)
    {
        // Brim speed is deliberately independent of Adaptive Speed, so it still applies when
        // the feature is off.
        if (!settings.LayerSpeedAdaptEnabled || toolpath.Layers.Count == 0)
            return ResetScales(toolpath, settings);

        float baseMmS = settings.PrintSpeedMps * 1000f;
        float minMmS  = Math.Max(settings.LayerSpeedMinMmS, 0.1f);
        float maxMmS  = settings.LayerSpeedMaxMmS > 0f ? settings.LayerSpeedMaxMmS : baseMmS;
        if (minMmS > maxMmS)
            (minMmS, maxMmS) = (maxMmS, minMmS);

        var rates = new ToolpathMotionRates(
            baseMmS,
            settings.TravelSpeed * 1000f,
            settings.WipeSpeed * 1000f);

        var layerValues = new double[toolpath.Layers.Count];
        for (int i = 0; i < toolpath.Layers.Count; i++)
            layerValues[i] = LayerMetricValue(toolpath.Layers[i], settings.LayerSpeedBasis, rates);

        double minValue = layerValues.Min();
        double maxValue = layerValues.Max();
        var notes = ParseNotes(settings.LayerSpeedNotes);

        float brimScale = BrimScale(settings, baseMmS);
        float? brimRpm  = BrimRpmOverride(settings);

        var result = ToolpathClone.Copy(toolpath);
        for (int i = 0; i < result.Layers.Count; i++)
        {
            float mapped = SpeedScaleForValue(layerValues[i], minValue, maxValue, minMmS, maxMmS, baseMmS);
            float speedMmS = mapped * baseMmS;
            // Print feedback: file rank may still slow a short layer, but it must not
            // push a long layer past the process speed that just failed on the floor.
            if (settings.LayerSpeedBasis == LayerSpeedBasis.PrintFeedback)
                speedMmS = Math.Min(speedMmS, baseMmS);
            speedMmS *= NoteFactor(notes, result.Layers[i].Index + 1);
            float scale = speedMmS / Math.Max(baseMmS, 0.1f);

            var layer = result.Layers[i];
            for (int mi = 0; mi < layer.Moves.Count; mi++)
            {
                var move = layer.Moves[mi];
                if (move.IsBrim)
                {
                    if (move.Kind == MoveKind.Extrude)
                        layer.Moves[mi] = move with
                        {
                            PrintSpeedScale    = brimScale,
                            RpmPercentOverride = brimRpm,
                        };
                    continue;
                }
                if (!IsAdaptable(move)) continue;
                layer.Moves[mi] = move with { PrintSpeedScale = scale };
            }
        }

        return result;
    }

    public static float SpeedScaleForValue(
        double value, double minValue, double maxValue, float minMmS, float maxMmS, float basePrintMmS)
    {
        double t = maxValue > minValue + 1e-9
            ? (value - minValue) / (maxValue - minValue)
            : 1.0;
        float speedMmS = (float)(minMmS + (maxMmS - minMmS) * t);
        return speedMmS / Math.Max(basePrintMmS, 0.1f);
    }

    /// <summary>
    /// Parse operator notes. Keys are 1-based layer numbers (layer 63 = first printed layer is 1).
    /// <c>63:-20</c> is twenty percent slower. <c>63:0.8</c> is the same as a factor.
    /// </summary>
    public static Dictionary<int, float> ParseNotes(string? text)
    {
        var notes = new Dictionary<int, float>();
        if (string.IsNullOrWhiteSpace(text)) return notes;

        foreach (var raw in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;
            int sep = token.IndexOf(':');
            if (sep < 0) sep = token.IndexOf('=');
            if (sep <= 0 || sep >= token.Length - 1) continue;

            var layerTok = token[..sep].Trim().TrimStart('L', 'l');
            var valueTok = token[(sep + 1)..].Trim().TrimEnd('%');
            if (!int.TryParse(layerTok, NumberStyles.Integer, CultureInfo.InvariantCulture, out int layer)
                || layer < 1)
                continue;
            if (!double.TryParse(valueTok, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                continue;

            float factor = Math.Abs(v) <= 2.0 && Math.Abs(v - Math.Round(v)) > 1e-6
                ? (float)v
                : 1f + (float)v / 100f;
            notes[layer] = Math.Clamp(factor, 0.05f, 3f);
        }

        return notes;
    }

    /// <summary>Replace or add one 1-based layer note. <paramref name="delta"/> is a signed percent or a factor.</summary>
    public static string SetNote(string? existing, int layer1Based, double delta)
    {
        var notes = ParseNotes(existing);
        float factor = Math.Abs(delta) <= 2.0 && Math.Abs(delta - Math.Round(delta)) > 1e-6
            ? (float)delta
            : 1f + (float)delta / 100f;
        notes[Math.Max(1, layer1Based)] = Math.Clamp(factor, 0.05f, 3f);
        return string.Join(",", notes.OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}:{((kv.Value - 1f) * 100f).ToString("0.#", CultureInfo.InvariantCulture)}"));
    }

    public static float NoteFactor(IReadOnlyDictionary<int, float> notes, int layer1Based)
        => notes.TryGetValue(layer1Based, out float f) ? f : 1f;

    private static double LayerMetricValue(ToolpathLayer layer, LayerSpeedBasis basis, ToolpathMotionRates rates)
    {
        // Print feedback still needs a short-vs-long metric so small layers can slow
        // down for cooling. Cut length is that metric; it is not the speed target.
        if (basis is LayerSpeedBasis.CutLength or LayerSpeedBasis.PrintFeedback)
        {
            double cutLen = 0.0;
            foreach (var move in layer.Moves)
            {
                if (!ToolpathMoveKinds.IsCutSegment(move.Kind)) continue;
                if (move.IsBrim) continue;
                cutLen += Vector3.Distance(move.From, move.To);
            }
            return cutLen;
        }

        double layerTime = 0.0;
        foreach (var move in layer.Moves)
        {
            if (move.IsBrim) continue;
            double dist = Vector3.Distance(move.From, move.To);
            layerTime += ToolpathStatistics.MoveTimeSeconds(move, rates, dist);
        }
        return layerTime;
    }

    /// <summary>
    /// Brim never takes the per-layer adaptive scale — <see cref="Apply"/> gives it its own
    /// fixed speed (and optional absolute RPM) before this is reached.
    /// Left adaptable, the brim is the longest "layer" in the part, so it took the maximum
    /// speed — and being full nominal thickness, nothing reduced its flow. That made it the
    /// move that hit the 99 % RPM export gate, which in turn capped how high the maximum
    /// could be set and left the rest of the part crawling near the minimum.
    /// </summary>
    private static bool IsAdaptable(ToolpathMove move)
        => move.Kind == MoveKind.Extrude && !move.IsWipe && !move.IsLayerStitch && !move.IsBrim;

    /// <summary>
    /// Scale that puts a brim extrude move at <see cref="SliceSettings.BrimSpeedMmS"/>,
    /// clamped to <see cref="SliceSettings.MaxBrimSpeedMmS"/>. Independent of print speed and
    /// of the Adaptive Speed window by design — the brim is bed adhesion, not part shape.
    /// </summary>
    public static float BrimScale(SliceSettings settings, float basePrintMmS)
    {
        // Floor matches the UI's own clamp: a brim below 1 mm/s is never wanted, and an
        // unset 0 from an older preset must not divide the speed away to nothing.
        float brimMmS = Math.Clamp(settings.BrimSpeedMmS, 1f, SliceSettings.MaxBrimSpeedMmS);
        return brimMmS / Math.Max(basePrintMmS, 0.1f);
    }

    /// <summary>
    /// Absolute brim RPM (%) to stamp on brim extrude moves, or null to let RPM follow brim
    /// speed as normal. Lets the brim be deliberately over-extruded for bed adhesion despite
    /// running slow — the whole reason it is absolute rather than another scale.
    /// </summary>
    public static float? BrimRpmOverride(SliceSettings settings)
        => settings.BrimRpmPercent > 1e-6f
            ? Math.Clamp(settings.BrimRpmPercent, 1f, SliceSettings.MaxBrimRpmPercent)
            : null;

    private static Toolpath ResetScales(Toolpath toolpath, SliceSettings settings)
    {
        float baseMmS   = settings.PrintSpeedMps * 1000f;
        float brimScale = BrimScale(settings, baseMmS);
        float? brimRpm  = BrimRpmOverride(settings);

        var result = ToolpathClone.Copy(toolpath);
        foreach (var layer in result.Layers)
        {
            for (int mi = 0; mi < layer.Moves.Count; mi++)
            {
                var move = layer.Moves[mi];
                bool isBrimBead = move.IsBrim && move.Kind == MoveKind.Extrude;
                float  want    = isBrimBead ? brimScale : 1f;
                float? wantRpm = isBrimBead ? brimRpm : null;
                if (Math.Abs(move.PrintSpeedScale - want) < 1e-6f
                    && Nullable.Equals(move.RpmPercentOverride, wantRpm)) continue;
                layer.Moves[mi] = move with { PrintSpeedScale = want, RpmPercentOverride = wantRpm };
            }
        }
        return result;
    }
}
