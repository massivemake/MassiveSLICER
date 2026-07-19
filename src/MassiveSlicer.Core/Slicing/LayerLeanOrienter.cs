using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// "Poor man's non-planar" for planar slicing: leans the tool toward the nearest
/// deposited material on the PREVIOUS layer, so stepped walls print with the TCP
/// following the local wall lean instead of staying vertical.
///
/// For each cut move with no assigned normal (the planar default), the midpoint is
/// matched against the previous layer's cut segments via a spatial hash; the
/// direction from that nearest point to the midpoint is the local stacking lean.
/// The final normal is UnitZ rotated toward that lean by
/// <c>strength × leanAngle</c>, hard-capped at <c>maxTiltDeg</c>
/// (same semantics as the Geodesic surface-follow controls).
///
/// Moves with no nearby support (first layer, bridges, islands) stay vertical.
/// Moves that already carry a normal (geodesic/curved/overhang orientation) are
/// left untouched.
/// </summary>
public static class LayerLeanOrienter
{
    /// <summary>Search radius = this many bead widths around the move midpoint.</summary>
    const float SearchRadiusBeads = 2f;

    public static void ApplyInPlace(Toolpath toolpath, float strength, float maxTiltDeg, float beadWidth)
    {
        strength = Math.Clamp(strength, 0f, 1f);
        if (strength <= 1e-6f || maxTiltDeg <= 1e-3f || beadWidth <= 0f) return;
        if (toolpath.Layers.Count < 2) return;

        float cell = MathF.Max(beadWidth, 0.5f);
        float maxSearch = beadWidth * SearchRadiusBeads;

        Dictionary<(int, int), List<(Vector3 a, Vector3 b)>>? prevGrid = null;

        foreach (var layer in toolpath.Layers)
        {
            var curGrid = new Dictionary<(int, int), List<(Vector3 a, Vector3 b)>>();

            for (int i = 0; i < layer.Moves.Count; i++)
            {
                var move = layer.Moves[i];
                if (!ToolpathMoveKinds.IsCutSegment(move.Kind)) continue;

                // Orient only unassigned (planar) normals; hash every cut segment.
                if (prevGrid is { Count: > 0 } && move.Normal.LengthSquared() < 1e-8f)
                {
                    var mid = (move.From + move.To) * 0.5f;
                    if (TryNearestPoint(prevGrid, mid, cell, maxSearch, out var support))
                    {
                        var lean = mid - support;
                        // Horizontal offset drives the lean; a purely vertical stack stays vertical.
                        float horiz = MathF.Sqrt(lean.X * lean.X + lean.Y * lean.Y);
                        if (horiz > 1e-4f && lean.Z > 1e-4f)
                        {
                            var leanDir = Vector3.Normalize(lean);
                            layer.Moves[i] = move with
                            {
                                Normal = OrientationBlender.BlendNormal(leanDir, strength, maxTiltDeg)
                            };
                        }
                    }
                }

                InsertSegment(curGrid, move.From, move.To, cell);
            }

            prevGrid = curGrid;
        }
    }

    static void InsertSegment(
        Dictionary<(int, int), List<(Vector3 a, Vector3 b)>> grid,
        Vector3 a, Vector3 b, float cell)
    {
        int x0 = (int)MathF.Floor(MathF.Min(a.X, b.X) / cell);
        int x1 = (int)MathF.Floor(MathF.Max(a.X, b.X) / cell);
        int y0 = (int)MathF.Floor(MathF.Min(a.Y, b.Y) / cell);
        int y1 = (int)MathF.Floor(MathF.Max(a.Y, b.Y) / cell);
        for (int gx = x0; gx <= x1; gx++)
        for (int gy = y0; gy <= y1; gy++)
        {
            if (!grid.TryGetValue((gx, gy), out var list))
                grid[(gx, gy)] = list = [];
            list.Add((a, b));
        }
    }

    /// <summary>Nearest point (3D, interpolated along the segment) on the previous
    /// layer's segments to <paramref name="p"/>, searching by XY distance.</summary>
    static bool TryNearestPoint(
        Dictionary<(int, int), List<(Vector3 a, Vector3 b)>> grid,
        Vector3 p, float cell, float maxSearch, out Vector3 nearest)
    {
        nearest = default;
        int cx = (int)MathF.Floor(p.X / cell);
        int cy = (int)MathF.Floor(p.Y / cell);
        int ring = Math.Max(1, (int)MathF.Ceiling(maxSearch / cell));

        float bestD2 = maxSearch * maxSearch;
        bool found = false;
        for (int gx = cx - ring; gx <= cx + ring; gx++)
        for (int gy = cy - ring; gy <= cy + ring; gy++)
        {
            if (!grid.TryGetValue((gx, gy), out var segs)) continue;
            foreach (var (a, b) in segs)
            {
                var q = ClosestPointXY(p, a, b);
                float dx = q.X - p.X, dy = q.Y - p.Y;
                float d2 = dx * dx + dy * dy;
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    nearest = q;
                    found = true;
                }
            }
        }
        return found;
    }

    /// <summary>Closest point on segment ab to p, parameterised in XY, returned in 3D.</summary>
    static Vector3 ClosestPointXY(Vector3 p, Vector3 a, Vector3 b)
    {
        float abx = b.X - a.X, aby = b.Y - a.Y;
        float len2 = abx * abx + aby * aby;
        if (len2 < 1e-12f) return a;
        float t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2;
        t = Math.Clamp(t, 0f, 1f);
        return a + (b - a) * t;
    }
}
