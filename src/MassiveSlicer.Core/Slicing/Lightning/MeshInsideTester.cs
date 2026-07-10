using System.Numerics;

namespace MassiveSlicer.Core.Slicing.Lightning;

/// <summary>
/// Point-in-solid oracle over raw triangle soups, built for REAL production
/// meshes: double-shelled exports (every surface duplicated microns apart) and
/// open shells (print pieces trimmed from a larger hull, cut faces left open).
///
/// A single ray's crossing parity is wrong whenever the ray escapes through an
/// open cut, so six axis rays (±X, ±Y, ±Z) each cast a parity vote and the
/// majority decides — an opening corrupts only the directions that see it.
/// Crossings closer than a cluster tolerance collapse into one so twin shells
/// read as a single surface. Triangles are bucketed on three axis-aligned grids
/// for locality.
///
/// Used by the Formbound planner/generator to reject parity phantoms: a grazing
/// cut over a pocket rim emits the rim curve without its host wall, which 2D
/// contour parity can only read as solid — the mesh knows it is void (all six
/// votes read even).
/// </summary>
internal sealed class MeshInsideTester
{
    private readonly List<(Vector3 A, Vector3 B, Vector3 C)> _tris = [];

    // One grid per ray axis: cells over the two OTHER axes.
    private readonly List<int>[][] _cells = new List<int>[3][];
    private readonly Vector3 _min;
    private readonly float _cellSize;
    private readonly int[] _n = new int[3];   // cell counts per grid axis pair

    /// <summary>Crossings closer than this along a ray merge into one surface.</summary>
    private const float ClusterTol = 0.2f;

    public MeshInsideTester(IEnumerable<Vector3[]> meshes)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var verts in meshes)
            for (int i = 0; i + 2 < verts.Length; i += 3)
            {
                _tris.Add((verts[i], verts[i + 1], verts[i + 2]));
                for (int v = 0; v < 3; v++)
                {
                    min = Vector3.Min(min, verts[i + v]);
                    max = Vector3.Max(max, verts[i + v]);
                }
            }

        if (_tris.Count == 0)
        {
            _min = Vector3.Zero; _cellSize = 1f;
            for (int a = 0; a < 3; a++) { _n[a] = 1; _cells[a] = [[]]; }
            return;
        }

        var span = Vector3.Max(max - min, new Vector3(1f));
        _cellSize = MathF.Max(MathF.Max(span.X, MathF.Max(span.Y, span.Z)) / 160f, 1f);
        _min = min;

        // axis a casts along a; its grid spans axes u=(a+1)%3, v=(a+2)%3.
        for (int a = 0; a < 3; a++)
        {
            int u = (a + 1) % 3, v = (a + 2) % 3;
            int nu = Math.Clamp((int)(Axis(span, u) / _cellSize) + 1, 1, 384);
            int nv = Math.Clamp((int)(Axis(span, v) / _cellSize) + 1, 1, 384);
            _n[a] = nu;
            var cells = new List<int>[nu * nv];
            for (int i = 0; i < cells.Length; i++) cells[i] = [];
            _cells[a] = cells;

            for (int t = 0; t < _tris.Count; t++)
            {
                var (p0, p1, p2) = _tris[t];
                float u0 = MathF.Min(Axis(p0, u), MathF.Min(Axis(p1, u), Axis(p2, u)));
                float u1 = MathF.Max(Axis(p0, u), MathF.Max(Axis(p1, u), Axis(p2, u)));
                float v0 = MathF.Min(Axis(p0, v), MathF.Min(Axis(p1, v), Axis(p2, v)));
                float v1 = MathF.Max(Axis(p0, v), MathF.Max(Axis(p1, v), Axis(p2, v)));
                int cu0 = Cell(u0, u, nu), cu1 = Cell(u1, u, nu);
                int cv0 = Cell(v0, v, nv), cv1 = Cell(v1, v, nv);
                for (int cv = cv0; cv <= cv1; cv++)
                    for (int cu = cu0; cu <= cu1; cu++)
                        cells[cv * nu + cu].Add(t);
            }
        }
    }

    private static float Axis(Vector3 p, int a) => a == 0 ? p.X : a == 1 ? p.Y : p.Z;

    private int Cell(float x, int axis, int n) =>
        Math.Clamp((int)((x - Axis(_min, axis)) / _cellSize), 0, n - 1);

    public bool IsInside(Vector3 p)
    {
        int solidVotes = 0, votes = 0;
        Span<float> jit = [0.0137f, 0.0071f, 0.0093f];   // keep rays off shared edges

        for (int a = 0; a < 3; a++)
        {
            int u = (a + 1) % 3, v = (a + 2) % 3;
            float pu = Axis(p, u) + jit[u];
            float pv = Axis(p, v) + jit[v];
            float pa = Axis(p, a);

            int nu = _n[a];
            int nv = _cells[a].Length / nu;
            var cell = _cells[a][Cell(pv, v, nv) * nu + Cell(pu, u, nu)];

            var above = new List<float>();
            var below = new List<float>();
            foreach (int t in cell)
            {
                var (t0, t1, t2) = _tris[t];
                float a0u = Axis(t0, u), a0v = Axis(t0, v), a0a = Axis(t0, a);
                float a1u = Axis(t1, u), a1v = Axis(t1, v), a1a = Axis(t1, a);
                float a2u = Axis(t2, u), a2v = Axis(t2, v), a2a = Axis(t2, a);
                float d = (a1v - a2v) * (a0u - a2u) + (a2u - a1u) * (a0v - a2v);
                if (MathF.Abs(d) < 1e-9f) continue;   // parallel to the ray
                float w0 = ((a1v - a2v) * (pu - a2u) + (a2u - a1u) * (pv - a2v)) / d;
                float w1 = ((a2v - a0v) * (pu - a2u) + (a0u - a2u) * (pv - a2v)) / d;
                float w2 = 1f - w0 - w1;
                if (w0 < -1e-6f || w1 < -1e-6f || w2 < -1e-6f) continue;
                float ca = w0 * a0a + w1 * a1a + w2 * a2a;
                if (ca > pa + 1e-3f) above.Add(ca);
                else if (ca < pa - 1e-3f) below.Add(ca);
            }

            foreach (var list in new[] { above, below })
            {
                votes++;
                if (list.Count == 0) continue;   // vote: outside
                list.Sort();
                int clusters = 1;
                for (int i = 1; i < list.Count; i++)
                    if (list[i] - list[i - 1] > ClusterTol)
                        clusters++;
                if ((clusters & 1) == 1) solidVotes++;
            }
        }

        return solidVotes * 2 > votes;   // strict majority of the six rays
    }
}
