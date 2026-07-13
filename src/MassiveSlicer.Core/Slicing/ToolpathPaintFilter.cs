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
    /// <param name="styleFilter">
    /// When set, only Bridge marks whose <see cref="PaintMark.SupportStyle"/> is
    /// accepted are projected (e.g. Formbound-only or Tree-only). Null = all Bridge.
    /// </param>
    public static IReadOnlyList<ManualDemandLayer>? ProjectBridgeMarks(
        IReadOnlyList<PaintMark> marks,
        int layerCount,
        Func<int, (Vector3 Origin, Vector3 Normal, Vector3 U, Vector3 V)> frameOf,
        float halfBandMm,
        bool targetSupportSelectionsOnly = false,
        Func<PaintSupportStyle, bool>? styleFilter = null)
    {
        var bridges = marks
            .Where(m => m.Kind == PaintMarkKind.Bridge
                        && (styleFilter is null || styleFilter(m.SupportStyle)))
            .ToList();
        if (bridges.Count == 0) return null;

        var perLayer = new ManualDemandLayer[layerCount];
        for (int li = 0; li < layerCount; li++)
            perLayer[li] = new ManualDemandLayer();

        // Target Support Selections: pin each mark to its nearest layer (by plane
        // signed distance — works for Planar, Angled constant-tilt, and Multi-Planar),
        // then only project onto that layer and a few BELOW it (support grows down).
        // Projecting upward / every layer in a fat band was birthing Formbound T's
        // across the whole stack away from the selection.
        if (targetSupportSelectionsOnly)
        {
            // Demand is pinned to the paint plane (+ a few layers of slack so the
            // planner sees SupportBar at the selection). The steppable column itself
            // continues BELOW via tree inheritance + MaxStep retract — do NOT flood
            // dozens of lower layers with re-projected SupportBar (that re-birthed
            // mid-air full T's). Inheritance owns foundation growth.
            int belowSlack = Math.Clamp(
                (int)MathF.Ceiling(halfBandMm / MathF.Max(1f, halfBandMm * 0.35f)) + 1, 1, 4);

            foreach (var m in bridges)
            {
                int bestLi = -1;
                float bestAbs = float.MaxValue;
                for (int li = 0; li < layerCount; li++)
                {
                    var f = frameOf(li);
                    // Plane distance: works when Origin = n·d (Angled) or march origin (MP).
                    float nLen = f.Normal.Length();
                    if (nLen < 1e-8f) continue;
                    var n = f.Normal / nLen;
                    float sd = Vector3.Dot(m.Center - f.Origin, n);
                    float a = MathF.Abs(sd);
                    if (a < bestAbs) { bestAbs = a; bestLi = li; }
                }
                // Reject marks that never land near any cutting plane (stale paint).
                if (bestLi < 0 || bestAbs > MathF.Max(halfBandMm * 2.5f, halfBandMm + 6f))
                    continue;

                int lo = Math.Max(0, bestLi - belowSlack);
                for (int li = lo; li <= bestLi; li++)
                {
                    var f = frameOf(li);
                    float nLen = f.Normal.Length();
                    if (nLen < 1e-8f) continue;
                    var n = f.Normal / nLen;
                    float sd = Vector3.Dot(m.Center - f.Origin, n);
                    // Project mark onto this plane, then express in plane UV.
                    var onPlane = m.Center - sd * n;
                    var rel = onPlane - f.Origin;
                    var p2 = new Vector2(Vector3.Dot(rel, f.U), Vector3.Dot(rel, f.V));
                    if (m.BridgeRole == PaintBridgeRole.ColumnFoot)
                    {
                        perLayer[li].ColumnFoot.Add(p2);
                        perLayer[li].ColumnFootSides.Add(m.SupportSide);
                    }
                    else
                    {
                        perLayer[li].SupportBar.Add(p2);
                        perLayer[li].SupportBarSides.Add(m.SupportSide);
                    }
                }
            }
        }
        else
        {
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
                    {
                        perLayer[li].ColumnFoot.Add(p2);
                        perLayer[li].ColumnFootSides.Add(m.SupportSide);
                    }
                    else
                    {
                        perLayer[li].SupportBar.Add(p2);
                        perLayer[li].SupportBarSides.Add(m.SupportSide);
                    }
                }
            }
        }

        bool any = false;
        for (int li = 0; li < layerCount && !any; li++)
            any = perLayer[li].HasAny;
        return any ? perLayer : null;
    }
}
