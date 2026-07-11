using System.Numerics;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Spatial hash over segment endpoints for contour chaining. The greedy
/// nearest-endpoint walk in the slicers scanned every remaining segment per
/// step — O(n²) — which effectively hung on dense meshes (a Multi-Planar
/// section of the V80 drone produces enough segments that one plane never
/// finished). Cells are 1 mm, matching the 1 mm² squared-distance join
/// tolerance, so a 3×3 neighbourhood always contains every joinable endpoint.
/// </summary>
internal sealed class SegmentEndpointGrid
{
    private const float CellMm = 1.0f;

    private readonly List<(Vector2 A, Vector2 B)> _segs;
    private readonly Dictionary<(int X, int Y), List<int>> _cells;

    public SegmentEndpointGrid(List<(Vector2 A, Vector2 B)> segs)
    {
        _segs = segs;
        _cells = new Dictionary<(int, int), List<int>>(segs.Count * 2);
        for (int i = 0; i < segs.Count; i++)
        {
            Add(segs[i].A, i);
            Add(segs[i].B, i);
        }
    }

    private static (int, int) Key(Vector2 p)
        => ((int)MathF.Floor(p.X / CellMm), (int)MathF.Floor(p.Y / CellMm));

    private void Add(Vector2 p, int idx)
    {
        var k = Key(p);
        if (!_cells.TryGetValue(k, out var list))
            _cells[k] = list = new List<int>(4);
        list.Add(idx);
    }

    /// <summary>
    /// Nearest unused segment endpoint within the 3×3 neighbourhood of
    /// <paramref name="p"/>. Returns the segment index (−1 if none),
    /// whether the chain should continue from that segment's A (flip) and
    /// the squared distance. Callers keep their own accept threshold.
    /// </summary>
    public int FindNearest(Vector2 p, bool[] used, out bool flip, out float bestSq)
    {
        // Tie-breaking must replicate the original global index scan exactly:
        // lexicographic (distance, segment index, A-before-B). Cell scan order
        // resolved exact-distance ties differently, which reordered points inside
        // a chain in symmetric "zipper" regions — same point multiset, different
        // polyline — and downstream boolean ops split the contour.
        float bSq = float.MaxValue;
        int bIdx = int.MaxValue;
        int bEnd = 2;
        var (kx, ky) = Key(p);
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
            if (!_cells.TryGetValue((kx + dx, ky + dy), out var cand)) continue;
            foreach (int i in cand)
            {
                if (used[i]) continue;
                var s = _segs[i];
                float dA = Vector2.DistanceSquared(p, s.A);
                float dB = Vector2.DistanceSquared(p, s.B);
                if (dA < bSq || (dA == bSq && (i < bIdx || (i == bIdx && 0 < bEnd))))
                {
                    bSq = dA; bIdx = i; bEnd = 0;
                }
                if (dB < bSq || (dB == bSq && (i < bIdx || (i == bIdx && 1 < bEnd))))
                {
                    bSq = dB; bIdx = i; bEnd = 1;
                }
            }
        }
        bestSq = bSq;
        flip = bEnd == 1;
        return bIdx == int.MaxValue ? -1 : bIdx;
    }
}
