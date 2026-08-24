using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Vertices shown in edit Point mode. Midpoints of long wall moves hide
/// programmed corners (a 80 mm side with 3 pts/side has corners only at From/To).
/// </summary>
public static class ToolpathEditPoints
{
    public static bool IsShown(ToolpathMove m) =>
        m.Kind == MoveKind.Extrude && !m.IsLayerStitch && !m.IsLayerChange && !m.IsWipe;

    /// <summary>
    /// Programmed polyline vertices: <c>From</c> of each shown extrude plus
    /// <c>To</c> of the last move in each contiguous run (closed loops drop the
    /// duplicate close). <paramref name="flatIdx"/> is the move that owns the vertex
    /// (From → that move; trailing To → last move of the run).
    /// </summary>
    public static List<(int FlatIdx, Vector3 Pos)> Collect(Toolpath toolpath)
    {
        var events = new List<(int FlatIdx, Vector3 Pos)>();
        if (toolpath is null) return events;

        int fi = 0;
        foreach (var layer in toolpath.Layers)
        {
            int runStart = -1;
            int lastFi = -1;
            Vector3 lastTo = default;
            foreach (var move in layer.Moves)
            {
                if (IsShown(move))
                {
                    if (runStart < 0) runStart = events.Count;
                    events.Add((fi, move.From));
                    lastFi = fi;
                    lastTo = move.To;
                }
                else if (lastFi >= 0)
                {
                    CloseRun(events, runStart, lastFi, lastTo);
                    runStart = -1;
                    lastFi = -1;
                }
                fi++;
            }
            if (lastFi >= 0)
                CloseRun(events, runStart, lastFi, lastTo);
        }
        return events;
    }

    /// <summary>Vertices of a picked span (From of first + To of each shown move).</summary>
    public static List<Vector3> VerticesOfSpan(ToolpathLayer layer, ContourSpan span)
    {
        var pts = new List<Vector3>();
        if (layer is null || span.Count <= 0) return pts;
        int start = Math.Max(0, span.Start);
        int end = Math.Min(layer.Moves.Count, start + span.Count);
        bool any = false;
        for (int i = start; i < end; i++)
        {
            var mv = layer.Moves[i];
            if (!IsShown(mv)) continue;
            if (!any)
            {
                pts.Add(mv.From);
                any = true;
            }
            pts.Add(mv.To);
        }
        return pts;
    }

    static void CloseRun(
        List<(int FlatIdx, Vector3 Pos)> events, int runStart, int lastFi, Vector3 lastTo)
    {
        if (runStart < 0 || runStart >= events.Count) return;
        var first = events[runStart].Pos;
        if (Vector3.DistanceSquared(first, lastTo) < 0.25f) // 0.5 mm — closed
            return;
        events.Add((lastFi, lastTo));
    }
}
