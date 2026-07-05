using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Re-positions the seam of an already-generated <see cref="Toolpath"/> in place — no re-slice.
/// For each recorded closed contour (<see cref="ToolpathLayer.Contours"/>), the loop is rotated so
/// it starts at the vertex nearest the closest placed seam point, and the loop's entry travel is
/// retargeted to that new start. With several seam points, each loop seams to its nearest one, so
/// different regions/islands can carry their own seam.
/// </summary>
public static class ToolpathSeamEditor
{
    /// <summary>
    /// Re-seams every closed contour toward the nearest of <paramref name="seamPointsXY"/> (world
    /// XY). Deterministic: the result depends only on geometry and seam points, not the loop's
    /// current rotation, so it is safe to re-apply. Returns the number of loops that moved.
    /// </summary>
    public static int ApplySeams(Toolpath toolpath, IReadOnlyList<Vector2> seamPointsXY)
    {
        if (toolpath is null || seamPointsXY is null || seamPointsXY.Count == 0)
            return 0;

        int reseamed = 0;
        foreach (var layer in toolpath.Layers)
        {
            foreach (var span in layer.Contours)
            {
                if (!span.Closed || span.Count < 3) continue;
                // Guard against stale spans (e.g. a move list edited outside this path).
                if (span.Start < 0 || span.Start + span.Count > layer.Moves.Count) continue;

                int k = FindSeamVertex(layer.Moves, span, seamPointsXY);
                if (k <= 0) continue; // already seamed at that vertex

                RotateLoop(layer.Moves, span, k);
                reseamed++;
            }
        }
        return reseamed;
    }

    /// <summary>Index (within the span) of the loop vertex closest to any seam point.</summary>
    private static int FindSeamVertex(
        IReadOnlyList<ToolpathMove> moves, ContourSpan span, IReadOnlyList<Vector2> seams)
    {
        int bestK = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < span.Count; i++)
        {
            var v = moves[span.Start + i].From;
            float d = NearestSeamDist2(v.X, v.Y, seams);
            if (d < bestD) { bestD = d; bestK = i; }
        }
        return bestK;
    }

    private static float NearestSeamDist2(float x, float y, IReadOnlyList<Vector2> seams)
    {
        float best = float.MaxValue;
        foreach (var s in seams)
        {
            float dx = x - s.X, dy = y - s.Y;
            float d = dx * dx + dy * dy;
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>
    /// Rotates the closed loop's extrude moves so it starts at vertex <paramref name="k"/>.
    /// Each move i runs v_i → v_(i+1 mod n), so rotating the move references by k re-orders the
    /// cycle correctly (moves keep their per-segment data). The entry travel is retargeted to v_k.
    /// </summary>
    private static void RotateLoop(List<ToolpathMove> moves, ContourSpan span, int k)
    {
        int start = span.Start, count = span.Count;

        var rotated = new ToolpathMove[count];
        for (int i = 0; i < count; i++)
            rotated[i] = moves[start + ((i + k) % count)];
        for (int i = 0; i < count; i++)
            moves[start + i] = rotated[i];

        if (span.EntryTravelIndex >= 0 && span.EntryTravelIndex < moves.Count)
        {
            var travel = moves[span.EntryTravelIndex];
            if (travel.Kind == MoveKind.Travel)
                moves[span.EntryTravelIndex] = travel with { To = moves[start].From };
        }
    }
}
