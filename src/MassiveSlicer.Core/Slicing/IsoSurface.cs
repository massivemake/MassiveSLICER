using System.Numerics;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Iso-surface of a scalar grid as a triangle soup. Each cell is split into six
/// tetrahedra around the same space diagonal, so shared faces meet.
/// Triangle normals point toward the higher field value.
/// </summary>
public static class IsoSurface
{
    /// <summary>
    /// Cube corners: 0 (0,0,0), 1 (1,0,0), 2 (1,1,0), 3 (0,1,0),
    /// 4 (0,0,1), 5 (1,0,1), 6 (1,1,1), 7 (0,1,1).
    /// </summary>
    static readonly int[] Tets =
    [
        0, 1, 2, 6,
        0, 2, 3, 6,
        0, 3, 7, 6,
        0, 7, 4, 6,
        0, 4, 5, 6,
        0, 5, 1, 6,
    ];

    public static Vector3[] Extract(
        float[] field, int nx, int ny, int nz, Vector3 origin, Vector3 spacing, float iso)
    {
        if (nx < 2 || ny < 2 || nz < 2 || field.Length < nx * ny * nz)
            return [];

        var dst = new List<Vector3>();
        Span<Vector3> corner = stackalloc Vector3[8];
        Span<float> value = stackalloc float[8];
        Span<Vector3> tv = stackalloc Vector3[4];
        Span<float> tf = stackalloc float[4];

        for (int k = 0; k < nz - 1; k++)
        for (int j = 0; j < ny - 1; j++)
        for (int i = 0; i < nx - 1; i++)
        {
            LoadCell(field, nx, ny, origin, spacing, i, j, k, corner, value);
            for (int t = 0; t < 6; t++)
            {
                int o = t * 4;
                for (int c = 0; c < 4; c++)
                {
                    int id = Tets[o + c];
                    tv[c] = corner[id];
                    tf[c] = value[id];
                }
                EmitTet(dst, tv, tf, iso);
            }
        }

        return dst.ToArray();
    }

    static void LoadCell(
        float[] field, int nx, int ny, Vector3 origin, Vector3 spacing,
        int i, int j, int k, Span<Vector3> corner, Span<float> value)
    {
        Read(field, nx, ny, origin, spacing, i,     j,     k,     0, corner, value);
        Read(field, nx, ny, origin, spacing, i + 1, j,     k,     1, corner, value);
        Read(field, nx, ny, origin, spacing, i + 1, j + 1, k,     2, corner, value);
        Read(field, nx, ny, origin, spacing, i,     j + 1, k,     3, corner, value);
        Read(field, nx, ny, origin, spacing, i,     j,     k + 1, 4, corner, value);
        Read(field, nx, ny, origin, spacing, i + 1, j,     k + 1, 5, corner, value);
        Read(field, nx, ny, origin, spacing, i + 1, j + 1, k + 1, 6, corner, value);
        Read(field, nx, ny, origin, spacing, i,     j + 1, k + 1, 7, corner, value);
    }

    static void Read(
        float[] field, int nx, int ny, Vector3 origin, Vector3 spacing,
        int i, int j, int k, int slot, Span<Vector3> corner, Span<float> value)
    {
        corner[slot] = origin + new Vector3(i * spacing.X, j * spacing.Y, k * spacing.Z);
        value[slot] = field[i + nx * (j + ny * k)];
    }

    static void EmitTet(List<Vector3> dst, Span<Vector3> v, Span<float> f, float iso)
    {
        Span<int> hot = stackalloc int[4];
        Span<int> cold = stackalloc int[4];
        int nh = 0, nc = 0;
        for (int i = 0; i < 4; i++)
        {
            if (float.IsNaN(f[i])) return;
            if (f[i] >= iso) hot[nh++] = i;
            else cold[nc++] = i;
        }

        if (nh == 0 || nh == 4) return;

        if (nh == 1)
        {
            int h = hot[0];
            var p0 = Cross(v[h], v[cold[0]], f[h], f[cold[0]], iso);
            var p1 = Cross(v[h], v[cold[1]], f[h], f[cold[1]], iso);
            var p2 = Cross(v[h], v[cold[2]], f[h], f[cold[2]], iso);
            Add(dst, p0, p1, p2, v[h] - (p0 + p1 + p2) / 3f);
            return;
        }

        if (nh == 3)
        {
            int c = cold[0];
            var p0 = Cross(v[c], v[hot[0]], f[c], f[hot[0]], iso);
            var p1 = Cross(v[c], v[hot[1]], f[c], f[hot[1]], iso);
            var p2 = Cross(v[c], v[hot[2]], f[c], f[hot[2]], iso);
            Add(dst, p0, p1, p2, (p0 + p1 + p2) / 3f - v[c]);
            return;
        }

        int h0 = hot[0], h1 = hot[1], c0 = cold[0], c1 = cold[1];
        var a = Cross(v[h0], v[c0], f[h0], f[c0], iso);
        var b = Cross(v[h0], v[c1], f[h0], f[c1], iso);
        var c2 = Cross(v[h1], v[c0], f[h1], f[c0], iso);
        var d = Cross(v[h1], v[c1], f[h1], f[c1], iso);
        var toward = (v[h0] + v[h1]) - (v[c0] + v[c1]);
        Add(dst, a, b, d, toward);
        Add(dst, a, d, c2, toward);
    }

    static Vector3 Cross(Vector3 p, Vector3 q, float vp, float vq, float iso)
    {
        float den = vq - vp;
        float t = MathF.Abs(den) < 1e-12f ? 0.5f : (iso - vp) / den;
        t = Math.Clamp(t, 0f, 1f);
        return p + t * (q - p);
    }

    static void Add(List<Vector3> dst, Vector3 a, Vector3 b, Vector3 c, Vector3 towardHigher)
    {
        var n = Vector3.Cross(b - a, c - a);
        if (n.LengthSquared() < 1e-16f) return;
        if (Vector3.Dot(n, towardHigher) < 0f)
            (b, c) = (c, b);
        dst.Add(a);
        dst.Add(b);
        dst.Add(c);
    }
}
