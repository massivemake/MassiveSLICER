using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Modifiers;

/// <summary>
/// Splits a <see cref="Toolpath"/> at a vertical plane (normal in the XY plane) for the Cut
/// modifier. Unlike a horizontal cut, a vertical plane can pass through the middle of a
/// layer's moves, so this clips move-by-move rather than bucketing whole layers: a move
/// entirely on one side is kept as-is, a move crossing the plane is split at the intersection
/// point, and a bridging travel move is inserted wherever removing the other side's moves
/// leaves a gap (marked <see cref="ToolpathMove.IsMergeConnector"/>, the same flag
/// <see cref="ToolpathMerger"/> uses for an equivalent reason). The source toolpath is never
/// mutated. Layer <see cref="ToolpathLayer.Index"/>/<see cref="ToolpathLayer.Z"/> are preserved
/// unchanged on both sides so the two pieces stay layer-for-layer synced with the un-cut model.
///
/// Assumes standard flat-Z planar layers (every move's endpoints share the layer's Z) — a
/// tilted/wedge layer (e.g. Multi-Planar/angled slicing) could have geometry on both sides of
/// the cut plane at a single Z, which this does not attempt to detect or handle; that combination
/// is out of scope for the current Cut modifier.
///
/// Does not attempt to re-derive <see cref="ToolpathLayer.Contours"/> — the original contour
/// index ranges no longer correspond to the rebuilt move lists, so both sides start with none.
/// </summary>
public static class VerticalCutSplitter
{
    public sealed record SplitResult(Toolpath Positive, Toolpath Negative);

    /// <param name="source">The toolpath to split. Never modified.</param>
    /// <param name="planePoint">A point on the cut plane, in WORLD space — toolpath moves are
    /// baked in world space at slice time, unlike MeshData, which is local.</param>
    /// <param name="planeNormal">Unit (or non-zero) normal of the plane, in world space; the side it points toward is <see cref="SplitResult.Positive"/>.</param>
    public static SplitResult Split(Toolpath source, Vector3 planePoint, Vector3 planeNormal)
    {
        var n = Vector3.Normalize(planeNormal);
        var positive = new Toolpath { FormboundStats = source.FormboundStats };
        var negative = new Toolpath { FormboundStats = source.FormboundStats };

        foreach (var layer in source.Layers)
        {
            var posMoves = ClipSide(layer.Moves, planePoint, n, keepPositive: true);
            var negMoves = ClipSide(layer.Moves, planePoint, n, keepPositive: false);

            if (posMoves.Count > 0) positive.Layers.Add(BuildLayer(layer, posMoves));
            if (negMoves.Count > 0) negative.Layers.Add(BuildLayer(layer, negMoves));
        }

        return new SplitResult(positive, negative);
    }

    private static ToolpathLayer BuildLayer(ToolpathLayer source, List<ToolpathMove> moves)
    {
        var layer = new ToolpathLayer(source.Index, source.Z)
        {
            Height       = source.Height,
            PlaneNormal  = source.PlaneNormal,
            ThermalTempC = source.ThermalTempC,
        };
        layer.Moves.AddRange(moves);
        return layer;
    }

    private static List<ToolpathMove> ClipSide(
        List<ToolpathMove> moves, Vector3 planePoint, Vector3 n, bool keepPositive)
    {
        const float eps = 1e-4f;
        var result = new List<ToolpathMove>();
        Vector3? pen = null;

        float Signed(Vector3 p) => Vector3.Dot(p - planePoint, n);
        bool Keep(float d) => keepPositive ? d >= -eps : d <= eps;

        void Emit(ToolpathMove move)
        {
            if (pen is { } from && (from - move.From).LengthSquared() > 1e-8f)
                result.Add(new ToolpathMove(from, move.From, MoveKind.Travel) { IsMergeConnector = true });
            result.Add(move);
            pen = move.To;
        }

        foreach (var move in moves)
        {
            float da = Signed(move.From), db = Signed(move.To);
            bool aKeep = Keep(da), bKeep = Keep(db);

            if (aKeep && bKeep)
            {
                Emit(move);
            }
            else if (!aKeep && !bKeep)
            {
                // Entirely on the other side: drop it. The next kept move gets a
                // bridging travel automatically, from wherever the pen last was.
            }
            else
            {
                float t = Math.Clamp(da / (da - db), 0f, 1f);
                var mid = move.From + (move.To - move.From) * t;
                Emit(aKeep ? move with { To = mid } : move with { From = mid });
            }
        }

        return result;
    }
}
