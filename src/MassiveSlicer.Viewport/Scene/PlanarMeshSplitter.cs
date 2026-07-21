using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Splits a closed triangle mesh by an infinite plane into two capped halves
/// (Object A = positive normal side, Object B = negative). Used by the Cut Tool.
/// </summary>
public static class PlanarMeshSplitter
{
    public sealed record SplitResult(
        MeshData Positive,
        MeshData Negative,
        IReadOnlyList<IReadOnlyList<Vector3>> CutLoops);

    /// <param name="planePoint">A point on the cut plane (world/model space of the mesh).</param>
    /// <param name="planeNormal">Unit normal of the plane; positive side becomes Object A.</param>
    public static SplitResult Split(MeshData source, Vector3 planePoint, Vector3 planeNormal)
    {
        var n = planeNormal;
        float len = n.Length;
        if (len < 1e-8f) n = Vector3.UnitZ;
        else n /= len;

        float PlaneD(Vector3 p) => Vector3.Dot(p - planePoint, n);

        var srcPos = source.Positions;
        var srcNrm = source.Normals;
        int triCount = source.Indices is { Length: > 0 } idx
            ? idx.Length / 3
            : srcPos.Length / 3;

        var posA = new List<Vector3>();
        var nrmA = new List<Vector3>();
        var idxA = new List<uint>();
        var posB = new List<Vector3>();
        var nrmB = new List<Vector3>();
        var idxB = new List<uint>();
        var cutEdges = new List<(Vector3 A, Vector3 B)>();

        void AddTri(List<Vector3> pos, List<Vector3> nrm, List<uint> indices,
            Vector3 p0, Vector3 p1, Vector3 p2, Vector3 n0, Vector3 n1, Vector3 n2)
        {
            uint baseI = (uint)pos.Count;
            pos.Add(p0); pos.Add(p1); pos.Add(p2);
            nrm.Add(n0); nrm.Add(n1); nrm.Add(n2);
            indices.Add(baseI); indices.Add(baseI + 1); indices.Add(baseI + 2);
        }

        for (int t = 0; t < triCount; t++)
        {
            int i0, i1, i2;
            if (source.Indices is { } ind)
            {
                i0 = (int)ind[t * 3];
                i1 = (int)ind[t * 3 + 1];
                i2 = (int)ind[t * 3 + 2];
            }
            else
            {
                i0 = t * 3; i1 = t * 3 + 1; i2 = t * 3 + 2;
            }

            var v0 = srcPos[i0]; var v1 = srcPos[i1]; var v2 = srcPos[i2];
            var nn0 = srcNrm.Length > i0 ? srcNrm[i0] : n;
            var nn1 = srcNrm.Length > i1 ? srcNrm[i1] : n;
            var nn2 = srcNrm.Length > i2 ? srcNrm[i2] : n;
            float d0 = PlaneD(v0), d1 = PlaneD(v1), d2 = PlaneD(v2);

            // On-plane tolerance
            const float eps = 1e-4f;
            int s0 = d0 > eps ? 1 : d0 < -eps ? -1 : 0;
            int s1 = d1 > eps ? 1 : d1 < -eps ? -1 : 0;
            int s2 = d2 > eps ? 1 : d2 < -eps ? -1 : 0;

            if (s0 >= 0 && s1 >= 0 && s2 >= 0)
            {
                AddTri(posA, nrmA, idxA, v0, v1, v2, nn0, nn1, nn2);
                continue;
            }
            if (s0 <= 0 && s1 <= 0 && s2 <= 0)
            {
                AddTri(posB, nrmB, idxB, v0, v1, v2, nn0, nn1, nn2);
                continue;
            }

            // Mixed: clip into positive and negative fragments.
            ClipTriangle(v0, v1, v2, nn0, nn1, nn2, d0, d1, d2,
                posA, nrmA, idxA, posB, nrmB, idxB, cutEdges, n);
        }

        // Cap faces from cut edge loops.
        var loops = BuildLoops(cutEdges, n);
        foreach (var loop in loops)
        {
            if (loop.Count < 3) continue;
            CapLoop(loop, n, planePoint, posA, nrmA, idxA, positive: true);
            CapLoop(loop, n, planePoint, posB, nrmB, idxB, positive: false);
        }

        var meshA = new MeshData(posA.ToArray(), nrmA.ToArray(), idxA.ToArray(),
            source.Name + "_A", source.BaseColor, source.Metallic, source.Roughness);
        var meshB = new MeshData(posB.ToArray(), nrmB.ToArray(), idxB.ToArray(),
            source.Name + "_B", source.BaseColor, source.Metallic, source.Roughness);
        return new SplitResult(meshA, meshB, loops);
    }

    private static void ClipTriangle(
        Vector3 v0, Vector3 v1, Vector3 v2,
        Vector3 n0, Vector3 n1, Vector3 n2,
        float d0, float d1, float d2,
        List<Vector3> posA, List<Vector3> nrmA, List<uint> idxA,
        List<Vector3> posB, List<Vector3> nrmB, List<uint> idxB,
        List<(Vector3 A, Vector3 B)> cutEdges, Vector3 planeN)
    {
        // Collect polygon vertices for + and - sides with interpolated attributes.
        var posSide = new List<(Vector3 P, Vector3 N, float D)>();
        var negSide = new List<(Vector3 P, Vector3 N, float D)>();
        var edgeHits = new List<Vector3>();

        void ProcessEdge(Vector3 a, Vector3 b, Vector3 na, Vector3 nb, float da, float db)
        {
            const float eps = 1e-5f;
            bool aPos = da >= -eps, bPos = db >= -eps;
            bool aNeg = da <= eps, bNeg = db <= eps;

            if (aPos) posSide.Add((a, na, da));
            if (aNeg) negSide.Add((a, na, da));

            if ((da > eps && db < -eps) || (da < -eps && db > eps))
            {
                float t = da / (da - db);
                t = Math.Clamp(t, 0f, 1f);
                var p = a + (b - a) * t;
                var nn = Vector3.Normalize(na + (nb - na) * t);
                if (nn.LengthSquared < 1e-8f) nn = planeN;
                posSide.Add((p, nn, 0f));
                negSide.Add((p, nn, 0f));
                edgeHits.Add(p);
            }
        }

        ProcessEdge(v0, v1, n0, n1, d0, d1);
        ProcessEdge(v1, v2, n1, n2, d1, d2);
        ProcessEdge(v2, v0, n2, n0, d2, d0);

        // Dedupe consecutive near-equal points and triangulate fans.
        Fan(posSide, posA, nrmA, idxA);
        Fan(negSide, posB, nrmB, idxB);

        if (edgeHits.Count >= 2)
            cutEdges.Add((edgeHits[0], edgeHits[^1]));
    }

    private static void Fan(
        List<(Vector3 P, Vector3 N, float D)> poly,
        List<Vector3> pos, List<Vector3> nrm, List<uint> indices)
    {
        // Remove consecutive duplicates.
        var clean = new List<(Vector3 P, Vector3 N, float D)>();
        foreach (var v in poly)
        {
            if (clean.Count == 0 || (clean[^1].P - v.P).LengthSquared > 1e-10f)
                clean.Add(v);
        }
        if (clean.Count >= 3 && (clean[0].P - clean[^1].P).LengthSquared < 1e-10f)
            clean.RemoveAt(clean.Count - 1);
        if (clean.Count < 3) return;

        for (int i = 1; i + 1 < clean.Count; i++)
        {
            uint b = (uint)pos.Count;
            pos.Add(clean[0].P); pos.Add(clean[i].P); pos.Add(clean[i + 1].P);
            nrm.Add(clean[0].N); nrm.Add(clean[i].N); nrm.Add(clean[i + 1].N);
            indices.Add(b); indices.Add(b + 1); indices.Add(b + 2);
        }
    }

    private static List<List<Vector3>> BuildLoops(List<(Vector3 A, Vector3 B)> edges, Vector3 planeN)
    {
        // Greedy chain of edges into closed loops.
        var remaining = new List<(Vector3 A, Vector3 B)>(edges);
        var loops = new List<List<Vector3>>();
        const float tol = 1e-3f;
        const float tol2 = tol * tol;

        while (remaining.Count > 0)
        {
            var (a, b) = remaining[^1];
            remaining.RemoveAt(remaining.Count - 1);
            var loop = new List<Vector3> { a, b };
            bool closed = false;
            bool progressed = true;
            while (progressed && !closed)
            {
                progressed = false;
                var tip = loop[^1];
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    var e = remaining[i];
                    if ((e.A - tip).LengthSquared <= tol2)
                    {
                        loop.Add(e.B);
                        remaining.RemoveAt(i);
                        progressed = true;
                    }
                    else if ((e.B - tip).LengthSquared <= tol2)
                    {
                        loop.Add(e.A);
                        remaining.RemoveAt(i);
                        progressed = true;
                    }
                    else continue;

                    if ((loop[^1] - loop[0]).LengthSquared <= tol2)
                    {
                        loop.RemoveAt(loop.Count - 1);
                        closed = true;
                    }
                    break;
                }
            }
            // Only a chain that actually closed back on itself is a real cross-section
            // boundary worth capping. A chain that dead-ends (never reconnects to its own
            // start) means the cut plane crossed the source mesh's own pre-existing open
            // edge — there is no enclosed cross-section there, so forcing a cap by fanning
            // across the gap between the two loose ends produces a bogus, twisted triangle
            // bridging empty space instead of a clean flat face. Leave it uncapped instead.
            if (closed && loop.Count >= 3)
            {
                // Order CCW relative to plane normal for consistent caps.
                if (Vector3.Dot(LoopAreaNormal(loop), planeN) < 0)
                    loop.Reverse();
                loops.Add(loop);
            }
        }
        return loops;
    }

    private static Vector3 LoopAreaNormal(List<Vector3> loop)
    {
        var acc = Vector3.Zero;
        for (int i = 0; i < loop.Count; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % loop.Count];
            acc += Vector3.Cross(p, q);
        }
        return acc;
    }

    private static void CapLoop(
        List<Vector3> loop, Vector3 planeN, Vector3 planePoint,
        List<Vector3> pos, List<Vector3> nrm, List<uint> indices, bool positive)
    {
        var faceN = positive ? -planeN : planeN; // outward from solid half
        // Project slightly off the plane into the solid so caps don't z-fight.
        float bias = 1e-3f;
        var offset = faceN * bias;

        var c = Vector3.Zero;
        foreach (var p in loop) c += p;
        c /= loop.Count;
        c += offset;

        for (int i = 0; i < loop.Count; i++)
        {
            var p0 = loop[i] + offset;
            var p1 = loop[(i + 1) % loop.Count] + offset;
            // Winding: for positive half, faceN = -planeN so loop CCW about planeN
            // appears CW about faceN — flip edge order for positive.
            uint b = (uint)pos.Count;
            if (positive)
            {
                pos.Add(c); pos.Add(p1); pos.Add(p0);
            }
            else
            {
                pos.Add(c); pos.Add(p0); pos.Add(p1);
            }
            nrm.Add(faceN); nrm.Add(faceN); nrm.Add(faceN);
            indices.Add(b); indices.Add(b + 1); indices.Add(b + 2);
        }
    }
}
