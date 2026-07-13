using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>Per-layer metrics for the 2D Slice Plane Viewer HUD.</summary>
public sealed class SliceLayerStats
{
    public int LayerIndex0 { get; init; }
    public int LayerNumber => LayerIndex0 + 1;
    public float Z { get; init; }

    public double ExtrudeLengthMm { get; init; }
    public double TravelLengthMm { get; init; }
    public double LightningLengthMm { get; init; }
    public double WipeLengthMm { get; init; }

    public int ExtrudeMoves { get; init; }
    public int TravelMoves { get; init; }

    /// <summary>Separate extrude islands / contours on this layer.</summary>
    public int Islands { get; init; }
    public int ClosedLoops { get; init; }
    public int OpenPaths { get; init; }

    /// <summary>Extrude length with weak support from the previous layer (score ≥ 0.5).</summary>
    public double OverhangLengthMm { get; init; }

    /// <summary><see cref="OverhangLengthMm"/> / extrude length × 100.</summary>
    public double OverhangPercent { get; init; }

    /// <summary>Lightning/formbound share of extrude length × 100.</summary>
    public double FormboundPercent { get; init; }

    public double EstTimeSeconds { get; init; }
    public double VolumeMm3 { get; init; }

    public float BoundsWidthMm { get; init; }
    public float BoundsDepthMm { get; init; }
    public float BoundsHeightSpanMm { get; init; }

    public bool HasGeometry => ExtrudeMoves > 0 || TravelMoves > 0;
}

/// <summary>Analyses a single toolpath layer for the 2D slice readout.</summary>
public static class SliceLayerAnalyzer
{
    /// <summary>
    /// Compute stats for <paramref name="layerIndex"/> (0-based).
    /// Previous layer is used for overhang estimation when present.
    /// </summary>
    public static SliceLayerStats Analyze(
        Toolpath toolpath,
        int layerIndex,
        float beadWidthMm,
        float layerHeightMm,
        ToolpathMotionRates? rates = null)
    {
        if (toolpath.Layers.Count == 0 || layerIndex < 0 || layerIndex >= toolpath.Layers.Count)
            return new SliceLayerStats { LayerIndex0 = Math.Max(0, layerIndex) };

        var layer = toolpath.Layers[layerIndex];
        ToolpathLayer? prev = layerIndex > 0 ? toolpath.Layers[layerIndex - 1] : null;
        rates ??= new ToolpathMotionRates(60, 600, 60);

        double extrudeLen = 0, travelLen = 0, lightningLen = 0, wipeLen = 0;
        double overhangLen = 0, timeSec = 0, volume = 0;
        int extrudeMoves = 0, travelMoves = 0;
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;

        // Spatial hash of previous-layer cuts for overhang.
        float cell = MathF.Max(beadWidthMm, 0.5f);
        var prevGrid = BuildPrevGrid(prev, cell);

        foreach (var mv in layer.Moves)
        {
            double dist = Vector3.Distance(mv.From, mv.To);
            timeSec += ToolpathStatistics.MoveTimeSeconds(mv, rates.Value, dist);

            if (mv.Kind == MoveKind.Travel)
            {
                travelMoves++;
                travelLen += dist;
                continue;
            }

            if (!ToolpathMoveKinds.IsCutSegment(mv.Kind)) continue;

            extrudeMoves++;
            extrudeLen += dist;
            if (mv.IsWipe) wipeLen += dist;
            if (mv.IsLightning) lightningLen += dist;
            if (mv.Kind == MoveKind.Extrude)
                volume += dist * beadWidthMm * layerHeightMm * Math.Max(mv.HeightScale, 1e-6f);

            ExpandBounds(mv.From, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);
            ExpandBounds(mv.To, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);

            if (prevGrid is not null && beadWidthMm > 0f)
            {
                var mid = (mv.From + mv.To) * 0.5f;
                float score = OverhangScore(mid, prevGrid, cell, beadWidthMm);
                if (score >= 0.5f)
                    overhangLen += dist;
            }
        }

        var (islands, closed, open) = CountIslands(layer);
        double ohPct = extrudeLen > 1e-6 ? overhangLen / extrudeLen * 100.0 : 0;
        double fbPct = extrudeLen > 1e-6 ? lightningLen / extrudeLen * 100.0 : 0;

        bool hasBounds = minX <= maxX;
        return new SliceLayerStats
        {
            LayerIndex0 = layer.Index,
            Z = layer.Z,
            ExtrudeLengthMm = extrudeLen,
            TravelLengthMm = travelLen,
            LightningLengthMm = lightningLen,
            WipeLengthMm = wipeLen,
            ExtrudeMoves = extrudeMoves,
            TravelMoves = travelMoves,
            Islands = islands,
            ClosedLoops = closed,
            OpenPaths = open,
            OverhangLengthMm = overhangLen,
            OverhangPercent = ohPct,
            FormboundPercent = fbPct,
            EstTimeSeconds = timeSec,
            VolumeMm3 = volume,
            BoundsWidthMm = hasBounds ? maxX - minX : 0,
            BoundsDepthMm = hasBounds ? maxY - minY : 0,
            BoundsHeightSpanMm = hasBounds ? maxZ - minZ : 0,
        };
    }

    private static void ExpandBounds(
        Vector3 p,
        ref float minX, ref float maxX, ref float minY, ref float maxY, ref float minZ, ref float maxZ)
    {
        if (p.X < minX) minX = p.X;
        if (p.X > maxX) maxX = p.X;
        if (p.Y < minY) minY = p.Y;
        if (p.Y > maxY) maxY = p.Y;
        if (p.Z < minZ) minZ = p.Z;
        if (p.Z > maxZ) maxZ = p.Z;
    }

    private static (int islands, int closed, int open) CountIslands(ToolpathLayer layer)
    {
        IReadOnlyList<ContourSpan> spans = layer.Contours.Count > 0
            ? layer.Contours
            : SynthesizeRuns(layer);

        int islands = 0, closed = 0, open = 0;
        foreach (var s in spans)
        {
            if (s.Count < 1) continue;
            // Skip pure-travel spans.
            bool anyExtrude = false;
            int end = Math.Min(layer.Moves.Count, s.Start + s.Count);
            for (int i = s.Start; i < end; i++)
                if (ToolpathMoveKinds.IsCutSegment(layer.Moves[i].Kind))
                { anyExtrude = true; break; }
            if (!anyExtrude) continue;

            islands++;
            if (s.Closed) closed++;
            else open++;
        }
        return (islands, closed, open);
    }

    private static List<ContourSpan> SynthesizeRuns(ToolpathLayer layer)
    {
        var spans = new List<ContourSpan>();
        var moves = layer.Moves;
        int i = 0;
        while (i < moves.Count)
        {
            while (i < moves.Count && !ToolpathMoveKinds.IsCutSegment(moves[i].Kind)) i++;
            if (i >= moves.Count) break;
            int start = i;
            while (i < moves.Count && ToolpathMoveKinds.IsCutSegment(moves[i].Kind)
                   && !moves[i].IsLayerStitch && !moves[i].IsLayerChange)
                i++;
            int count = i - start;
            if (count < 1) continue;
            bool closed = false;
            if (count >= 2)
            {
                var a = moves[start].From;
                var b = moves[start + count - 1].To;
                closed = Vector3.DistanceSquared(a, b) < 1.0f;
            }
            spans.Add(new ContourSpan(start, count, closed, -1));
        }
        return spans;
    }

    private static Dictionary<(int, int), List<(Vector3 a, Vector3 b)>>? BuildPrevGrid(
        ToolpathLayer? prev, float cell)
    {
        if (prev is null) return null;
        var grid = new Dictionary<(int, int), List<(Vector3 a, Vector3 b)>>();
        foreach (var mv in prev.Moves)
        {
            if (!ToolpathMoveKinds.IsCutSegment(mv.Kind)) continue;
            int x0 = (int)MathF.Floor(MathF.Min(mv.From.X, mv.To.X) / cell);
            int x1 = (int)MathF.Floor(MathF.Max(mv.From.X, mv.To.X) / cell);
            int y0 = (int)MathF.Floor(MathF.Min(mv.From.Y, mv.To.Y) / cell);
            int y1 = (int)MathF.Floor(MathF.Max(mv.From.Y, mv.To.Y) / cell);
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                if (!grid.TryGetValue((x, y), out var list))
                    grid[(x, y)] = list = [];
                list.Add((mv.From, mv.To));
            }
        }
        return grid.Count > 0 ? grid : null;
    }

    private static float OverhangScore(
        Vector3 mid,
        Dictionary<(int, int), List<(Vector3 a, Vector3 b)>> prevGrid,
        float cell,
        float beadWidth)
    {
        int cx = (int)MathF.Floor(mid.X / cell);
        int cy = (int)MathF.Floor(mid.Y / cell);
        float minD = float.MaxValue;
        for (int gx = cx - 1; gx <= cx + 1; gx++)
        for (int gy = cy - 1; gy <= cy + 1; gy++)
        {
            if (!prevGrid.TryGetValue((gx, gy), out var segs)) continue;
            foreach (var (a, b) in segs)
            {
                float d = SegDist2D(mid, a, b);
                if (d < minD) minD = d;
            }
        }
        if (minD == float.MaxValue) return 1f;
        return Math.Clamp(minD / beadWidth, 0f, 1f);
    }

    private static float SegDist2D(Vector3 p, Vector3 a, Vector3 b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-10f)
        {
            float ex = p.X - a.X, ey = p.Y - a.Y;
            return MathF.Sqrt(ex * ex + ey * ey);
        }
        float t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0f, 1f);
        float cx = a.X + t * dx - p.X, cy = a.Y + t * dy - p.Y;
        return MathF.Sqrt(cx * cx + cy * cy);
    }
}
