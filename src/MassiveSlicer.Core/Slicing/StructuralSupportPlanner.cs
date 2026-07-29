using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Applies <see cref="StructuralSupportSpec"/> modifiers to a sliced toolpath:
/// on every affected layer, the wall path is split at the point nearest the spec's
/// ANCHOR, and a detour is spliced in — neck out to the helper shape, a full wrap
/// of the outline, neck back — as continuous extrusion. The anchor and outline are
/// identical on every layer, so the neck and pocket stack vertically.
/// </summary>
public static class StructuralSupportPlanner
{
    public static void Apply(Toolpath toolpath, SliceSettings settings)
    {
        var specs = settings.StructuralSupports;
        if (specs.Count == 0 || toolpath.Layers.Count == 0) return;

        foreach (var spec in specs)
        {
            if (!spec.Enabled) continue;
            int lo = Math.Max(0, spec.AnchorLayer - Math.Max(0, spec.LayersDown));
            int hi = Math.Min(toolpath.Layers.Count - 1,
                spec.AnchorLayer + Math.Max(0, spec.LayersUp));
            var outline = spec.BuildOutline();
            if (outline.Length < 3) continue;

            // Outline entry vertex is resolved ONCE, from the spec's fixed anchor — never
            // per layer from that layer's own split point. Deriving it per layer let the
            // neck attach to a different corner of the pocket as the wall wandered
            // underneath it, which reads as the whole rectangle jumping around even though
            // the footprint itself never moved. The anchor is fixed data, so this is too.
            var anchor2 = new Vector2(spec.AnchorX, spec.AnchorY);
            int entryIdx = 0;
            float entryD2 = float.MaxValue;
            for (int i = 0; i < outline.Length; i++)
            {
                float dx = outline[i].X - anchor2.X, dy = outline[i].Y - anchor2.Y;
                float d2 = dx * dx + dy * dy;
                if (d2 < entryD2) { entryD2 = d2; entryIdx = i; }
            }

            // Wrap direction is ALSO fixed per spec, for the same reason as entryIdx.
            // Deciding it per layer (by whichever leg pairing was shorter) made the
            // traversal flip partway up the stack as the wall moved, so the outgoing and
            // returning legs swapped mouths mid-print.
            int nOut = outline.Length;
            float hMouth = MathF.Max(0.05f, settings.BeadWidth * 0.5f);
            var cMouth = StepAlong(outline[entryIdx], outline[(entryIdx + 1) % nOut], hMouth);
            var dMouth = StepAlong(outline[entryIdx], outline[(entryIdx - 1 + nOut) % nOut], hMouth);
            bool ccw = Vector2.Distance(anchor2, cMouth) <= Vector2.Distance(anchor2, dMouth);

            for (int li = lo; li <= hi; li++)
                ApplyToLayer(toolpath.Layers[li], spec, outline, entryIdx, settings.BeadWidth, ccw);
        }
    }

    static void ApplyToLayer(
        ToolpathLayer layer, StructuralSupportSpec spec, Vector2[] outline, int entryIdx,
        float bead, bool ccw)
    {
        var anchor = new Vector2(spec.AnchorX, spec.AnchorY);

        // 1) Find the extrude segment closest to the anchor (XY) and the split point on it.
        int bestMove = -1;
        float bestD2 = float.MaxValue;
        Vector3 bestP = default;
        float bestT = 0f;
        for (int i = 0; i < layer.Moves.Count; i++)
        {
            var mv = layer.Moves[i];
            if (mv.Kind != MoveKind.Extrude || mv.IsWipe || mv.IsResumeRamp) continue;
            var (p, t, d2) = ClosestOnSegmentXY(anchor, mv.From, mv.To);
            if (d2 < bestD2)
            {
                bestD2 = d2;
                bestMove = i;
                bestP = p;
                bestT = t;
            }
        }
        if (bestMove < 0) return;

        var wall = layer.Moves[bestMove];
        float z = bestP.Z;

        // 2) Outline entry vertex is supplied by the caller — resolved once from the spec's
        //    fixed anchor so the neck lands on the SAME corner on every layer.

        // 3) Build the detour as a real DUCT with three one-bead gaps, so nothing is
        //    deposited on top of anything else. The bead is laid centred on the path
        //    (renderer: pt ± beadWidth/2), so two centrelines must sit a full bead width
        //    apart to touch without overlapping — half a bead either side of the axis.
        //
        //      wall ──A          C────┐  <- pocket mouth (gap ≈ 1 bead, straddles `entry`)
        //             │  leg 1   │    │
        //             │          │  pocket wrap (OPEN loop, C → … → D)
        //             │  leg 2   │    │
        //      wall ──B          D────┘
        //             ^ wall mouth (gap ≈ 1 bead, centred on the anchor)
        //
        //    Previously all of this collapsed onto two points: the wall ran straight
        //    through the anchor, both legs retraced one identical centreline, and the wrap
        //    opened and closed at the same vertex — four beads piled on each junction.
        float h = MathF.Max(0.05f, bead * 0.5f);
        Vector3 At(Vector2 v) => new(v.X, v.Y, z);

        // Wall mouth: stop short of the anchor and resume past it, measured along the wall.
        var wallDir2 = new Vector2(wall.To.X - wall.From.X, wall.To.Y - wall.From.Y);
        float wallLen = wallDir2.Length();
        var wallDir = wallLen > 1e-6f ? wallDir2 / wallLen : new Vector2(1f, 0f);
        // Never eat more than the segment can spare, or a short wall move would vanish.
        float wallHalf = wallLen > 1e-6f ? MathF.Min(h, wallLen * 0.45f) : 0f;
        var aWall = new Vector3(bestP.X - wallDir.X * wallHalf, bestP.Y - wallDir.Y * wallHalf, bestP.Z);
        var bWall = new Vector3(bestP.X + wallDir.X * wallHalf, bestP.Y + wallDir.Y * wallHalf, bestP.Z);

        // Pocket mouth: open the outline loop either side of the entry vertex.
        int n = outline.Length;
        var entryV = outline[entryIdx];
        var nextV  = outline[(entryIdx + 1) % n];
        var prevV  = outline[(entryIdx - 1 + n) % n];
        var cPocket = At(StepAlong(entryV, nextV, h));   // wrap START (CCW from entry)
        var dPocket = At(StepAlong(entryV, prevV, h));   // wrap END   (CW  from entry)

        // The duct ALWAYS leaves from the wall.From-side root and returns to the wall.To-side
        // root, so the two wall pieces can never overlap. Wrap direction comes from the
        // caller (fixed per spec) so it cannot flip between layers.
        var mouthIn  = ccw ? cPocket : dPocket;
        var mouthOut = ccw ? dPocket : cPocket;

        var detour = new List<ToolpathMove>(n + 4);
        var prev = aWall;
        void Emit(Vector3 to)
        {
            if (Vector3.DistanceSquared(prev, to) < 1e-6f) { prev = to; return; }
            detour.Add(new ToolpathMove(prev, to, MoveKind.Extrude)
            {
                Normal = wall.Normal,
                HeightScale = wall.HeightScale,
            });
            prev = to;
        }

        Emit(mouthIn);                                  // leg 1: wall mouth → pocket mouth
        for (int k = 1; k <= n - 1; k++)                // open wrap, in the chosen direction
            Emit(At(outline[ccw ? (entryIdx + k) % n : (entryIdx - k + n) % n]));
        Emit(mouthOut);                                 // finish the wrap at the far mouth
        Emit(bWall);                                    // leg 2: pocket mouth → wall mouth

        if (detour.Count == 0) return;

        // 4) Splice: the wall ENDS at aWall and RESUMES at bWall — a real break in the
        //    surface, one bead wide and centred on the anchor, instead of running
        //    continuously beneath the neck. The path stays one unbroken extrusion.
        var replaced = new List<ToolpathMove>(detour.Count + 2);
        if (Vector3.Distance(wall.From, aWall) > 1e-4f)
            replaced.Add(wall with { To = aWall });
        replaced.AddRange(detour);
        if (Vector3.Distance(bWall, wall.To) > 1e-4f)
            replaced.Add(wall with { From = bWall });
        if (replaced.Count == 0) return;

        layer.Moves.RemoveAt(bestMove);
        layer.Moves.InsertRange(bestMove, replaced);

        // Recorded contour spans (if any) are index-based — they no longer match.
        layer.Contours.Clear();
    }

    /// <summary>
    /// Point <paramref name="dist"/> along the edge <paramref name="from"/> →
    /// <paramref name="to"/>. Capped at 45% of the edge so opening a pocket mouth can
    /// never swallow a whole short edge (small pockets, fine circle facets).
    /// </summary>
    static Vector2 StepAlong(Vector2 from, Vector2 to, float dist)
    {
        var d = to - from;
        float len = d.Length();
        if (len < 1e-6f) return from;
        return from + d / len * MathF.Min(dist, len * 0.45f);
    }

    static (Vector3 p, float t, float d2) ClosestOnSegmentXY(Vector2 q, Vector3 a, Vector3 b)
    {
        float abx = b.X - a.X, aby = b.Y - a.Y;
        float len2 = abx * abx + aby * aby;
        float t = len2 < 1e-12f
            ? 0f
            : Math.Clamp(((q.X - a.X) * abx + (q.Y - a.Y) * aby) / len2, 0f, 1f);
        var p = a + (b - a) * t;
        float dx = p.X - q.X, dy = p.Y - q.Y;
        return (p, t, dx * dx + dy * dy);
    }
}
