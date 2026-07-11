using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Applies the brush tool's Remove marks to a sliced toolpath: extrude moves whose
/// midpoint falls inside any Remove mark are deleted, and each contiguous deleted
/// run is spliced with a single travel so the chain stays connected. Marks are
/// world-space spheres, so they survive re-slices and setting changes.
/// </summary>
public static class ToolpathPaintFilter
{
    public static void ApplyRemovals(Toolpath toolpath, IReadOnlyList<PaintMark> marks)
    {
        var removals = marks.Where(m => m.Kind == PaintMarkKind.Remove).ToList();
        if (removals.Count == 0) return;

        foreach (var layer in toolpath.Layers)
        {
            var moves = layer.Moves;
            List<ToolpathMove>? kept = null;      // allocated on first removal
            Vector3? runStart = null;             // where the removed run began

            for (int i = 0; i < moves.Count; i++)
            {
                var mv = moves[i];
                bool remove = mv.Kind == MoveKind.Extrude
                    && !mv.IsLayerStitch
                    && InAnyMark(removals, (mv.From + mv.To) * 0.5f);

                if (remove)
                {
                    kept ??= new List<ToolpathMove>(moves.Take(i));
                    runStart ??= mv.From;
                    continue;
                }
                if (kept is null) continue;       // nothing removed yet — fast path

                if (runStart is { } start)
                {
                    // Splice the gap. Removed beads mean the extruder must jump.
                    if (Vector3.Distance(start, mv.From) > 0.01f)
                        kept.Add(new ToolpathMove(start, mv.From, MoveKind.Travel));
                    runStart = null;
                }
                kept.Add(mv);
            }

            if (kept is not null)
            {
                layer.Moves.Clear();
                layer.Moves.AddRange(kept);       // trailing removed run just ends the layer
            }
        }

        toolpath.Layers.RemoveAll(l => !l.Moves.Any(m => m.Kind == MoveKind.Extrude));
    }

    private static bool InAnyMark(List<PaintMark> marks, Vector3 p)
    {
        foreach (var m in marks)
            if (Vector3.DistanceSquared(p, m.Center) <= m.Radius * m.Radius)
                return true;
        return false;
    }

    /// <summary>
    /// Projects Bridge marks onto every slicing plane as structured manual demand:
    /// support-bar samples (full-width T) and column-foot samples (single mouth aim).
    /// A mark demands on ITS OWN plane only so support grows below the paint.
    /// </summary>
    public static IReadOnlyList<ManualDemandLayer>? ProjectBridgeMarks(
        IReadOnlyList<PaintMark> marks,
        int layerCount,
        Func<int, (Vector3 Origin, Vector3 Normal, Vector3 U, Vector3 V)> frameOf,
        float halfBandMm)
    {
        var bridges = marks.Where(m => m.Kind == PaintMarkKind.Bridge).ToList();
        if (bridges.Count == 0) return null;

        var perLayer = new ManualDemandLayer[layerCount];
        for (int li = 0; li < layerCount; li++)
            perLayer[li] = new ManualDemandLayer();

        for (int li = 0; li < layerCount; li++)
        {
            var f = frameOf(li);
            foreach (var m in bridges)
            {
                float sd = Vector3.Dot(m.Center - f.Origin, f.Normal);
                if (MathF.Abs(sd) > halfBandMm) continue;
                var rel = m.Center - sd * f.Normal - f.Origin;
                var p2 = new Vector2(Vector3.Dot(rel, f.U), Vector3.Dot(rel, f.V));
                // ColumnFoot stays foot; None and SupportBar feed the T bar run.
                if (m.BridgeRole == PaintBridgeRole.ColumnFoot)
                    perLayer[li].ColumnFoot.Add(p2);
                else
                    perLayer[li].SupportBar.Add(p2);
            }
        }

        bool any = false;
        for (int li = 0; li < layerCount && !any; li++)
            any = perLayer[li].HasAny;
        return any ? perLayer : null;
    }
}
