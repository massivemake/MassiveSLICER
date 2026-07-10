using System.Numerics;

namespace MassiveSlicer.Core.Slicing.Lightning;

/// <summary>
/// Point-in-solid oracle over raw triangle soups: casts a vertical ray and counts
/// surface crossings above the point (odd = inside). Crossings closer than a
/// cluster tolerance collapse into one, so double-shelled CAD exports (every
/// surface duplicated microns apart) read as a single surface instead of
/// inverting the parity. Triangles are bucketed on an XY grid for locality.
///
/// Used by the Formbound planner to reject demand from parity phantoms: a grazing
/// cut over a pocket rim emits the rim curve without its host wall, which 2D
/// contour parity can only read as solid — the mesh knows it is void.
/// </summary>
internal sealed class MeshInsideTester
{
    private readonly List<int>[] _cells;
    private readonly List<(Vector3 A, Vector3 B, Vector3 C)> _tris = [];
    private readonly float _minX, _minY, _cellSize;
    private readonly int _nx, _ny;

    /// <summary>Crossings closer than this along the ray merge into one surface.</summary>
    private const float ClusterTol = 0.2f;

    public MeshInsideTester(IEnumerable<Vector3[]> meshes)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var verts in meshes)
            for (int i = 0; i + 2 < verts.Length; i += 3)
            {
                _tris.Add((verts[i], verts[i + 1], verts[i + 2]));
                for (int v = 0; v < 3; v++)
                {
                    var p = verts[i + v];
                    if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                }
            }

        if (_tris.Count == 0)
        {
            _minX = _minY = 0; _cellSize = 1; _nx = _ny = 1;
            _cells = [[]];
            return;
        }

        float spanX = MathF.Max(maxX - minX, 1f);
        float spanY = MathF.Max(maxY - minY, 1f);
        _cellSize = MathF.Max(MathF.Max(spanX, spanY) / 192f, 1f);
        _minX = minX; _minY = minY;
        _nx = Math.Clamp((int)(spanX / _cellSize) + 1, 1, 512);
        _ny = Math.Clamp((int)(spanY / _cellSize) + 1, 1, 512);
        _cells = new List<int>[_nx * _ny];
        for (int i = 0; i < _cells.Length; i++) _cells[i] = [];

        for (int t = 0; t < _tris.Count; t++)
        {
            var (a, b, c) = _tris[t];
            float x0 = MathF.Min(a.X, MathF.Min(b.X, c.X)), x1 = MathF.Max(a.X, MathF.Max(b.X, c.X));
            float y0 = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), y1 = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
            int cx0 = Cx(x0), cx1 = Cx(x1), cy0 = Cy(y0), cy1 = Cy(y1);
            for (int cy = cy0; cy <= cy1; cy++)
                for (int cx = cx0; cx <= cx1; cx++)
                    _cells[cy * _nx + cx].Add(t);
        }
    }

    private int Cx(float x) => Math.Clamp((int)((x - _minX) / _cellSize), 0, _nx - 1);
    private int Cy(float y) => Math.Clamp((int)((y - _minY) / _cellSize), 0, _ny - 1);

    public bool IsInside(Vector3 p)
    {
        // Deterministic sub-cluster jitter: keeps the ray off shared triangle
        // edges/vertices, where a pass-through would double-count.
        float px = p.X + 0.0137f, py = p.Y + 0.0071f;

        var crossings = new List<float>();
        foreach (int t in _cells[Cy(py) * _nx + Cx(px)])
        {
            var (a, b, c) = _tris[t];
            float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (MathF.Abs(d) < 1e-9f) continue;   // vertical triangle — measure zero
            float w0 = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / d;
            float w1 = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / d;
            float w2 = 1f - w0 - w1;
            if (w0 < -1e-6f || w1 < -1e-6f || w2 < -1e-6f) continue;
            float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
            if (z > p.Z + 1e-3f) crossings.Add(z);
        }
        if (crossings.Count == 0) return false;

        crossings.Sort();
        int clusters = 1;
        for (int i = 1; i < crossings.Count; i++)
            if (crossings[i] - crossings[i - 1] > ClusterTol)
                clusters++;
        return (clusters & 1) == 1;
    }
}
