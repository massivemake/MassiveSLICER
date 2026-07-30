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

            // The mouth is resolved ONCE from the spec's fixed anchor — never per layer from
            // that layer's own split point, which made the neck hop between corners as the
            // wall wandered underneath it.
            //
            // It sits on the outline EDGE nearest the anchor, NOT the nearest vertex. A
            // vertex mouth puts the two legs on two PERPENDICULAR edges, so they converge
            // and cross on the way in — visible in the viewport as an X at the pocket. An
            // edge mouth is a flat opening facing the wall, with parallel legs.
            var anchor2 = new Vector2(spec.AnchorX, spec.AnchorY);
            int nOut = outline.Length;
            int edgeIdx = 0;
            float bestEd2 = float.MaxValue;
            var onEdge = outline[0];
            for (int i = 0; i < nOut; i++)
            {
                var p = ClosestOnSegment2D(anchor2, outline[i], outline[(i + 1) % nOut]);
                float d2 = Vector2.DistanceSquared(p, anchor2);
                if (d2 < bestEd2) { bestEd2 = d2; edgeIdx = i; onEdge = p; }
            }

            float hMouth = MathF.Max(0.05f, settings.BeadWidth * 0.5f);
            var cMouth = StepAlong(onEdge, outline[(edgeIdx + 1) % nOut], hMouth);  // toward next vertex
            var dMouth = StepAlong(onEdge, outline[edgeIdx], hMouth);               // toward this vertex

            // ── Reach gate + one-way termination ─────────────────────────────────────
            // A break only means anything if THIS layer's wall actually passes through it.
            // Walk outward from the anchor layer and stop the first time the wall has
            // receded out of reach — and do NOT resume higher up even if the wall comes
            // back, because a column cannot restart in mid-air. Without this the planner
            // took the globally closest segment no matter how far away it was, so on a
            // filleted top the arm stretched after the receding wall and produced an
            // overhang it could never print.
            float reach = MathF.Max(settings.BeadWidth, 0.01f);
            int start = Math.Clamp(spec.AnchorLayer, 0, toolpath.Layers.Count - 1);
            int appliedCount = 0;
            int endedUpAt = -1, endedDownAt = -1;

            for (int li = start; li <= hi; li++)
            {
                if (ClosestWallDistanceXY(toolpath.Layers[li], anchor2) > reach)
                { endedUpAt = li; break; }
                ApplyToLayer(toolpath.Layers[li], spec, outline, edgeIdx,
                    cMouth, dMouth, settings.BeadWidth);
                appliedCount++;
            }
            for (int li = start - 1; li >= lo; li--)
            {
                if (ClosestWallDistanceXY(toolpath.Layers[li], anchor2) > reach)
                { endedDownAt = li; break; }
                ApplyToLayer(toolpath.Layers[li], spec, outline, edgeIdx,
                    cMouth, dMouth, settings.BeadWidth);
                appliedCount++;
            }

            string name = string.IsNullOrWhiteSpace(spec.Name) ? "support" : spec.Name;
            System.Console.WriteLine(
                $"[support] {name}: {appliedCount} layer(s) built"
                + (endedUpAt >= 0
                    ? $", topped out at L{endedUpAt + 1} (wall receded past {reach:0.#} mm — "
                      + "arm ends there and does not resume)"
                    : ", reached the top of its range")
                + (endedDownAt >= 0 ? $", bottomed out at L{endedDownAt + 1}" : ""));
        }
    }

    static void ApplyToLayer(
        ToolpathLayer layer, StructuralSupportSpec spec, Vector2[] outline, int edgeIdx,
        Vector2 cMouth, Vector2 dMouth, float bead)
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

        // Wall mouth: consume half a bead of wall either side of the anchor, walking ACROSS
        // adjacent moves as needed. Trimming only the one split move was wrong — a curved
        // wall is chopped into chords and the closest one to the anchor is frequently a
        // sliver (measured 0.02 mm on a real bendy wall), which collapsed the mouth to
        // nothing on those layers while the rest of the stack looked fine.
        var (headIdx, aWall, gotBack) = WalkWall(layer, bestMove, bestP, h, -1);
        var (tailIdx, bWall, gotFwd) = WalkWall(layer, bestMove, bestP, h, +1);

        // Re-balance at the END of an open path. The anchor can sit at (or within half a
        // bead of) a run's endpoint — very common when a support is placed on the end of a
        // wall — and then one side simply has no wall to give. Taking half from each side
        // left a 3 mm mouth with the two legs only half a bead apart, so they overlapped:
        // Jeff's "no gap to invent or make", which showed up as a mangled arm on end-placed
        // supports. Take the shortfall from whichever side still has wall, so the opening is
        // a full bead wherever the run is long enough to hold one.
        if (gotBack < h - 1e-4f)
            (tailIdx, bWall, gotFwd) = WalkWall(layer, bestMove, bestP, h + (h - gotBack), +1);
        else if (gotFwd < h - 1e-4f)
            (headIdx, aWall, gotBack) = WalkWall(layer, bestMove, bestP, h + (h - gotFwd), -1);

        // Pocket mouth: fixed per spec by the caller, both points on the SAME outline edge.
        int n = outline.Length;
        var cPocket = At(cMouth);
        var dPocket = At(dMouth);

        // The duct ALWAYS leaves from the wall.From-side root and returns to the wall.To-side
        // root, so the two wall pieces can never overlap.
        //
        // Which mouth the OUTGOING leg uses must be decided per layer, from the actual wall
        // roots: it is a non-crossing test, and the wall's direction rotates as the stack
        // rises. Pinning it per spec (an earlier attempt at determinism) is what made the
        // legs cross. The mouth POINTS stay fixed either way — only the traversal direction
        // adapts, so the deposited geometry is identical and nothing drifts.
        bool ccw = Vector3.Distance(aWall, cPocket) + Vector3.Distance(dPocket, bWall)
                <= Vector3.Distance(aWall, dPocket) + Vector3.Distance(cPocket, bWall);
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
        // Open wrap. Both mouths sit on edge `edgeIdx`, so a full lap visits every vertex
        // exactly once: CCW starts at the edge's far vertex, CW starts at its near one.
        if (ccw)
            for (int k = 1; k <= n; k++) Emit(At(outline[(edgeIdx + k) % n]));
        else
            for (int k = 0; k <= n - 1; k++) Emit(At(outline[(edgeIdx - k + n) % n]));
        Emit(mouthOut);                                 // finish the wrap at the far mouth
        Emit(bWall);                                    // leg 2: pocket mouth → wall mouth

        if (detour.Count == 0) return;

        // 4) Splice: the wall ENDS at aWall and RESUMES at bWall — a real break in the
        //    surface, one bead wide and centred on the anchor, instead of running
        //    continuously beneath the neck. The path stays one unbroken extrusion.
        //    Every move from headIdx..tailIdx is consumed by the mouth, so the whole run is
        //    replaced (not just the single split move).
        var head = layer.Moves[headIdx];
        var tail = layer.Moves[tailIdx];
        var replaced = new List<ToolpathMove>(detour.Count + 2);
        if (Vector3.Distance(head.From, aWall) > 1e-4f)
            replaced.Add(head with { To = aWall });
        replaced.AddRange(detour);
        if (Vector3.Distance(bWall, tail.To) > 1e-4f)
            replaced.Add(tail with { From = bWall });
        if (replaced.Count == 0) return;

        layer.Moves.RemoveRange(headIdx, tailIdx - headIdx + 1);
        layer.Moves.InsertRange(headIdx, replaced);

        // Recorded contour spans (if any) are index-based — they no longer match.
        layer.Contours.Clear();
    }

    /// <summary>
    /// Walks the contiguous extrude run away from <paramref name="fromPoint"/> on move
    /// <paramref name="idx"/> — backwards for <paramref name="dir"/> = -1, forwards for +1 —
    /// consuming <paramref name="dist"/> mm of wall, and returns the move the cut lands in
    /// plus the cut point. Crossing move boundaries is the whole point: on a curved wall the
    /// segment nearest the anchor is often far shorter than a bead, so a mouth confined to
    /// that one move would collapse. Stops at a travel, a wipe, a resume ramp, a
    /// disconnected joint, or the end of the layer, returning the furthest point reached.
    /// </summary>
    static (int idx, Vector3 pt, float consumed) WalkWall(
        ToolpathLayer layer, int idx, Vector3 fromPoint, float dist, int dir)
    {
        int i = idx;
        var p = fromPoint;
        float remaining = dist;
        float consumed = 0f;
        while (true)
        {
            var mv = layer.Moves[i];
            var target = dir < 0 ? mv.From : mv.To;
            float avail = Vector3.Distance(p, target);
            if (avail >= remaining)
            {
                var d = target - p;
                float len = d.Length();
                return (i, len > 1e-9f ? p + d / len * remaining : target, consumed + remaining);
            }

            remaining -= avail;
            consumed  += avail;
            int next = i + dir;
            if (next < 0 || next >= layer.Moves.Count) return (i, target, consumed);
            var nm = layer.Moves[next];
            if (nm.Kind != MoveKind.Extrude || nm.IsWipe || nm.IsResumeRamp)
                return (i, target, consumed);
            // Only continue through a joint that actually connects — never jump a gap.
            var joint = dir < 0 ? nm.To : nm.From;
            if (Vector3.Distance(joint, target) > 0.05f) return (i, target, consumed);

            i = next;
            p = target;
        }
    }

    /// <summary>
    /// Point <paramref name="dist"/> along the edge <paramref name="from"/> →
    /// <paramref name="to"/>. Capped at 45% of the edge so opening a pocket mouth can
    /// never swallow a whole short edge (small pockets, fine circle facets).
    /// </summary>
    /// <summary>
    /// XY distance from the anchor to the nearest printable extrude segment on a layer —
    /// i.e. "does this layer's wall still pass through the break?". <see cref="float.MaxValue"/>
    /// when the layer has no eligible extrusion at all.
    /// </summary>
    static float ClosestWallDistanceXY(ToolpathLayer layer, Vector2 anchor)
    {
        float best = float.MaxValue;
        foreach (var mv in layer.Moves)
        {
            if (mv.Kind != MoveKind.Extrude || mv.IsWipe || mv.IsResumeRamp) continue;
            var (_, _, d2) = ClosestOnSegmentXY(anchor, mv.From, mv.To);
            if (d2 < best) best = d2;
        }
        return best == float.MaxValue ? float.MaxValue : MathF.Sqrt(best);
    }

    /// <summary>Closest point to <paramref name="q"/> on the segment a→b, in 2D.</summary>
    static Vector2 ClosestOnSegment2D(Vector2 q, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-12f) return a;
        float t = Math.Clamp(Vector2.Dot(q - a, ab) / len2, 0f, 1f);
        return a + ab * t;
    }

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
