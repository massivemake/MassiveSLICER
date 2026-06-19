using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Fills a slice polygon with a continuous infill pattern.
///
/// Algorithm:
///   1. Rotate all polygons by -angleDeg so scan lines are horizontal.
///   2. Scanline fill with even-odd rule — produces segments inside the polygon.
///   3. Greedy nearest-neighbour ordering: after each segment, pick the closest
///      unvisited segment endpoint (either end, so direction is chosen per hop).
///   4. Connections ≤ 2× spacing → Extrude (same-wall stitch).
///      Connections  > 2× spacing → Travel (cross-void jump).
///   5. Rotate all points back by +angleDeg before emitting.
/// </summary>
public static class InfillGenerator
{
    public static void Emit(
        IReadOnlyList<List<Vector2>> polygons,
        float z,
        ToolpathLayer layer,
        float spacingMm,
        float angleDeg)
    {
        if (polygons.Count == 0 || spacingMm < 0.1f) return;

        float rad  = angleDeg * (MathF.PI / 180f);
        float cosN = MathF.Cos(-rad), sinN = MathF.Sin(-rad);
        float cosP = MathF.Cos( rad), sinP = MathF.Sin( rad);

        var rotPolys = new List<List<Vector2>>(polygons.Count);
        foreach (var poly in polygons)
        {
            var r = new List<Vector2>(poly.Count);
            foreach (var p in poly) r.Add(Rot(p, cosN, sinN));
            rotPolys.Add(r);
        }

        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var poly in rotPolys)
            foreach (var p in poly)
            {
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
        if (maxY - minY < spacingMm * 0.5f) return;

        // Collect all segments as (xLeft, xRight, y) in scan frame
        var xa = new List<float>();  // left endpoint X
        var xb = new List<float>();  // right endpoint X
        var ys = new List<float>();  // Y

        for (float y = minY + spacingMm * 0.5f; y < maxY; y += spacingMm)
        {
            var xs = Intersections(rotPolys, y);
            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                xa.Add(xs[i]);
                xb.Add(xs[i + 1]);
                ys.Add(y);
            }
        }

        int n = xa.Count;
        if (n == 0) return;

        var visited      = new bool[n];
        float stitchSq   = spacingMm * 2f * (spacingMm * 2f);  // (2× spacing)²
        bool    hasLast  = false;
        Vector2 lastScan = default;
        Vector3 lastPos  = default;

        for (int iter = 0; iter < n; iter++)
        {
            // Find the nearest unvisited segment, considering both endpoints as
            // possible start (direction is chosen to minimise the connection gap).
            int   best  = -1;
            bool  rev   = false;
            float bestD = float.MaxValue;

            for (int i = 0; i < n; i++)
            {
                if (visited[i]) continue;
                float dy = ys[i] - (hasLast ? lastScan.Y : ys[i]);

                // Forward: start at xa[i]
                float dxF = xa[i] - (hasLast ? lastScan.X : xa[i]);
                float d2F = dxF * dxF + dy * dy;
                if (d2F < bestD) { bestD = d2F; best = i; rev = false; }

                // Reverse: start at xb[i]
                float dxR = xb[i] - (hasLast ? lastScan.X : xb[i]);
                float d2R = dxR * dxR + dy * dy;
                if (d2R < bestD) { bestD = d2R; best = i; rev = true; }
            }

            if (best < 0) break;
            visited[best] = true;

            float fromX = rev ? xb[best] : xa[best];
            float toX   = rev ? xa[best] : xb[best];
            float sy    = ys[best];

            var worldFrom = Rot3(fromX, sy, cosP, sinP, z);
            var worldTo   = Rot3(toX,   sy, cosP, sinP, z);

            if (hasLast)
            {
                float dx = fromX - lastScan.X, dy2 = sy - lastScan.Y;
                float d2 = dx * dx + dy2 * dy2;
                if (d2 > 1e-4f)
                {
                    var kind = d2 <= stitchSq ? MoveKind.Extrude : MoveKind.Travel;
                    layer.Moves.Add(new ToolpathMove(lastPos, worldFrom, kind));
                }
            }

            layer.Moves.Add(new ToolpathMove(worldFrom, worldTo, MoveKind.Extrude));
            hasLast  = true;
            lastScan = new Vector2(toX, sy);
            lastPos  = worldTo;
        }
    }

    // ── Ghost Mesh Grid ──────────────────────────────────────────────────────────

    /// <summary>
    /// Ghost mesh variant: identical greedy ordering to <see cref="Emit"/> but every
    /// connection between segments follows the polygon perimeter (all Extrude, no Travel).
    /// On the final layer the outer perimeter is traced once after the last segment.
    /// </summary>
    public static void EmitGhostMesh(
        IReadOnlyList<List<Vector2>> polygons,
        float z,
        ToolpathLayer layer,
        float spacingMm,
        float angleDeg,
        bool isLastLayer = false)
    {
        if (polygons.Count == 0 || spacingMm < 0.1f) return;

        float rad  = angleDeg * (MathF.PI / 180f);
        float cosN = MathF.Cos(-rad), sinN = MathF.Sin(-rad);
        float cosP = MathF.Cos( rad), sinP = MathF.Sin( rad);

        var rotPolys = new List<List<Vector2>>(polygons.Count);
        foreach (var poly in polygons)
        {
            var r = new List<Vector2>(poly.Count);
            foreach (var p in poly) r.Add(Rot(p, cosN, sinN));
            rotPolys.Add(r);
        }

        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var poly in rotPolys)
            foreach (var p in poly)
            {
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
        if (maxY - minY < spacingMm * 0.5f) return;

        var chain = new BoundaryChain(rotPolys);

        var xa = new List<float>();
        var xb = new List<float>();
        var ys = new List<float>();

        for (float y = minY + spacingMm * 0.5f; y < maxY; y += spacingMm)
        {
            var xs = Intersections(rotPolys, y);
            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                xa.Add(xs[i]);
                xb.Add(xs[i + 1]);
                ys.Add(y);
            }
        }

        int segCount = xa.Count;
        if (segCount == 0) return;

        var visited  = new bool[segCount];
        bool    hasLast  = false;
        Vector2 lastScan = default;
        Vector3 lastPos  = default;

        for (int iter = 0; iter < segCount; iter++)
        {
            int   best  = -1;
            bool  rev   = false;
            float bestD = float.MaxValue;

            for (int i = 0; i < segCount; i++)
            {
                if (visited[i]) continue;
                float dy = ys[i] - (hasLast ? lastScan.Y : ys[i]);

                float dxF = xa[i] - (hasLast ? lastScan.X : xa[i]);
                float d2F = dxF * dxF + dy * dy;
                if (d2F < bestD) { bestD = d2F; best = i; rev = false; }

                float dxR = xb[i] - (hasLast ? lastScan.X : xb[i]);
                float d2R = dxR * dxR + dy * dy;
                if (d2R < bestD) { bestD = d2R; best = i; rev = true; }
            }

            if (best < 0) break;
            visited[best] = true;

            float fromX = rev ? xb[best] : xa[best];
            float toX   = rev ? xa[best] : xb[best];
            float sy    = ys[best];

            var worldFrom = Rot3(fromX, sy, cosP, sinP, z);
            var worldTo   = Rot3(toX,   sy, cosP, sinP, z);

            if (hasLast)
            {
                float dx = fromX - lastScan.X, dy2 = sy - lastScan.Y;
                if (dx * dx + dy2 * dy2 > 1e-4f)
                    EmitBoundaryWalk(chain, lastScan, new Vector2(fromX, sy),
                                     lastPos, worldFrom, layer, cosP, sinP, z);
            }

            layer.Moves.Add(new ToolpathMove(worldFrom, worldTo, MoveKind.Extrude));
            hasLast  = true;
            lastScan = new Vector2(toX, sy);
            lastPos  = worldTo;
        }

        if (isLastLayer && hasLast)
            EmitPerimeterClose(chain, lastScan, lastPos, layer, cosP, sinP, z);
    }

    // Walk the polygon boundary from fromScan to toScan (scan frame), emitting
    // Extrude moves. Ends EXACTLY at toWorld so the caller's next segment starts
    // at a precisely known position.
    private static void EmitBoundaryWalk(
        BoundaryChain chain,
        Vector2 fromScan, Vector2 toScan,
        Vector3 fromWorld, Vector3 toWorld,
        ToolpathLayer layer, float cosP, float sinP, float z)
    {
        var (piF, eF, _, arcF) = ClosestOnBoundary(chain, fromScan);
        var (piT, eT, _, arcT) = ClosestOnBoundary(chain, toScan);

        var prev3 = fromWorld;

        if (piF == piT)
        {
            var poly  = chain.Polys[piF];
            int pn    = poly.Count;
            float tot = chain.TotalLen[piF];

            float fwd   = arcT >= arcF ? arcT - arcF : arcT + tot - arcF;
            bool  goFwd = fwd <= tot * 0.5f;

            if (goFwd)
            {
                int steps = (eT - eF + pn) % pn;
                int v = (eF + 1) % pn;
                for (int i = 0; i < steps; i++)
                {
                    var wp3 = Rot3(poly[v].X, poly[v].Y, cosP, sinP, z);
                    layer.Moves.Add(new ToolpathMove(prev3, wp3, MoveKind.Extrude));
                    prev3 = wp3;
                    v = (v + 1) % pn;
                }
            }
            else
            {
                int steps = (eF - eT + pn) % pn;
                int v = eF;
                for (int i = 0; i < steps; i++)
                {
                    var wp3 = Rot3(poly[v].X, poly[v].Y, cosP, sinP, z);
                    layer.Moves.Add(new ToolpathMove(prev3, wp3, MoveKind.Extrude));
                    prev3 = wp3;
                    v = (v - 1 + pn) % pn;
                }
            }
        }
        // else: cross-polygon — fall through, prev3 = fromWorld, final step reaches toWorld

        if (Vector3.DistanceSquared(prev3, toWorld) > 1e-4f)
            layer.Moves.Add(new ToolpathMove(prev3, toWorld, MoveKind.Extrude));
    }

    // After the last infill segment, trace the entire outer perimeter once and
    // return to the projection of the last point.
    private static void EmitPerimeterClose(
        BoundaryChain chain,
        Vector2 lastScan, Vector3 lastPos,
        ToolpathLayer layer, float cosP, float sinP, float z)
    {
        // Outer polygon = largest absolute area
        int   outerPi = 0;
        float maxA    = 0f;
        for (int pi = 0; pi < chain.Polys.Count; pi++)
        {
            float a = MathF.Abs(chain.Areas[pi]);
            if (a > maxA) { maxA = a; outerPi = pi; }
        }

        var poly = chain.Polys[outerPi];
        int n    = poly.Count;
        var (eF, ptF, _) = ClosestOnPoly(chain, outerPi, lastScan);

        // Walk from lastPos to ptF if not already there
        var ptF3  = Rot3(ptF.X, ptF.Y, cosP, sinP, z);
        var prev3 = lastPos;
        if (Vector3.DistanceSquared(prev3, ptF3) > 1e-4f)
        {
            layer.Moves.Add(new ToolpathMove(prev3, ptF3, MoveKind.Extrude));
            prev3 = ptF3;
        }

        // Traverse all n vertices (full loop) then close back to ptF
        int v = (eF + 1) % n;
        for (int i = 0; i < n; i++)
        {
            var wp3 = Rot3(poly[v].X, poly[v].Y, cosP, sinP, z);
            layer.Moves.Add(new ToolpathMove(prev3, wp3, MoveKind.Extrude));
            prev3 = wp3;
            v = (v + 1) % n;
        }
        if (Vector3.DistanceSquared(prev3, ptF3) > 1e-4f)
            layer.Moves.Add(new ToolpathMove(prev3, ptF3, MoveKind.Extrude));
    }

    // ── Boundary chain (ghost mesh only) ─────────────────────────────────────────

    private sealed class BoundaryChain
    {
        public readonly IReadOnlyList<List<Vector2>> Polys;
        public readonly float[][]                    CumLen;
        public readonly float[]                      TotalLen;
        public readonly float[]                      Areas;    // signed area per polygon

        public BoundaryChain(List<List<Vector2>> polys)
        {
            Polys    = polys;
            CumLen   = new float[polys.Count][];
            TotalLen = new float[polys.Count];
            Areas    = new float[polys.Count];

            for (int pi = 0; pi < polys.Count; pi++)
            {
                var poly = polys[pi];
                int n    = poly.Count;
                var cum  = new float[n];
                float acc = 0f, area = 0f;
                for (int i = 0; i < n; i++)
                {
                    cum[i] = acc;
                    var a = poly[i];
                    var b = poly[(i + 1) % n];
                    acc  += Vector2.Distance(a, b);
                    area += a.X * b.Y - b.X * a.Y;
                }
                CumLen[pi]   = cum;
                TotalLen[pi] = acc;
                Areas[pi]    = area * 0.5f;
            }
        }
    }

    // Returns (polygon index, edge index, arc length) for the closest boundary point.
    private static (int pi, int ei, Vector2 pt, float arc)
        ClosestOnBoundary(BoundaryChain chain, Vector2 q)
    {
        float bestD2 = float.MaxValue;
        int   bestPi = 0, bestEi = 0;
        float bestT  = 0f;

        for (int pi = 0; pi < chain.Polys.Count; pi++)
        {
            var poly = chain.Polys[pi];
            int n    = poly.Count;
            for (int i = 0; i < n; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % n];
                float t  = SegProject(q, a, b);
                float d2 = Vector2.DistanceSquared(q, Vector2.Lerp(a, b, t));
                if (d2 < bestD2) { bestD2 = d2; bestPi = pi; bestEi = i; bestT = t; }
            }
        }

        var pB  = chain.Polys[bestPi];
        var a2  = pB[bestEi];
        var b2  = pB[(bestEi + 1) % pB.Count];
        float arc = chain.CumLen[bestPi][bestEi] + bestT * Vector2.Distance(a2, b2);
        return (bestPi, bestEi, Vector2.Lerp(a2, b2, bestT), arc);
    }

    // Closest point restricted to a specific polygon.
    private static (int ei, Vector2 pt, float arc)
        ClosestOnPoly(BoundaryChain chain, int pi, Vector2 q)
    {
        var poly = chain.Polys[pi];
        int n    = poly.Count;
        float bestD2 = float.MaxValue;
        int   bestEi = 0;
        float bestT  = 0f;

        for (int i = 0; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];
            float t  = SegProject(q, a, b);
            float d2 = Vector2.DistanceSquared(q, Vector2.Lerp(a, b, t));
            if (d2 < bestD2) { bestD2 = d2; bestEi = i; bestT = t; }
        }

        var a2  = poly[bestEi];
        var b2  = poly[(bestEi + 1) % n];
        float arc = chain.CumLen[pi][bestEi] + bestT * Vector2.Distance(a2, b2);
        return (bestEi, Vector2.Lerp(a2, b2, bestT), arc);
    }

    private static float SegProject(Vector2 q, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float lenSq = ab.LengthSquared();
        return lenSq < 1e-10f ? 0f : Math.Clamp(Vector2.Dot(q - a, ab) / lenSq, 0f, 1f);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static List<float> Intersections(List<List<Vector2>> polys, float y)
    {
        var xs = new List<float>(8);
        foreach (var poly in polys)
        {
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % n];
                if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
                    xs.Add(a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X));
            }
        }
        xs.Sort();
        return xs;
    }

    private static Vector2 Rot(Vector2 p, float c, float s) =>
        new(c * p.X - s * p.Y, s * p.X + c * p.Y);

    private static Vector3 Rot3(float x, float y, float c, float s, float z) =>
        new(c * x - s * y, s * x + c * y, z);
}
