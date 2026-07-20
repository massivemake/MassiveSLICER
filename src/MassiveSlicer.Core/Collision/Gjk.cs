using System.Numerics;

namespace MassiveSlicer.Core.Collision;

/// <summary>A convex shape queryable by support mapping (max-dot extreme point).</summary>
public interface ISupport
{
    Vector3 Support(Vector3 dir);
    Aabb Bounds { get; }
}

/// <summary>
/// GJK distance between two convex shapes. Returns the separation distance,
/// or 0 when the shapes intersect (no penetration depth — callers only compare
/// against a clearance margin, so EPA is unnecessary).
/// </summary>
public static class Gjk
{
    const int MaxIterations = 128;

    public static float Distance(ISupport a, ISupport b)
    {
        // Minkowski difference support: A ⊖ B.
        Vector3 SupportMd(Vector3 d) => a.Support(d) - b.Support(-d);

        // Scale-aware epsilon.
        float scale = MathF.Max(
            a.Bounds.Extent.Length() + b.Bounds.Extent.Length(),
            1f);
        float eps = scale * 1e-6f;
        float eps2 = eps * eps;

        Span<Vector3> w = stackalloc Vector3[4];
        int count = 0;

        var v = SupportMd(new Vector3(1f, 0f, 0f));
        if (v.LengthSquared() < eps2) return 0f;
        w[count++] = v;

        for (int iter = 0; iter < MaxIterations; iter++)
        {
            float vLen2 = v.LengthSquared();
            if (vLen2 < eps2) return 0f; // origin (numerically) reached → intersecting

            var s = SupportMd(-v);

            // Convergence: no support point meaningfully closer to the origin.
            if (vLen2 - Vector3.Dot(v, s) <= eps * MathF.Sqrt(vLen2))
                return MathF.Sqrt(vLen2);

            // Duplicate support → no progress possible.
            for (int i = 0; i < count; i++)
                if (Vector3.DistanceSquared(w[i], s) < eps2)
                    return MathF.Sqrt(vLen2);

            w[count++] = s;

            bool containsOrigin = ReduceSimplex(w, ref count, out v);
            if (containsOrigin) return 0f;
        }

        return v.Length(); // iteration cap — conservative (shorter = more cautious)
    }

    /// <summary>Convenience: true when distance &lt; <paramref name="margin"/>.</summary>
    public static bool Hit(ISupport a, ISupport b, float margin) =>
        Distance(a, b) < margin;

    // ── Simplex reduction (closest point to origin + minimal supporting subset) ──

    static bool ReduceSimplex(Span<Vector3> w, ref int count, out Vector3 closest)
    {
        switch (count)
        {
            case 1:
                closest = w[0];
                return false;
            case 2:
                return Segment(w, ref count, out closest);
            case 3:
                return Triangle(w, ref count, out closest);
            default:
                return Tetrahedron(w, ref count, out closest);
        }
    }

    static bool Segment(Span<Vector3> w, ref int count, out Vector3 closest)
    {
        var a = w[0];
        var b = w[1];
        var ab = b - a;
        float t = -Vector3.Dot(a, ab);
        float d2 = ab.LengthSquared();
        if (d2 < 1e-18f || t <= 0f)
        {
            // Closest at A (b is the newest point... keep both for safety? No:
            // supporting feature is A alone).
            w[0] = a;
            count = 1;
            closest = a;
            return false;
        }
        if (t >= d2)
        {
            w[0] = b;
            count = 1;
            closest = b;
            return false;
        }
        closest = a + ab * (t / d2);
        return false;
    }

    static bool Triangle(Span<Vector3> w, ref int count, out Vector3 closest)
    {
        // Ericson, Real-Time Collision Detection §5.1.5 — point = origin.
        var a = w[0]; var b = w[1]; var c = w[2];
        var ab = b - a;
        var ac = c - a;
        var ap = -a;

        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) { Keep(w, ref count, a); closest = a; return false; }

        var bp = -b;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) { Keep(w, ref count, b); closest = b; return false; }

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float t = d1 / (d1 - d3);
            Keep(w, ref count, a, b);
            closest = a + ab * t;
            return false;
        }

        var cp = -c;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) { Keep(w, ref count, c); closest = c; return false; }

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float t = d2 / (d2 - d6);
            Keep(w, ref count, a, c);
            closest = a + ac * t;
            return false;
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
        {
            float t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            Keep(w, ref count, b, c);
            closest = b + (c - b) * t;
            return false;
        }

        // Collinear degenerate triangle: barycentric denominator vanishes.
        float sum = va + vb + vc;
        if (MathF.Abs(sum) < 1e-18f)
        {
            // Reduce via the best of the three edges.
            Span<Vector3> seg = stackalloc Vector3[2];
            float bestD2 = float.MaxValue;
            Vector3 bestClosest = a;
            Span<Vector3> bestW = stackalloc Vector3[2];
            int bestCount = 1;
            ReadOnlySpan<Vector3> tri = [a, b, c];
            for (int e = 0; e < 3; e++)
            {
                seg[0] = tri[e];
                seg[1] = tri[(e + 1) % 3];
                int cnt = 2;
                Segment(seg, ref cnt, out var cl);
                float segD2 = cl.LengthSquared();
                if (segD2 < bestD2)
                {
                    bestD2 = segD2;
                    bestClosest = cl;
                    for (int i = 0; i < cnt; i++) bestW[i] = seg[i];
                    bestCount = cnt;
                }
            }
            for (int i = 0; i < bestCount; i++) w[i] = bestW[i];
            count = bestCount;
            closest = bestClosest;
            return false;
        }

        // Interior of the triangle (Ericson: P = a + ab·v + ac·w, v from vb, w from vc).
        float denom = 1f / sum;
        float v = vb * denom;
        float uw = vc * denom;
        closest = a + ab * v + ac * uw;
        count = 3;
        return false;
    }

    static bool Tetrahedron(Span<Vector3> w, ref int count, out Vector3 closest)
    {
        var a = w[0]; var b = w[1]; var c = w[2]; var d = w[3];

        // Degenerate (near-coplanar) tetrahedron: containment tests are meaningless.
        // Reduce via the face closest to the origin instead — GJK keeps progressing
        // with a valid 2-simplex and can rebuild a proper 3-simplex next iteration.
        var ab = b - a; var ac = c - a; var ad = d - a;
        float vol6 = Vector3.Dot(Vector3.Cross(ab, ac), ad);
        float scale = MathF.Max(MathF.Max(ab.Length(), ac.Length()), ad.Length());
        bool degenerate = MathF.Abs(vol6) <= scale * scale * scale * 1e-6f;

        if (!degenerate)
        {
            // Origin inside all four face half-spaces (sign-consistent with the
            // opposite vertex) → contained.
            bool OutsideFace(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 opposite)
            {
                var n = Vector3.Cross(p1 - p0, p2 - p0);
                float signOpp = Vector3.Dot(n, opposite - p0);
                float signOrigin = Vector3.Dot(n, -p0);
                return signOpp * signOrigin < 0f;
            }

            bool outAbc = OutsideFace(a, b, c, d);
            bool outAbd = OutsideFace(a, b, d, c);
            bool outAcd = OutsideFace(a, c, d, b);
            bool outBcd = OutsideFace(b, c, d, a);

            if (!outAbc && !outAbd && !outAcd && !outBcd)
            {
                closest = Vector3.Zero;
                return true;
            }
        }

        // Reduce via the best face (all four when degenerate — cheap and safe).
        float bestD2 = float.MaxValue;
        Vector3 bestClosest = a;
        Span<Vector3> bestW = stackalloc Vector3[3];
        int bestCount = 0;
        Span<Vector3> tmp = stackalloc Vector3[4];

        void TestFace(Vector3 p0, Vector3 p1, Vector3 p2,
                      Span<Vector3> scratch, ref float bd2, ref Vector3 bc,
                      Span<Vector3> bw, ref int bcnt)
        {
            scratch[0] = p0; scratch[1] = p1; scratch[2] = p2;
            int cnt = 3;
            Triangle(scratch, ref cnt, out var cl);
            float d2 = cl.LengthSquared();
            if (d2 < bd2)
            {
                bd2 = d2;
                bc = cl;
                for (int i = 0; i < cnt; i++) bw[i] = scratch[i];
                bcnt = cnt;
            }
        }

        TestFace(a, b, c, tmp, ref bestD2, ref bestClosest, bestW, ref bestCount);
        TestFace(a, b, d, tmp, ref bestD2, ref bestClosest, bestW, ref bestCount);
        TestFace(a, c, d, tmp, ref bestD2, ref bestClosest, bestW, ref bestCount);
        TestFace(b, c, d, tmp, ref bestD2, ref bestClosest, bestW, ref bestCount);

        for (int i = 0; i < bestCount; i++) w[i] = bestW[i];
        count = Math.Max(bestCount, 1);
        closest = bestClosest;
        return false;
    }

    static void Keep(Span<Vector3> w, ref int count, Vector3 a)
    {
        w[0] = a;
        count = 1;
    }

    static void Keep(Span<Vector3> w, ref int count, Vector3 a, Vector3 b)
    {
        w[0] = a;
        w[1] = b;
        count = 2;
    }
}
