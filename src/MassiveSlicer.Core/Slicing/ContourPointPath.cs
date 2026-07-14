using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Shortest-path selection along a recorded (or synthesized) contour of extrude beads.
/// Used by edit-mode point selection: pick A, Shift+click B → all points between on
/// the shortest route along the path (open: unique interval; closed: min of two ways).
/// </summary>
public static class ContourPointPath
{
    /// <summary>
    /// Build the shortest sequence of move indices from <paramref name="fromMove"/> to
    /// <paramref name="toMove"/> along a shared contour on <paramref name="layer"/>.
    /// Returns one contiguous <see cref="ContourSpan"/>, or two when the closed-loop
    /// route wraps past the contour seam. Empty list if the moves are not on one path.
    /// </summary>
    public static IReadOnlyList<ContourSpan> ShortestPath(
        ToolpathLayer layer, int fromMove, int toMove, float beadMm = 6f)
    {
        if (layer is null || layer.Moves.Count == 0) return Array.Empty<ContourSpan>();
        int nMoves = layer.Moves.Count;
        if ((uint)fromMove >= (uint)nMoves || (uint)toMove >= (uint)nMoves)
            return Array.Empty<ContourSpan>();

        if (fromMove == toMove)
            return [new ContourSpan(fromMove, 1, Closed: false, EntryTravelIndex: -1)];

        if (!TryResolveSharedContour(layer, fromMove, toMove, beadMm, out var contour))
            return Array.Empty<ContourSpan>();

        return ShortestAlongContour(contour, fromMove, toMove);
    }

    /// <summary>
    /// Shortest path between two move indices that both lie inside <paramref name="contour"/>.
    /// </summary>
    public static IReadOnlyList<ContourSpan> ShortestAlongContour(
        ContourSpan contour, int a, int b)
    {
        int start = contour.Start;
        int n = Math.Max(0, contour.Count);
        if (n <= 0) return Array.Empty<ContourSpan>();
        int end = start + n - 1;
        if (a < start || a > end || b < start || b > end)
            return Array.Empty<ContourSpan>();
        if (a == b)
            return [new ContourSpan(a, 1, Closed: false, EntryTravelIndex: -1)];

        // Open path — only one route.
        if (!contour.Closed)
        {
            int lo = Math.Min(a, b);
            int hi = Math.Max(a, b);
            return [new ContourSpan(lo, hi - lo + 1, Closed: false, EntryTravelIndex: -1)];
        }

        // Closed loop: compare forward vs backward arc lengths (in move steps).
        int ia = a - start;
        int ib = b - start;
        int fwd = (ib - ia + n) % n;   // steps a → b forward
        int back = (ia - ib + n) % n;  // steps a → b backward

        if (fwd <= back)
        {
            // Walk forward a → … → b (may wrap past end → start).
            if (a <= b)
                return [new ContourSpan(a, b - a + 1, Closed: false, EntryTravelIndex: -1)];
            return
            [
                new ContourSpan(a, end - a + 1, Closed: false, EntryTravelIndex: -1),
                new ContourSpan(start, b - start + 1, Closed: false, EntryTravelIndex: -1),
            ];
        }

        // Walk backward a → … → b (equivalent covering range on the short arc).
        if (b <= a)
            return [new ContourSpan(b, a - b + 1, Closed: false, EntryTravelIndex: -1)];
        // a < b but short path is the long way around: [start..a] U [b..end]
        return
        [
            new ContourSpan(start, a - start + 1, Closed: false, EntryTravelIndex: -1),
            new ContourSpan(b, end - b + 1, Closed: false, EntryTravelIndex: -1),
        ];
    }

    /// <summary>
    /// Find a contour (recorded or synthesized) that contains both move indices.
    /// </summary>
    public static bool TryResolveSharedContour(
        ToolpathLayer layer, int a, int b, float beadMm, out ContourSpan contour)
    {
        contour = new ContourSpan(0, 0, false, -1);
        if (layer.Contours.Count > 0)
        {
            ContourSpan? ca = null, cb = null;
            foreach (var c in layer.Contours)
            {
                int cEnd = c.Start + Math.Max(0, c.Count) - 1;
                if (a >= c.Start && a <= cEnd) ca = c;
                if (b >= c.Start && b <= cEnd) cb = c;
            }
            if (ca is { } x && cb is { } y
                && x.Start == y.Start && x.Count == y.Count)
            {
                contour = x;
                return x.Count > 0;
            }
            // Different recorded contours — not one path.
            if (ca is not null || cb is not null)
                return false;
        }

        // No Contours (or hit outside them): grow a continuous extrude run around a,
        // require b inside it.
        if (!TryGrowConnectedRun(layer, a, beadMm, out int lo, out int hi, out bool closed))
            return false;
        if (b < lo || b > hi) return false;
        contour = new ContourSpan(lo, hi - lo + 1, closed, EntryTravelIndex: -1);
        return true;
    }

    /// <summary>
    /// Expand along consecutive Extrude moves (gap ≤ 1.25× bead) from a seed.
    /// </summary>
    public static bool TryGrowConnectedRun(
        ToolpathLayer layer, int seed, float beadMm,
        out int lo, out int hi, out bool closed)
    {
        lo = hi = seed;
        closed = false;
        var moves = layer.Moves;
        if ((uint)seed >= (uint)moves.Count) return false;
        if (moves[seed].Kind != MoveKind.Extrude || moves[seed].IsWipe)
            return false;

        float gapTol = MathF.Max(beadMm, 0.5f) * 1.25f;
        bool wantLightning = moves[seed].IsLightning;

        while (lo > 0 && CanJoin(moves, lo - 1, lo, wantLightning, gapTol))
            lo--;
        while (hi < moves.Count - 1 && CanJoin(moves, hi, hi + 1, wantLightning, gapTol))
            hi++;

        closed = lo < hi
            && CanJoinEnds(moves[hi], moves[lo], wantLightning, gapTol);
        return true;
    }

    private static bool CanJoin(
        IReadOnlyList<ToolpathMove> moves, int a, int b,
        bool wantLightning, float gapTol)
    {
        if ((uint)a >= (uint)moves.Count || (uint)b >= (uint)moves.Count) return false;
        var ma = moves[a];
        var mb = moves[b];
        if (ma.Kind != MoveKind.Extrude || mb.Kind != MoveKind.Extrude) return false;
        if (ma.IsWipe || mb.IsWipe) return false;
        if (ma.IsLayerStitch || mb.IsLayerStitch || ma.IsLayerChange || mb.IsLayerChange)
            return false;
        if (ma.IsLightning != wantLightning || mb.IsLightning != wantLightning) return false;
        return System.Numerics.Vector3.Distance(ma.To, mb.From) <= gapTol;
    }

    private static bool CanJoinEnds(
        ToolpathMove last, ToolpathMove first, bool wantLightning, float gapTol)
    {
        if (last.Kind != MoveKind.Extrude || first.Kind != MoveKind.Extrude) return false;
        if (last.IsWipe || first.IsWipe) return false;
        if (last.IsLightning != wantLightning || first.IsLightning != wantLightning) return false;
        return System.Numerics.Vector3.Distance(last.To, first.From) <= gapTol;
    }
}
