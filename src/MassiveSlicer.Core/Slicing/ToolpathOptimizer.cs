using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Post-slice travel optimizer. Per layer it builds two candidates and keeps the
/// one with less travel: (A) paths greedily re-ordered so each starts where the
/// previous ends (closed loops re-seam to the nearest vertex, open paths may
/// reverse), and (B) the original order untouched. In both, any remaining
/// travel shorter than 3× bead width becomes an extruded bridge — welding
/// neighbouring paths into one continuous extrusion. Original ordering wins ties
/// so slice-time seam placement is never disturbed for nothing.
/// Runs in place; layer contour spans are rebuilt so re-seaming still works.
/// </summary>
public static class ToolpathOptimizer
{
    public sealed record Stats(
        int TravelsBefore, int TravelsAfter,
        float TravelMmBefore, float TravelMmAfter,
        int Bridges, int LayersReordered)
    {
        public override string ToString() =>
            $"travel {TravelMmBefore / 1000f:0.0} m ({TravelsBefore} moves) → "
            + $"{TravelMmAfter / 1000f:0.0} m ({TravelsAfter} moves), "
            + $"{Bridges} path(s) bridged into continuous extrusion, "
            + $"{LayersReordered} layer(s) re-ordered";
    }

    private const float Eps = 0.05f;       // zero-gap tolerance (mm)
    private const float ChainEps = 0.5f;   // path contiguity tolerance (mm) — float drift stays in-chain

    public static Stats Optimize(Toolpath tp, float beadWidth)
    {
        float bridgeMax = beadWidth * 3f;
        int travelsBefore = 0, travelsAfter = 0, bridges = 0, reordered = 0;
        float mmBefore = 0f, mmAfter = 0f;

        Vector3? prevEnd = null;
        foreach (var layer in tp.Layers)
        {
            var (tb, nb) = TravelLength(layer.Moves);
            travelsBefore += nb;
            mmBefore += tb;

            // Post-processed or non-additive layers keep their ordering: it
            // encodes semantics this pass doesn't understand.
            bool untouchable = false;
            foreach (var m in layer.Moves)
                if (m.Kind == MoveKind.Mill || m.IsWipe || m.IsResumeRamp || m.IsZHop || m.IsMergeConnector)
                { untouchable = true; break; }

            if (untouchable || layer.Moves.Count == 0)
            {
                travelsAfter += nb;
                mmAfter += tb;
                if (layer.Moves.Count > 0) prevEnd = layer.Moves[^1].To;
                continue;
            }

            // Candidate B: original order, short travels welded into extrude bridges.
            var (movesB, spansB, bridgesB) = BridgeInPlace(layer, bridgeMax);

            // Candidate A: greedy re-order, then the same welding via emission.
            var chains = SplitChains(layer.Moves);
            List<ToolpathMove>? movesA = null;
            List<ContourSpan>? spansA = null;
            int bridgesA = 0;
            if (chains.Count > 0)
                (movesA, spansA, bridgesA) = Reorder(chains, prevEnd, bridgeMax);

            var (ta, na) = movesA is null ? (float.MaxValue, 0) : TravelLength(movesA);
            var (tb2, nb2) = TravelLength(movesB);

            bool useA = movesA is not null && ta < tb2 - 0.5f;
            var moves = useA ? movesA! : movesB;
            var spans = useA ? spansA! : spansB;
            bridges += useA ? bridgesA : bridgesB;
            if (useA) reordered++;
            travelsAfter += useA ? na : nb2;
            mmAfter += useA ? ta : tb2;

            layer.Moves.Clear();
            layer.Moves.AddRange(moves);
            layer.Contours.Clear();
            layer.Contours.AddRange(spans);
            prevEnd = moves.Count > 0 ? moves[^1].To : prevEnd;
        }
        return new Stats(travelsBefore, travelsAfter, mmBefore, mmAfter, bridges, reordered);
    }

    private static (float Mm, int Count) TravelLength(List<ToolpathMove> moves)
    {
        float mm = 0f; int n = 0;
        foreach (var m in moves)
            if (m.Kind == MoveKind.Travel)
            {
                n++;
                mm += Vector3.Distance(m.From, m.To);
            }
        return (mm, n);
    }

    /// <summary>Original order; travels ≤ 3× bead become extrude bridges in place.</summary>
    private static (List<ToolpathMove> Moves, List<ContourSpan> Spans, int Bridges) BridgeInPlace(
        ToolpathLayer layer, float bridgeMax)
    {
        var moves = new List<ToolpathMove>(layer.Moves.Count);
        var converted = new HashSet<int>();
        int bridgesMade = 0;
        for (int i = 0; i < layer.Moves.Count; i++)
        {
            var m = layer.Moves[i];
            float len = Vector3.Distance(m.From, m.To);
            if (m.Kind == MoveKind.Travel && len > Eps && len <= bridgeMax)
            {
                // Take the bead orientation from the path being entered.
                var normal = m.Normal;
                float heightScale = 1f;
                for (int j = i + 1; j < layer.Moves.Count; j++)
                    if (layer.Moves[j].Kind == MoveKind.Extrude)
                    {
                        normal = layer.Moves[j].Normal;
                        heightScale = layer.Moves[j].HeightScale;
                        break;
                    }
                moves.Add(new ToolpathMove(m.From, m.To, MoveKind.Extrude)
                {
                    Normal = normal,
                    HeightScale = heightScale,
                    IsLayerStitch = m.IsLayerChange,
                });
                converted.Add(i);
                bridgesMade++;
            }
            else
            {
                moves.Add(m);
            }
        }
        var spans = new List<ContourSpan>(layer.Contours.Count);
        foreach (var s in layer.Contours)
            spans.Add(s.EntryTravelIndex >= 0 && converted.Contains(s.EntryTravelIndex)
                ? s with { EntryTravelIndex = -1 }
                : s);
        return (moves, spans, bridgesMade);
    }

    /// <summary>Greedy nearest-entry ordering of extrude chains + welded emission.</summary>
    private static (List<ToolpathMove> Moves, List<ContourSpan> Spans, int Bridges) Reorder(
        List<List<ToolpathMove>> chains, Vector3? prevEnd, float bridgeMax)
    {
        // A chain that begins with a layer stitch is physically welded to the
        // previous layer's end — it stays first and keeps its orientation.
        var ordered = new List<List<ToolpathMove>>(chains.Count);
        var remaining = new List<List<ToolpathMove>>(chains);
        Vector3 cursor;
        if (chains[0][0].IsLayerStitch)
        {
            ordered.Add(chains[0]);
            remaining.RemoveAt(0);
            cursor = chains[0][^1].To;
        }
        else
        {
            cursor = prevEnd ?? chains[0][0].From;
        }

        while (remaining.Count > 0)
        {
            int bestI = -1, bestRot = 0;
            bool bestRev = false;
            float bestD = float.MaxValue;
            for (int ci = 0; ci < remaining.Count; ci++)
            {
                var c = remaining[ci];
                if (IsClosed(c))
                {
                    for (int vi = 0; vi < c.Count; vi++)
                    {
                        float d = Vector3.DistanceSquared(cursor, c[vi].From);
                        if (d < bestD) { bestD = d; bestI = ci; bestRot = vi; bestRev = false; }
                    }
                }
                else
                {
                    float d0 = Vector3.DistanceSquared(cursor, c[0].From);
                    float d1 = Vector3.DistanceSquared(cursor, c[^1].To);
                    if (d0 < bestD) { bestD = d0; bestI = ci; bestRot = 0; bestRev = false; }
                    if (d1 < bestD) { bestD = d1; bestI = ci; bestRot = 0; bestRev = true; }
                }
            }
            var pick = remaining[bestI];
            remaining.RemoveAt(bestI);
            if (bestRev) pick = Reverse(pick);
            else if (bestRot > 0) pick = Rotate(pick, bestRot);
            ordered.Add(pick);
            cursor = pick[^1].To;
        }

        // Emit: per chain — nothing if touching, extrude bridge if within
        // 3× bead, travel otherwise.
        var moves = new List<ToolpathMove>();
        var spans = new List<ContourSpan>(ordered.Count);
        int bridgesMade = 0;
        var pos = prevEnd ?? ordered[0][0].From;
        bool first = true;
        foreach (var chain in ordered)
        {
            bool stitched = first && chain[0].IsLayerStitch;
            float gap = Vector3.Distance(pos, chain[0].From);
            int entryTravel = -1;
            if (!stitched && gap > Eps)
            {
                if (gap <= bridgeMax)
                {
                    moves.Add(new ToolpathMove(pos, chain[0].From, MoveKind.Extrude)
                    {
                        Normal = chain[0].Normal,
                        HeightScale = chain[0].HeightScale,
                        IsLayerStitch = first,
                    });
                    bridgesMade++;
                }
                else
                {
                    entryTravel = moves.Count;
                    moves.Add(new ToolpathMove(pos, chain[0].From, MoveKind.Travel)
                    {
                        Normal = chain[0].Normal,
                        IsLayerChange = first,
                    });
                }
            }
            spans.Add(new ContourSpan(moves.Count, chain.Count, IsClosed(chain), entryTravel));
            moves.AddRange(chain);
            pos = chain[^1].To;
            first = false;
        }
        return (moves, spans, bridgesMade);
    }

    /// <summary>Maximal runs of point-contiguous extrude moves; travels are dropped.</summary>
    private static List<List<ToolpathMove>> SplitChains(List<ToolpathMove> src)
    {
        var chains = new List<List<ToolpathMove>>();
        List<ToolpathMove>? cur = null;
        foreach (var m in src)
        {
            if (m.Kind != MoveKind.Extrude)
            {
                cur = null;
                continue;
            }
            if (cur is not null && Vector3.DistanceSquared(cur[^1].To, m.From) <= ChainEps * ChainEps)
            {
                cur.Add(m);
            }
            else
            {
                cur = [m];
                chains.Add(cur);
            }
        }
        return chains;
    }

    private static bool IsClosed(List<ToolpathMove> c)
        => c.Count >= 3 && Vector3.DistanceSquared(c[0].From, c[^1].To) <= ChainEps * ChainEps;

    private static List<ToolpathMove> Rotate(List<ToolpathMove> c, int at)
    {
        var r = new List<ToolpathMove>(c.Count);
        r.AddRange(c[at..]);
        r.AddRange(c[..at]);
        return r;
    }

    private static List<ToolpathMove> Reverse(List<ToolpathMove> c)
    {
        var r = new List<ToolpathMove>(c.Count);
        for (int i = c.Count - 1; i >= 0; i--)
            r.Add(c[i] with { From = c[i].To, To = c[i].From });
        return r;
    }
}
