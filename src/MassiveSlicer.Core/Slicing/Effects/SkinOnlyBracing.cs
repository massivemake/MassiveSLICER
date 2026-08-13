using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Shared support for "pattern the skin only": decorative effects displace the wall, and the
/// structure inside it (infill, X-bracing, Formbound fill, supports) stays straight.
/// <para>
/// Leaving structure completely undisplaced would break it off the skin — the wall moves by up
/// to the pattern amplitude along its normal, so every brace would end short of, or poke
/// through, the wall by that much. On an LFAM bead that is a bonding failure, not a cosmetic
/// one. So a brace still follows the wall <b>at its ends</b>: each end takes the displacement
/// of the nearest wall point, and the displacement is blended linearly along the brace.
/// </para>
/// <para>
/// Blending linearly is what keeps a brace straight. A straight segment under a displacement
/// that varies linearly along it is still a straight segment — the map is affine — so the brace
/// is only shifted and tilted, never bowed.
/// </para>
/// </summary>
internal static class SkinOnlyBracing
{
    /// <summary>
    /// Where a layer's wall ended up: original XY paired with the displacement applied to it.
    /// Built while the effect walks the wall, then queried for the structure behind it.
    /// </summary>
    internal sealed class WallField
    {
        private readonly List<(Vector2 At, Vector3 Delta)> _samples = [];

        /// <summary>Records that wall point <paramref name="original"/> moved to <paramref name="displaced"/>.</summary>
        public void Record(Vector3 original, Vector3 displaced)
        {
            var delta = displaced - original;
            if (delta.LengthSquared() < 1e-10f) return;          // unmoved wall teaches nothing

            // One sample per few mm is plenty: the query is "which part of the wall is this
            // brace attached to", and effects subdivide walls far finer than that.
            var at = new Vector2(original.X, original.Y);
            if (_samples.Count > 0 && Vector2.DistanceSquared(_samples[^1].At, at) < 4f) return;
            _samples.Add((at, delta));
        }

        public bool IsEmpty => _samples.Count == 0;

        /// <summary>Displacement of the wall point nearest <paramref name="p"/> in XY.</summary>
        public Vector3 DeltaNear(Vector3 p)
        {
            var    q    = new Vector2(p.X, p.Y);
            float  best = float.MaxValue;
            Vector3 d   = Vector3.Zero;
            foreach (var (at, delta) in _samples)
            {
                float dist = Vector2.DistanceSquared(at, q);
                if (dist < best) { best = dist; d = delta; }
            }
            return d;
        }
    }

    /// <summary>
    /// Displacement to apply at each end of every non-wall move in <paramref name="moves"/>.
    /// Index-parallel to <paramref name="moves"/>; entries for wall, travel and empty moves are
    /// zero and should be ignored by the caller.
    /// </summary>
    internal static (Vector3 AtFrom, Vector3 AtTo)[] BlendForStructure(
        IReadOnlyList<ToolpathMove> moves, WallField wall)
    {
        var result = new (Vector3, Vector3)[moves.Count];
        if (wall.IsEmpty) return result;

        int i = 0;
        while (i < moves.Count)
        {
            if (!IsStructure(moves[i])) { i++; continue; }

            // Contiguous run of structure moves = one brace / one fill line.
            int start = i;
            float total = 0f;
            var cum = new List<float> { 0f };
            var prevTo = moves[i].From;
            int j = i;
            while (j < moves.Count && IsStructure(moves[j])
                   && Vector3.DistanceSquared(moves[j].From, prevTo) <= 1.0f)
            {
                total += Vector3.Distance(moves[j].From, moves[j].To);
                cum.Add(total);
                prevTo = moves[j].To;
                j++;
            }

            var dStart = wall.DeltaNear(moves[start].From);
            var dEnd   = wall.DeltaNear(moves[j - 1].To);

            for (int k = start; k < j; k++)
            {
                float u0 = total > 1e-4f ? cum[k - start]     / total : 0f;
                float u1 = total > 1e-4f ? cum[k - start + 1] / total : 1f;
                result[k] = (Vector3.Lerp(dStart, dEnd, u0), Vector3.Lerp(dStart, dEnd, u1));
            }

            i = Math.Max(j, i + 1);
        }

        return result;
    }

    /// <summary>Extrusion that is not skin — what should stay straight.</summary>
    internal static bool IsStructure(ToolpathMove m)
        => m.Kind == MoveKind.Extrude && !m.IsWall && !m.IsLayerStitch;
}
