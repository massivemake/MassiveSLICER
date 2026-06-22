using System;
using System.Collections.Generic;
using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Turns a low-poly UV-mapped mesh + a UV-space <see cref="HeightField2D"/> into a dense
/// <b>displaced surface</b>: each base point is pushed along its (interpolated) normal by
/// <c>distance * (height(uv) - bias)</c>. This recovers the fine detail that lives in the
/// model's displacement / bump / height map (or a height field integrated from its normal map)
/// as real geometry — the surface a milling toolpath then rides.
/// <para>
/// Subdivision is adaptive: a triangle is tessellated finely enough that each sub-edge spans
/// roughly one map texel (capped by <c>maxSubdiv</c>), so detail tracks the map resolution
/// rather than the coarse mesh. Output normals are recomputed from the displaced geometry.
/// </para>
/// </summary>
public static class DisplacedSurfaceBuilder
{
    public readonly record struct Result(
        Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, int[] Indices)
    {
        public int TriangleCount => Indices.Length / 3;
        public int VertexCount => Positions.Length;
    }

    /// <param name="bias">Map value treated as "no displacement" (0 pushes 0..distance outward; 0.5 is signed +/-).</param>
    /// <param name="maxSubdiv">Cap on sub-edges per triangle edge (guards against runaway tessellation).</param>
    /// <param name="texelsPerSample">Target map texels per generated sample edge (higher = coarser).</param>
    public static Result Build(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<Vector2> uvs,
        IReadOnlyList<int> indices,
        HeightField2D height,
        float displacementDistance,
        float bias = 0f,
        int maxSubdiv = 24,
        float texelsPerSample = 2f)
    {
        if (positions.Count == 0 || indices.Count < 3)
            return new Result([], [], [], []);
        if (normals.Count != positions.Count)
            throw new ArgumentException("normals must match positions length.");
        if (uvs.Count != positions.Count)
            throw new ArgumentException("displaced surface needs per-vertex UVs.");

        var outPos = new List<Vector3>(positions.Count * 4);
        var outUv = new List<Vector2>(positions.Count * 4);
        var outIdx = new List<int>(indices.Count * 4);

        int triCount = indices.Count / 3;
        for (int t = 0; t < triCount; t++)
        {
            int i0 = indices[t * 3], i1 = indices[t * 3 + 1], i2 = indices[t * 3 + 2];
            Vector3 p0 = positions[i0], p1 = positions[i1], p2 = positions[i2];
            Vector3 n0 = Norm(normals[i0]), n1 = Norm(normals[i1]), n2 = Norm(normals[i2]);
            Vector2 t0 = uvs[i0], t1 = uvs[i1], t2 = uvs[i2];

            int n = AdaptiveSubdiv(t0, t1, t2, height.Width, height.Height, texelsPerSample, maxSubdiv);

            int baseIndex = outPos.Count;

            // Barycentric lattice: rows j=0..n, columns i=0..(n-j). bary = ((n-i-j)/n, i/n, j/n).
            for (int j = 0; j <= n; j++)
            {
                for (int i = 0; i <= n - j; i++)
                {
                    float b1 = (float)i / n;
                    float b2 = (float)j / n;
                    float b0 = 1f - b1 - b2;

                    Vector3 p = b0 * p0 + b1 * p1 + b2 * p2;
                    Vector3 nrm = Norm(b0 * n0 + b1 * n1 + b2 * n2);
                    Vector2 uv = b0 * t0 + b1 * t1 + b2 * t2;

                    float h = height.Sample(uv.X, uv.Y) - bias;
                    p += nrm * (displacementDistance * h);

                    outPos.Add(p);
                    outUv.Add(uv);
                }
            }

            // Triangulate the lattice. Row j has (n-j+1) verts; offset(j) = sum of prior row widths.
            int RowStart(int j) => baseIndex + j * (n + 1) - (j * (j - 1)) / 2;
            for (int j = 0; j < n; j++)
            {
                int rowA = RowStart(j);
                int rowB = RowStart(j + 1);
                int widthA = n - j + 1;
                for (int i = 0; i < widthA - 1; i++)
                {
                    int a = rowA + i;
                    int b = rowA + i + 1;
                    int c = rowB + i;
                    outIdx.Add(a); outIdx.Add(b); outIdx.Add(c);   // up-triangle
                    if (i < widthA - 2)
                    {
                        int d = rowB + i + 1;
                        outIdx.Add(b); outIdx.Add(d); outIdx.Add(c); // down-triangle
                    }
                }
            }
        }

        var posArr = outPos.ToArray();
        var idxArr = outIdx.ToArray();
        var nrmArr = RecomputeNormals(posArr, idxArr);
        return new Result(posArr, nrmArr, outUv.ToArray(), idxArr);
    }

    private static int AdaptiveSubdiv(Vector2 a, Vector2 b, Vector2 c, int w, int h, float texelsPerSample, int max)
    {
        // Longest UV edge measured in texels -> samples so each sub-edge ~ texelsPerSample texels.
        float Tex(Vector2 p, Vector2 q)
        {
            float du = (p.X - q.X) * w;
            float dv = (p.Y - q.Y) * h;
            return MathF.Sqrt(du * du + dv * dv);
        }
        float maxTexels = MathF.Max(Tex(a, b), MathF.Max(Tex(b, c), Tex(c, a)));
        int n = (int)MathF.Ceiling(maxTexels / MathF.Max(0.5f, texelsPerSample));
        return Math.Clamp(n, 1, max);
    }

    private static Vector3[] RecomputeNormals(Vector3[] pos, int[] idx)
    {
        var nrm = new Vector3[pos.Length];
        for (int t = 0; t < idx.Length; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            Vector3 face = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]); // area-weighted
            nrm[a] += face; nrm[b] += face; nrm[c] += face;
        }
        for (int i = 0; i < nrm.Length; i++)
            nrm[i] = nrm[i].LengthSquared() > 1e-20f ? Vector3.Normalize(nrm[i]) : Vector3.UnitZ;
        return nrm;
    }

    private static Vector3 Norm(Vector3 v) =>
        v.LengthSquared() > 1e-20f ? Vector3.Normalize(v) : Vector3.UnitZ;
}
