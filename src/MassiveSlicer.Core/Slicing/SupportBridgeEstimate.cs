using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Result of a support-bridge proximity estimate: how many layers below a selection
/// are needed so a steppable column (≤ MaxStep/layer at the overhang angle) can
/// reach solid geometry or the bed.
/// </summary>
public sealed class SupportBridgeEstimateResult
{
    /// <summary>Worst-case layers of support height (including the tip layer's demand).</summary>
    public int LayersRequired { get; init; }

    /// <summary>LayersRequired × layer height (mm).</summary>
    public float HeightMm { get; init; }

    /// <summary>Max lateral XY gap (mm) from selection samples to the solid that lands the bridge.</summary>
    public float MaxGapMm { get; init; }

    /// <summary>Average gap across samples that needed a look-down (mm).</summary>
    public float AvgGapMm { get; init; }

    /// <summary>MaxStep per layer = layerHeight × tan(overhangDeg), optionally capped.</summary>
    public float MaxStepMm { get; init; }

    /// <summary>Overhang angle used (deg).</summary>
    public float OverhangDeg { get; init; }

    /// <summary>Layer height used (mm).</summary>
    public float LayerHeightMm { get; init; }

    /// <summary>Number of extrude samples evaluated.</summary>
    public int SampleCount { get; init; }

    /// <summary>True when every sample already sits within MaxStep of the layer immediately below (or bed).</summary>
    public bool AlreadySupported { get; init; }

    /// <summary>True when the worst case must go all the way to the bed (no solid within reach sooner).</summary>
    public bool ReachesBed { get; init; }

    /// <summary>Short UI line, e.g. "3 layers · 9 mm · gap 4.2 mm @ 30°".</summary>
    public string Summary { get; init; } = "";

    /// <summary>Longer helper text for tooltips / detail.</summary>
    public string Detail { get; init; } = "";
}

/// <summary>
/// Proximity helper for edit-mode Support: from a selected path, look downward through
/// previous toolpath layers and compute how many layers a Formbound/Tree-style bridge
/// needs so lateral growth stays within the overhang cone (default 30°).
/// Tree Support always reports foundation to the print bed (not to a mid-air solid plane).
/// </summary>
public static class SupportBridgeEstimate
{
    /// <param name="toolpath">Active scrub toolpath.</param>
    /// <param name="spans">Selection spans (layer index, move start, move count).</param>
    /// <param name="layerHeightMm">Nominal layer height.</param>
    /// <param name="beadWidthMm">Bead width (used for small-gap “already supported” threshold).</param>
    /// <param name="overhangDeg">Max overhang angle from vertical (default 30°).</param>
    /// <param name="capMaxStepToHalfBead">Match Formbound MaxStep cap (min with 0.5× bead).</param>
    /// <param name="toBedFoundation">
    /// When true (Tree Support), never land on intermediate solid/plane — count layers from
    /// the selection tip down to the print bed (Z≈0), including multiplanar stacks whose
    /// layer 0 is already elevated above the bed.
    /// </param>
    public static SupportBridgeEstimateResult Compute(
        Toolpath toolpath,
        IReadOnlyList<(int LayerIndex, int MoveStart, int MoveCount)> spans,
        float layerHeightMm,
        float beadWidthMm,
        float overhangDeg = 30f,
        bool capMaxStepToHalfBead = true,
        bool toBedFoundation = false)
    {
        float layerH = MathF.Max(layerHeightMm, 0.1f);
        float bead = MathF.Max(beadWidthMm, 0.1f);
        float deg = Math.Clamp(overhangDeg, 5f, 80f);
        float tanA = MathF.Tan(deg * MathF.PI / 180f);
        float maxStep = layerH * tanA;
        if (capMaxStepToHalfBead)
            maxStep = MathF.Min(maxStep, 0.5f * bead);
        maxStep = MathF.Max(maxStep, 1e-3f);

        // Already supported if gap to immediate previous ≤ half bead (same spirit as overhang score).
        float alreadyThr = bead * 0.5f;

        if (toolpath.Layers.Count == 0 || spans.Count == 0)
        {
            return Empty(maxStep, deg, layerH, "No selection");
        }

        // Spatial grids per layer (lazy) for nearest-gap queries.
        float cell = MathF.Max(bead, 1f);
        var grids = new Dictionary<int, Dictionary<(int, int), List<Vector2>>?>();

        Dictionary<(int, int), List<Vector2>>? GridOf(int li)
        {
            if (grids.TryGetValue(li, out var g)) return g;
            if (li < 0 || li >= toolpath.Layers.Count)
            {
                grids[li] = null;
                return null;
            }
            g = BuildGrid(toolpath.Layers[li], cell);
            grids[li] = g;
            return g;
        }

        int worstLayers = 0;
        float worstGap = 0f;
        float sumGap = 0f;
        int gapN = 0;
        int samples = 0;
        bool anyToBed = false;
        float worstTipZ = 0f;

        foreach (var (layerIdx, start, count) in spans)
        {
            if (layerIdx < 0 || layerIdx >= toolpath.Layers.Count) continue;
            var layer = toolpath.Layers[layerIdx];
            int end = Math.Min(layer.Moves.Count, start + Math.Max(0, count));

            for (int i = start; i < end; i++)
            {
                var mv = layer.Moves[i];
                if (mv.Kind != MoveKind.Extrude || mv.IsWipe) continue;
                samples++;
                var mid = (mv.From + mv.To) * 0.5f;
                var xy = new Vector2(mid.X, mid.Y);
                if (mid.Z > worstTipZ) worstTipZ = mid.Z;

                int need;
                float landGap;
                bool hitBed;

                if (toBedFoundation)
                {
                    // Tree: always foundation from bed. Use the larger of
                    // (index stack L0…tip) and (world Z / layerH) so multiplanar
                    // stacks that start above the bed still report full height.
                    int byIndex = layerIdx + 1;
                    int byZ = mid.Z > 0.05f
                        ? Math.Max(1, (int)MathF.Ceiling(mid.Z / layerH))
                        : byIndex;
                    need = Math.Max(byIndex, byZ);
                    // Lateral gap is informational only (nearest solid anywhere below).
                    landGap = float.PositiveInfinity;
                    for (int k = 1; k <= layerIdx; k++)
                    {
                        var grid = GridOf(layerIdx - k);
                        if (grid is null) continue;
                        float gap = NearestGap(xy, grid, cell);
                        if (gap < landGap) landGap = gap;
                    }
                    if (float.IsInfinity(landGap))
                        landGap = need * maxStep;
                    hitBed = true;
                    anyToBed = true;
                }
                else
                {
                    // Formbound: walk down — first solid within the overhang cone wins.
                    // k = 1 is previous layer, k = layerIdx is layer 0, k = layerIdx+1 is bed.
                    need = layerIdx + 1;
                    landGap = float.PositiveInfinity;
                    hitBed = true;

                    for (int k = 1; k <= layerIdx; k++)
                    {
                        int li = layerIdx - k;
                        var grid = GridOf(li);
                        float gap = grid is null
                            ? float.PositiveInfinity
                            : NearestGap(xy, grid, cell);
                        float reach = k * maxStep;
                        float thr = k == 1 ? MathF.Max(reach, alreadyThr) : reach;
                        if (gap <= thr)
                        {
                            need = k;
                            landGap = gap;
                            hitBed = false;
                            break;
                        }
                        if (gap < landGap) landGap = gap;
                    }

                    if (hitBed)
                    {
                        need = layerIdx + 1;
                        landGap = float.IsInfinity(landGap) ? need * maxStep : landGap;
                        anyToBed = true;
                    }
                }

                if (need > worstLayers) worstLayers = need;
                if (!float.IsInfinity(landGap) && landGap > worstGap) worstGap = landGap;
                if (!float.IsInfinity(landGap))
                {
                    sumGap += landGap;
                    gapN++;
                }
            }
        }

        if (samples == 0)
            return Empty(maxStep, deg, layerH, "No extrude beads in selection");

        float avgGap = gapN > 0 ? sumGap / gapN : 0f;
        bool already = !toBedFoundation
            && worstLayers <= 1
            && worstGap <= alreadyThr
            && !anyToBed;
        float height = worstLayers * layerH;

        string summary;
        if (already)
            summary = $"Supported · gap {worstGap:0.#} mm ≤ {alreadyThr:0.#} mm";
        else if (anyToBed || toBedFoundation)
            summary = $"To bed · {worstLayers} layers · {height:0.#} mm";
        else
            summary = $"{worstLayers} layer{(worstLayers == 1 ? "" : "s")} · {height:0.#} mm · gap {worstGap:0.#} mm";

        string detail;
        if (toBedFoundation)
        {
            detail =
                $"Tree bed foundation @ H={layerH:0.#} mm. "
                + $"Tip Z≈{worstTipZ:0.#} mm → {worstLayers} layers ({height:0.#} mm). "
                + $"Lateral nearest solid gap {worstGap:0.##} mm · samples {samples}";
        }
        else
        {
            detail =
                $"MaxStep {maxStep:0.##} mm/layer @ {deg:0.#}° (H={layerH:0.#} mm). "
                + $"Worst gap {worstGap:0.##} mm → ceil(gap/MaxStep) ≈ {Math.Max(1, (int)MathF.Ceiling(worstGap / maxStep))} "
                + $"· samples {samples}"
                + (anyToBed ? " · some points need bed foundation" : "");
        }

        return new SupportBridgeEstimateResult
        {
            LayersRequired = worstLayers,
            HeightMm = height,
            MaxGapMm = worstGap,
            AvgGapMm = avgGap,
            MaxStepMm = maxStep,
            OverhangDeg = deg,
            LayerHeightMm = layerH,
            SampleCount = samples,
            AlreadySupported = already,
            ReachesBed = anyToBed || toBedFoundation,
            Summary = summary,
            Detail = detail,
        };
    }

    private static SupportBridgeEstimateResult Empty(
        float maxStep, float deg, float layerH, string reason) => new()
    {
        LayersRequired = 0,
        MaxStepMm = maxStep,
        OverhangDeg = deg,
        LayerHeightMm = layerH,
        Summary = reason,
        Detail = $"MaxStep {maxStep:0.##} mm/layer @ {deg:0.#}°",
    };

    private static Dictionary<(int, int), List<Vector2>> BuildGrid(ToolpathLayer layer, float cell)
    {
        var grid = new Dictionary<(int, int), List<Vector2>>();
        foreach (var mv in layer.Moves)
        {
            if (mv.Kind != MoveKind.Extrude || mv.IsWipe) continue;
            // Sample ends + mid for better nearest distance.
            void Add(Vector3 p)
            {
                var xy = new Vector2(p.X, p.Y);
                int cx = (int)MathF.Floor(xy.X / cell);
                int cy = (int)MathF.Floor(xy.Y / cell);
                var key = (cx, cy);
                if (!grid.TryGetValue(key, out var list))
                {
                    list = [];
                    grid[key] = list;
                }
                list.Add(xy);
            }
            Add(mv.From);
            Add(mv.To);
            Add((mv.From + mv.To) * 0.5f);
        }
        return grid;
    }

    private static float NearestGap(
        Vector2 xy, Dictionary<(int, int), List<Vector2>> grid, float cell)
    {
        int cx = (int)MathF.Floor(xy.X / cell);
        int cy = (int)MathF.Floor(xy.Y / cell);
        float best = float.MaxValue;
        // Search expanding ring of cells until we find something or exhaust.
        for (int r = 0; r <= 12; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                if (r > 0 && Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                if (!grid.TryGetValue((cx + dx, cy + dy), out var list)) continue;
                foreach (var p in list)
                {
                    float d = Vector2.Distance(xy, p);
                    if (d < best) best = d;
                }
            }
            // Early out: if we have a hit closer than next ring could improve by much.
            if (best < float.MaxValue && best <= (r + 0.5f) * cell)
                break;
        }
        return best == float.MaxValue ? float.PositiveInfinity : best;
    }
}
