using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Splices <see cref="StructuralSupportSpec"/> pockets into a sliced toolpath as a real
/// duct: out through a break in the wall, along a two-bead arm, around the pocket, and back.
/// <para>
/// The construction is driven by the ARM, not by the toolpath. The arm axis runs from the
/// spec's fixed anchor to the pocket, and the two legs are fixed lines half a bead either
/// side of it — so they are exactly parallel and exactly one bead apart along their whole
/// length, and cannot pinch or cross. Everything else is a consequence of those two lines:
/// where they cut the pocket is the pocket mouth, where they cut the wall is the surface
/// break. Nothing is measured after the fact.
/// </para>
/// <para>
/// The bead is deposited centred on the path, so "touching without overlapping" means two
/// centrelines a full bead apart — that is where the one-bead figure comes from throughout.
/// Earlier versions measured a bead of ARC LENGTH along the wall or the outline instead,
/// whose straight-line gap is always smaller and, across a corner, much smaller.
/// </para>
/// </summary>
public static class StructuralSupportPlanner
{
    /// <summary>Search radius for wall/leg intersections, as a multiple of the bead width.
    /// Keeps a leg line from grabbing a different island across the layer.</summary>
    const float SearchBeads = 8f;

    public static void Apply(Toolpath toolpath, SliceSettings settings)
    {
        var specs = settings.StructuralSupports;
        if (specs.Count == 0 || toolpath.Layers.Count == 0)
        {
            // Say so rather than vanishing: "nothing was built" and "nothing was asked for"
            // look identical from the outside, and that cost a long debugging detour.
            System.Console.WriteLine($"[support] skipped: {specs.Count} spec(s), "
                + $"{toolpath.Layers.Count} layer(s) — nothing to do");
            return;
        }

        foreach (var spec in specs)
        {
            string label = string.IsNullOrWhiteSpace(spec.Name) ? "support" : spec.Name;
            if (!spec.Enabled)
            {
                System.Console.WriteLine($"[support] {label}: SKIPPED — disabled");
                continue;
            }
            var outline = spec.BuildOutline();
            if (outline.Length < 3)
            {
                System.Console.WriteLine($"[support] {label}: SKIPPED — degenerate outline "
                    + $"({outline.Length} pts, {spec.WidthMm:0.#}x{spec.DepthMm:0.#} mm)");
                continue;
            }

            int lo = Math.Max(0, spec.AnchorLayer - Math.Max(0, spec.LayersDown));
            int hi = Math.Min(toolpath.Layers.Count - 1,
                spec.AnchorLayer + Math.Max(0, spec.LayersUp));

            float bead = MathF.Max(0.1f, settings.BeadWidth);
            float h = bead * 0.5f;
            int start = Math.Clamp(spec.AnchorLayer, 0, toolpath.Layers.Count - 1);

            // Anchor limit. A duct one bead wide cannot exit centred on a point less than
            // half a bead from the end of a wall run — half the mouth would hang off the end,
            // and clamping the stray leg instead just squeezes the arm. So pull the anchor
            // inboard until the whole mouth lands on wall. Done ONCE, at the anchor layer, so
            // the arm axis and the pocket mouth stay fixed for the entire stack.
            var anchor = LimitAnchorToRunInterior(
                toolpath.Layers[start], new Vector2(spec.AnchorX, spec.AnchorY), h);

            // ── Arm geometry, resolved ONCE from fixed data ───────────────────────────
            // The axis aims at the pocket's CENTRE, not at its nearest boundary point. Aiming
            // at the nearest point can put the axis on a corner, and then a leg offset half a
            // bead to one side passes clean outside the pocket and has no mouth at all.
            // Through the centre, a miss is geometrically impossible: for a convex outline,
            // any line offset less than the inradius from an interior point still crosses the
            // boundary twice (half a bead vs 21 mm on a 2x4, or the radius on a circle).
            var axis = new Vector2(spec.CenterX, spec.CenterY) - anchor;
            if (axis.LengthSquared() < 1e-8f)
            {
                System.Console.WriteLine($"[support] {label}: SKIPPED — anchor "
                    + $"({anchor.X:0.#}, {anchor.Y:0.#}) sits on the pocket centre, no arm "
                    + "direction");
                continue;
            }
            var u = Vector2.Normalize(axis);
            var perp = new Vector2(-u.Y, u.X);

            // Pocket mouth = where each leg line first meets the outline. Fixed, so the
            // pocket never drifts; the wall end is free to move as the wall does.
            bool hit1 = TryRayHitOutline(outline, anchor + perp * h, u, out int mouthEdge1, out var mouth1);
            bool hit2 = TryRayHitOutline(outline, anchor - perp * h, u, out int mouthEdge2, out var mouth2);
            if (!hit1 || !hit2)
            {
                System.Console.WriteLine($"[support] {label}: SKIPPED — leg line missed the "
                    + $"pocket (leg1 hit={hit1}, leg2 hit={hit2}) · anchor ({anchor.X:0.#}, "
                    + $"{anchor.Y:0.#}) → centre ({spec.CenterX:0.#}, {spec.CenterY:0.#}) · "
                    + $"axis ({u.X:0.###}, {u.Y:0.###}) · half-bead {h:0.##} mm · "
                    + $"{outline.Length} outline pts");
                continue;
            }

            // The outbound journey can never head back toward the other arm — that arc is
            // what the mouth just trimmed away. It follows the pocket AROUND instead. The
            // "around" arc is simply the one containing the pocket's far side.
            var wrap = BuildWrapArc(outline, mouthEdge1, mouthEdge2, anchor, u, mouth1, mouth2);

            // ── One-way termination, gated on the real condition ─────────────────────
            // The test is Jeff's rule literally: does THIS layer's wall cross into the
            // break? The two leg lines either still meet wall or they don't — no magic
            // distance involved. Once they don't, the arm ends and never resumes, because a
            // column cannot restart in mid-air.
            //
            // The previous version measured distance from the FIXED anchor and quit past one
            // bead. That was wrong for a leaning wall: the cross-section walks sideways as
            // the stack rises, so a wall that is perfectly present — and that the leg lines
            // still cross cleanly — was declared absent after a handful of layers. On a wall
            // leaning ~10 degrees, 6 mm of drift is only ~12 layers of height.
            // `track` follows the break up the stack, so a leaning wall is tracked layer to
            // layer instead of being measured against a fixed point hundreds of layers below.
            // ── Pass 1: survey, don't build ──────────────────────────────────────────
            // The arm must END on a layer whose legs genuinely CROSS the wall. Stopping at
            // the last layer that produced *any* attachment ends it on a leg clamped to the
            // end of a run (TryWallHit's rescue path) — a degenerate join that doubles back
            // on itself instead of stepping up cleanly. Backing off to the last crossed
            // layer costs a few layers of pocket height and removes the artifact entirely.
            //
            // Surveying the WHOLE range first also makes a wall that dips out of reach and
            // returns visible as a dip rather than a permanent end. We still stop at the
            // last clean layer below it — resuming would leave a hole in the pocket column,
            // which is worse than a short one — but we can now say it happened instead of
            // silently truncating.
            int topClean = Survey(toolpath, start, hi, +1, anchor, u, mouth1, mouth2, wrap,
                bead, out int topAny, out bool recoveredAbove);
            int botClean = Survey(toolpath, start - 1, lo, -1, anchor, u, mouth1, mouth2, wrap,
                bead, out int botAny, out bool recoveredBelow);

            // ── Pass 2: build up to the last cleanly-crossed layer ───────────────────
            int built = 0;
            var track = anchor;
            for (int li = start; li <= topClean; li++)
            {
                if (!ApplyToLayer(toolpath.Layers[li], track, u, mouth1, mouth2, wrap, bead,
                        out var nextTrack, out _))
                    break;
                track = nextTrack;
                built++;
            }
            track = anchor;
            for (int li = start - 1; li >= botClean; li--)
            {
                if (!ApplyToLayer(toolpath.Layers[li], track, u, mouth1, mouth2, wrap, bead,
                        out var nextTrack, out _))
                    break;
                track = nextTrack;
                built++;
            }

            int heldBack = Math.Max(0, topAny - topClean);
            System.Console.WriteLine(
                $"[support] {label}: {built} layer(s) built, L{lo + 1}..L{hi + 1} requested, "
                + $"top L{topClean + 1}"
                + (topClean >= hi
                    ? " (reached the top of its range)"
                    : heldBack > 0
                        ? $" (ended {heldBack} layer(s) early — the wall stopped crossing the "
                          + $"break cleanly above here; last any-attachment layer was L{topAny + 1})"
                        : " (wall no longer reaches the break — arm ends there and does not resume)")
                + (botClean > lo ? $", bottom L{botClean + 1}" : "")
                + (recoveredAbove || recoveredBelow
                    ? " · NOTE: the wall came back into reach further along, so this arm was "
                      + "truncated at a dip rather than at the real end of the wall"
                    : ""));
        }
    }

    /// <summary>
    /// Walks <paramref name="from"/> toward <paramref name="to"/> in steps of
    /// <paramref name="step"/> WITHOUT building anything, and reports how far the arm can go.
    /// <para>
    /// Returns the last layer whose legs genuinely CROSS the wall — where the arm should end.
    /// <paramref name="lastAny"/> is the last layer that produced any attachment at all,
    /// clamped ones included: that is where the arm used to stop, so the difference is how
    /// many degenerate layers are being dropped. <paramref name="recovered"/> is true when a
    /// cleanly-crossed layer exists BEYOND a layer that had nothing — i.e. the wall dipped out
    /// of reach and came back, so the end we are choosing is a dip, not the wall's real end.
    /// </para>
    /// <para>
    /// The tracked break is only advanced on layers that actually attached, so a gap does not
    /// throw the track away.
    /// </para>
    /// </summary>
    static int Survey(
        Toolpath toolpath, int from, int to, int step, Vector2 anchor, Vector2 u,
        Vector2 mouth1, Vector2 mouth2, Vector2[] wrap, float bead,
        out int lastAny, out bool recovered)
    {
        int lastClean = from - step;   // "nothing built" sits one step behind the start
        lastAny  = from - step;
        recovered = false;
        bool sawGap = false;

        var track = anchor;
        for (int li = from; step > 0 ? li <= to : li >= to; li += step)
        {
            if (li < 0 || li >= toolpath.Layers.Count) break;
            if (!ApplyToLayer(toolpath.Layers[li], track, u, mouth1, mouth2, wrap, bead,
                    out var nextTrack, out bool clamped, probe: true))
            {
                sawGap = true;
                continue;                       // may come back into reach further along
            }
            track   = nextTrack;
            lastAny = li;
            if (clamped) continue;              // attached, but on a clamped leg — not an end
            lastClean = li;
            if (sawGap) recovered = true;       // clean wall above a gap: we truncated at a dip
        }
        return lastClean;
    }

    /// <summary>
    /// Splices the duct into one layer. False when this layer's wall no longer crosses into
    /// the break — which is what ends the arm. <paramref name="track"/> is the previous
    /// layer's break (the anchor on the first layer); <paramref name="nextTrack"/> reports
    /// this layer's, so a leaning wall is followed rather than measured against a fixed point.
    /// </summary>
    static bool ApplyToLayer(
        ToolpathLayer layer, Vector2 track, Vector2 u,
        Vector2 mouth1, Vector2 mouth2, Vector2[] wrap, float bead,
        out Vector2 nextTrack, out bool clamped, bool probe = false)
    {
        nextTrack = track;
        clamped = false;

        // Reference move: nearest to the tracked break. Supplies Z and the move template.
        int refMove = -1;
        float refD2 = float.MaxValue;
        Vector3 refPoint = default;
        for (int i = 0; i < layer.Moves.Count; i++)
        {
            var mv = layer.Moves[i];
            if (mv.Kind != MoveKind.Extrude || mv.IsWipe || mv.IsResumeRamp) continue;
            var (p, _, d2) = ClosestOnSegmentXY(track, mv.From, mv.To);
            if (d2 < refD2) { refD2 = d2; refMove = i; refPoint = p; }
        }
        if (refMove < 0) return false;

        float z = refPoint.Z;
        float search = bead * SearchBeads;

        // The break must be a gap in ONE continuous wall pass, so both roots have to live in
        // the same contiguous extrude run as the reference move. Without this the splice's
        // RemoveRange could span a TRAVEL between two zig-zag passes and delete it, silently
        // merging two runs into one — which loses a pair of seam markers (they are derived
        // from each run's first/last extrude move) and changes the printed path.
        int runLo = refMove, runHi = refMove;
        while (runLo - 1 >= 0 && IsWallMove(layer.Moves[runLo - 1])
               && Vector3.Distance(layer.Moves[runLo - 1].To, layer.Moves[runLo].From) < 0.05f)
            runLo--;
        while (runHi + 1 < layer.Moves.Count && IsWallMove(layer.Moves[runHi + 1])
               && Vector3.Distance(layer.Moves[runHi].To, layer.Moves[runHi + 1].From) < 0.05f)
            runHi++;

        // Surface break = where each leg line crosses the wall. Never "half a bead along the
        // wall either way" — at an oblique arm that gives a gap narrower than a bead, and at
        // the end of an open run it gives half a gap.
        if (!TryWallHit(layer, mouth1, u, track, search, bead, runLo, runHi,
                out int idx1, out var root1, out bool clamped1))
            return false;
        if (!TryWallHit(layer, mouth2, u, track, search, bead, runLo, runHi,
                out int idx2, out var root2, out bool clamped2))
            return false;
        clamped = clamped1 || clamped2;

        // Hand the midpoint of this layer's break to the next layer.
        nextTrack = new Vector2((root1.X + root2.X) * 0.5f, (root1.Y + root2.Y) * 0.5f);

        // Probing only wants "could this layer carry the arm, and how cleanly" — the caller
        // surveys the whole stack before committing, so it must not mutate anything yet.
        if (probe) return true;

        // Path order: whichever root the wall reaches first is where it stops.
        bool oneFirst = idx1 < idx2
            || (idx1 == idx2
                && Vector3.DistanceSquared(layer.Moves[idx1].From, root1)
                   <= Vector3.DistanceSquared(layer.Moves[idx1].From, root2));

        int headIdx = oneFirst ? idx1 : idx2;
        int tailIdx = oneFirst ? idx2 : idx1;
        var headRoot = oneFirst ? root1 : root2;
        var tailRoot = oneFirst ? root2 : root1;
        var headMouth = oneFirst ? mouth1 : mouth2;
        var tailMouth = oneFirst ? mouth2 : mouth1;
        // The stored arc runs mouth1 → mouth2; entering from the other end reverses it.
        var arc = oneFirst ? wrap : wrap.Reverse().ToArray();

        if (tailIdx < headIdx) return false;                       // shouldn't happen; bail safe
        if (tailIdx - headIdx > 64) return false;                  // sanity: never eat a huge run

        var template = layer.Moves[refMove];
        var head = layer.Moves[headIdx];
        var tail = layer.Moves[tailIdx];

        var duct = new List<ToolpathMove>(arc.Length + 4);
        var prev = headRoot;
        void Emit(Vector3 to)
        {
            if (Vector3.DistanceSquared(prev, to) < 1e-6f) { prev = to; return; }
            duct.Add(new ToolpathMove(prev, to, MoveKind.Extrude)
            {
                Normal = template.Normal,
                HeightScale = template.HeightScale,
            });
            prev = to;
        }

        Vector3 At(Vector2 v) => new(v.X, v.Y, z);
        Emit(At(headMouth));                       // outbound leg
        foreach (var v in arc) Emit(At(v));        // around the pocket, away from the mouth
        Emit(At(tailMouth));                       // close onto the far mouth
        Emit(tailRoot);                            // return leg
        if (duct.Count == 0) return false;

        var replaced = new List<ToolpathMove>(duct.Count + 2);
        if (Vector3.Distance(head.From, headRoot) > 1e-4f)
            replaced.Add(head with { To = headRoot });
        replaced.AddRange(duct);
        if (Vector3.Distance(tailRoot, tail.To) > 1e-4f)
            replaced.Add(tail with { From = tailRoot });

        layer.Moves.RemoveRange(headIdx, tailIdx - headIdx + 1);
        layer.Moves.InsertRange(headIdx, replaced);

        // Recorded contour spans are index-based — they no longer match.
        layer.Contours.Clear();
        return true;
    }

    // ── Pocket-side geometry (all fixed per spec) ────────────────────────────────────

    /// <summary>
    /// Where the arm's two legs actually LAND on the pocket. Each leg is a line half a bead
    /// either side of the anchor→centre axis, and its mouth is that line's FIRST crossing of
    /// the outline — so the arm attaches wherever the shot from the anchor happens to hit,
    /// which is usually not a corner.
    /// <para>
    /// Public so a viewport preview can be drawn from the same math the builder uses. A
    /// preview that computes its own approximation — the nearest outline vertex, say — points
    /// at somewhere the printer never goes.
    /// </para>
    /// <para>
    /// Pass the resolved anchor if you have one: the builder may pull it inboard at the anchor
    /// layer (<see cref="LimitAnchorToRunInterior"/>) when it sits within half a bead of the
    /// end of a wall run, which tilts the axis slightly.
    /// </para>
    /// </summary>
    public static bool TryArmMouths(
        StructuralSupportSpec spec, float beadWidth, Vector2 anchor,
        out Vector2 legStart1, out Vector2 mouth1,
        out Vector2 legStart2, out Vector2 mouth2)
    {
        legStart1 = legStart2 = mouth1 = mouth2 = default;
        var outline = spec.BuildOutline();
        if (outline.Length < 3) return false;

        var axis = new Vector2(spec.CenterX, spec.CenterY) - anchor;
        if (axis.LengthSquared() < 1e-8f) return false;
        var u = Vector2.Normalize(axis);
        var perp = new Vector2(-u.Y, u.X);
        float h = MathF.Max(0.1f, beadWidth) * 0.5f;

        legStart1 = anchor + perp * h;
        legStart2 = anchor - perp * h;
        return TryRayHitOutline(outline, legStart1, u, out _, out mouth1)
            && TryRayHitOutline(outline, legStart2, u, out _, out mouth2);
    }

    /// <summary>
    /// First crossing of the ray (origin, dir) with the outline — the NEAR side. A band of
    /// one bead cuts clean through a pocket much wider than a bead, so each leg line crosses
    /// twice; only the near crossing is the mouth.
    /// </summary>
    static bool TryRayHitOutline(
        Vector2[] poly, Vector2 origin, Vector2 dir, out int edgeIdx, out Vector2 hit)
    {
        edgeIdx = -1;
        hit = default;
        float bestT = float.MaxValue;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            var ab = b - a;
            float denom = Cross(ab, dir);
            if (MathF.Abs(denom) < 1e-9f) continue;              // parallel
            float s = Cross(origin - a, dir) / denom;            // param along the edge
            if (s < -1e-4f || s > 1f + 1e-4f) continue;
            var p = a + ab * Math.Clamp(s, 0f, 1f);
            float t = Vector2.Dot(p - origin, dir);              // param along the ray
            if (t <= 1e-4f) continue;
            if (t < bestT) { bestT = t; edgeIdx = i; hit = p; }
        }
        return edgeIdx >= 0;
    }

    /// <summary>
    /// Vertices to visit going from <paramref name="mouth1"/> AROUND the pocket to
    /// <paramref name="mouth2"/>. The outbound arm must never head back into the arc the
    /// mouth just trimmed away, so the arc we want is the one containing the pocket's far
    /// side (measured along the arm axis).
    /// </summary>
    static Vector2[] BuildWrapArc(
        Vector2[] poly, int e1, int e2, Vector2 anchor, Vector2 u, Vector2 mouth1, Vector2 mouth2)
    {
        int n = poly.Length;

        // Forward: v[e1+1], v[e1+2], … , v[e2].
        var fwd = new List<Vector2>(n);
        for (int k = 1; ; k++)
        {
            int vi = (e1 + k) % n;
            fwd.Add(poly[vi]);
            if (vi == e2 || k > n) break;
        }
        // Backward: v[e1], v[e1-1], … , v[e2+1].
        var bwd = new List<Vector2>(n);
        for (int k = 0; ; k++)
        {
            int vi = (e1 - k + n * 2) % n;
            bwd.Add(poly[vi]);
            if (vi == (e2 + 1) % n || k > n) break;
        }

        // The pocket's far vertex along the arm axis can only lie on the way round.
        int farIdx = 0;
        float farT = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            float t = Vector2.Dot(poly[i] - anchor, u);
            if (t > farT) { farT = t; farIdx = i; }
        }
        var far = poly[farIdx];
        bool fwdHasFar = fwd.Contains(far);
        bool bwdHasFar = bwd.Contains(far);

        if (fwdHasFar != bwdHasFar)
            return (fwdHasFar ? fwd : bwd).ToArray();

        // Both mouths on the same edge: each candidate is a full lap, so pick the one that
        // leaves mouth1 heading AWAY from mouth2 — still "never into the trimmed arc".
        var away = mouth1 - mouth2;
        float fwdDot = fwd.Count > 0 ? Vector2.Dot(fwd[0] - mouth1, away) : 0f;
        float bwdDot = bwd.Count > 0 ? Vector2.Dot(bwd[0] - mouth1, away) : 0f;
        return (fwdDot >= bwdDot ? fwd : bwd).ToArray();
    }

    // ── Wall-side geometry (per layer) ───────────────────────────────────────────────

    /// <summary>
    /// Where the leg line (through <paramref name="linePoint"/>, direction
    /// <paramref name="dir"/>) crosses the wall, nearest the anchor. Falls back to the wall
    /// point closest to that line — which is what clamps a leg to the end of an open run
    /// instead of leaving it with no root at all.
    /// </summary>
    static bool TryWallHit(
        ToolpathLayer layer, Vector2 linePoint, Vector2 dir, Vector2 anchor, float maxDist,
        float maxPerp, int runLo, int runHi, out int moveIdx, out Vector3 hit, out bool clamped)
    {
        moveIdx = -1;
        hit = default;
        clamped = false;
        float bestD2 = float.MaxValue;
        float maxD2 = maxDist * maxDist;

        for (int i = runLo; i <= runHi; i++)
        {
            var mv = layer.Moves[i];
            if (!IsWallMove(mv)) continue;
            var a = new Vector2(mv.From.X, mv.From.Y);
            var b = new Vector2(mv.To.X, mv.To.Y);
            var ab = b - a;
            float denom = Cross(ab, dir);
            if (MathF.Abs(denom) < 1e-9f) continue;
            float s = Cross(linePoint - a, dir) / denom;
            if (s < -1e-4f || s > 1f + 1e-4f) continue;
            s = Math.Clamp(s, 0f, 1f);
            var p = a + ab * s;
            float d2 = Vector2.DistanceSquared(p, anchor);
            if (d2 > maxD2 || d2 >= bestD2) continue;
            bestD2 = d2;
            moveIdx = i;
            hit = mv.From + (mv.To - mv.From) * s;
        }
        if (moveIdx >= 0) return true;

        // Past here the leg no longer CROSSES wall — it is being clamped onto the end of a
        // run. Report that, because an arm whose top layer is clamped is the degenerate
        // attachment that turns back on itself instead of stepping up cleanly.
        clamped = true;

        // No crossing. The ONLY case worth rescuing is a leg line running just past the end
        // of an open run, where the true root is the run's endpoint a hair off the line.
        // Bound the rescue to one bead of perpendicular offset: beyond that the wall really
        // isn't in the break any more, and clamping would resurrect the original bug where
        // the arm chased a receding wall into an unprintable overhang.
        float bestPerp = MathF.Max(0.05f, maxPerp);
        for (int i = runLo; i <= runHi; i++)
        {
            var mv = layer.Moves[i];
            if (!IsWallMove(mv)) continue;
            foreach (var (pt, s) in new[] { (mv.From, 0f), (mv.To, 1f) })
            {
                var q = new Vector2(pt.X, pt.Y);
                if (Vector2.DistanceSquared(q, anchor) > maxD2) continue;
                var rel = q - linePoint;
                float perpDist = MathF.Abs(Cross(dir, rel));
                if (perpDist >= bestPerp) continue;
                bestPerp = perpDist;
                moveIdx = i;
                hit = mv.From + (mv.To - mv.From) * s;
            }
        }
        return moveIdx >= 0;
    }

    /// <summary>
    /// Moves the anchor inboard along its wall run so a full one-bead mouth fits on wall.
    /// Returns it unchanged when there is already room on both sides.
    /// </summary>
    static Vector2 LimitAnchorToRunInterior(ToolpathLayer layer, Vector2 anchor, float h)
    {
        int refMove = -1;
        float refD2 = float.MaxValue;
        Vector3 refPoint = default;
        for (int i = 0; i < layer.Moves.Count; i++)
        {
            var mv = layer.Moves[i];
            if (mv.Kind != MoveKind.Extrude || mv.IsWipe || mv.IsResumeRamp) continue;
            var (p, _, d2) = ClosestOnSegmentXY(anchor, mv.From, mv.To);
            if (d2 < refD2) { refD2 = d2; refMove = i; refPoint = p; }
        }
        if (refMove < 0) return anchor;

        var (_, _, back) = WalkRun(layer, refMove, refPoint, h, -1);
        var (_, _, fwd)  = WalkRun(layer, refMove, refPoint, h, +1);

        if (back < h - 1e-4f)
        {
            var (_, pt, _) = WalkRun(layer, refMove, refPoint, h - back, +1);
            return new Vector2(pt.X, pt.Y);
        }
        if (fwd < h - 1e-4f)
        {
            var (_, pt, _) = WalkRun(layer, refMove, refPoint, h - fwd, -1);
            return new Vector2(pt.X, pt.Y);
        }
        return anchor;
    }

    /// <summary>
    /// Walks the contiguous extrude run away from <paramref name="fromPoint"/> on move
    /// <paramref name="idx"/> — backwards for <paramref name="dir"/> = -1, forwards for +1 —
    /// consuming up to <paramref name="dist"/> mm, and reports how much was actually
    /// available. Crossing move boundaries matters: a curved wall is chopped into chords far
    /// shorter than a bead. Stops at a travel, wipe, resume ramp, disconnected joint, or the
    /// end of the layer.
    /// </summary>
    static (int idx, Vector3 pt, float consumed) WalkRun(
        ToolpathLayer layer, int idx, Vector3 fromPoint, float dist, int dir)
    {
        int i = idx;
        var p = fromPoint;
        float remaining = dist, consumed = 0f;
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
            var joint = dir < 0 ? nm.To : nm.From;
            if (Vector3.Distance(joint, target) > 0.05f) return (i, target, consumed);

            i = next;
            p = target;
        }
    }

    // ── Small geometry helpers ──────────────────────────────────────────────────────

    /// <summary>A printable wall segment the break may cut — plain extrusion, not a wipe or
    /// a resume ramp, and never a travel.</summary>
    static bool IsWallMove(ToolpathMove mv)
        => mv.Kind == MoveKind.Extrude && !mv.IsWipe && !mv.IsResumeRamp;

    static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    static Vector2 ClosestOnSegment2D(Vector2 q, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-12f) return a;
        return a + ab * Math.Clamp(Vector2.Dot(q - a, ab) / len2, 0f, 1f);
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
