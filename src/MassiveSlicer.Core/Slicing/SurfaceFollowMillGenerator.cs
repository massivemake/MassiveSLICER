using System;
using System.Collections.Generic;
using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Generates a multi-axis surface-following finish toolpath over a displaced surface. The mesh is
/// rastered along one or more <b>view directions</b>; for each, the ball-nose tip rides the front-most
/// surface facing that view and the <b>tool axis follows the surface normal</b> (carried on
/// <see cref="ToolpathMove.Normal"/>, which the KRL exporter turns into per-move A/B/C). For a
/// ball-nose aligned to the normal the tip coincides with the contact, so path points are exactly the
/// surface samples.
/// <para>
/// <see cref="Generate"/> is the single top-down drive (relief / textured detail).
/// <see cref="GenerateMultiAxis"/> adds side drives (top + the four walls by default) so vertical
/// walls are sampled at full density and surfaces facing outward — including undercuts that some
/// view can see — get covered, each with the spindle tilted to that face. Per-view approach/retract
/// runs along the view axis. Collision avoidance for deep undercuts is still future work.
/// </para>
/// </summary>
public static class SurfaceFollowMillGenerator
{
    private readonly record struct Hit(Vector3 Point, Vector3 Normal);

    /// <summary>Default drive directions: top plus the four side walls (outward = where the tool comes from).</summary>
    public static readonly IReadOnlyList<Vector3> DefaultViewDirs =
    [
        Vector3.UnitZ,
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
    ];

    /// <summary>Single top-down (+Z) surface-follow pass.</summary>
    public static Toolpath Generate(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<int> indices,
        MillSettings mill,
        float? sampleSpacingMm = null)
    {
        var tp = new Toolpath();
        if (positions.Count == 0 || indices.Count < 3) return tp;

        var layer = new ToolpathLayer(0, TopZ(positions)) { PlaneNormal = Vector3.UnitZ };
        // Top-down: take the topmost surface regardless of facing (-1 = no facing filter).
        layer.Moves.AddRange(RasterView(positions, normals, indices, mill, sampleSpacingMm, Vector3.UnitZ, -1f));
        if (layer.Moves.Count > 0) tp.Layers.Add(layer);
        return tp;
    }

    /// <summary>
    /// Multi-axis wrap-around: one surface-follow pass per view direction (top + walls by default),
    /// each capturing the faces pointing toward it. All passes share one layer; per-move tool axes
    /// carry the real orientation.
    /// </summary>
    public static Toolpath GenerateMultiAxis(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<int> indices,
        MillSettings mill,
        float? sampleSpacingMm = null,
        IReadOnlyList<Vector3>? viewDirs = null,
        float facingMin = 0.2f)
    {
        var tp = new Toolpath();
        if (positions.Count == 0 || indices.Count < 3) return tp;

        var layer = new ToolpathLayer(0, TopZ(positions)) { PlaneNormal = Vector3.UnitZ };

        // Coverage dedup: a face seen by several views (e.g. a ~45deg wall the top AND a side both
        // reach) must be cut once, not once per view. Track world cells (~stepover) claimed by
        // earlier views; later views skip already-covered cells. A view never blocks itself, so its
        // own passes stay continuous.
        var covered = new HashSet<(int, int, int)>();
        float cell  = MathF.Max(0.1f, mill.StepoverMm);
        (int, int, int) Cell(Vector3 w) =>
            ((int)MathF.Floor(w.X / cell), (int)MathF.Floor(w.Y / cell), (int)MathF.Floor(w.Z / cell));

        foreach (var v in viewDirs ?? DefaultViewDirs)
        {
            var basis = new ViewBasis(v);
            // Raster in the view's frame (its +Z is the approach axis), then map moves to world.
            int n = positions.Count;
            var vp = new Vector3[n];
            var vn = new Vector3[n];
            for (int i = 0; i < n; i++) { vp[i] = basis.ToView(positions[i]); vn[i] = basis.ToView(normals[i]); }

            var claimedThisView = new HashSet<(int, int, int)>();
            bool Claim(Vector3 viewPoint)
            {
                var key = Cell(basis.ToWorld(viewPoint));
                if (covered.Contains(key)) return false;   // an earlier view already cut here
                claimedThisView.Add(key);
                return true;
            }

            foreach (var m in RasterView(vp, vn, indices, mill, sampleSpacingMm, Vector3.UnitZ, facingMin, Claim))
            {
                layer.Moves.Add(m with
                {
                    From   = basis.ToWorld(m.From),
                    To     = basis.ToWorld(m.To),
                    Normal = m.Normal == Vector3.Zero ? Vector3.Zero : basis.ToWorld(m.Normal),
                });
            }
            covered.UnionWith(claimedThisView);   // hand this view's coverage to later views
        }
        if (layer.Moves.Count > 0) tp.Layers.Add(layer);
        return tp;
    }

    // Rasters the surface from +Z in the given coordinate space; returns moves in that same space.
    // facingMin < 0 disables the normal-facing filter (top-down takes the topmost surface either way).
    private static List<ToolpathMove> RasterView(
        IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3> normals, IReadOnlyList<int> indices,
        MillSettings mill, float? sampleSpacingMm, Vector3 up, float facingMin,
        Func<Vector3, bool>? claim = null)
    {
        float stepover = MathF.Max(0.1f, mill.StepoverMm);
        float sample   = MathF.Max(0.05f, sampleSpacingMm ?? stepover);

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var p in positions)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            if (p.Z > maxZ) maxZ = p.Z;
        }
        float safeZ = maxZ + MathF.Max(1f, mill.RapidZMm);

        var grid = new XyTriangleGrid(positions, normals, indices, minX, minY, maxX, maxY, stepover, facingMin);

        var moves = new List<ToolpathMove>();
        Vector3? cursor = null;
        int rows = Math.Max(1, (int)MathF.Ceiling((maxY - minY) / stepover));
        int cols = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / sample));

        for (int r = 0; r <= rows; r++)
        {
            float y = MathF.Min(minY + r * stepover, maxY);
            var rowHits = new List<Hit>();
            for (int c = 0; c <= cols; c++)
            {
                int cc = (r & 1) == 0 ? c : cols - c;
                float x = MathF.Min(minX + cc * sample, maxX);
                // A covered cell (claimed by an earlier view) breaks the run, like a miss.
                if (grid.QueryTop(x, y, out var hit) && (claim is null || claim(hit.Point)))
                    rowHits.Add(hit);
                else
                    FlushSegment(moves, rowHits, ref cursor, safeZ);
            }
            FlushSegment(moves, rowHits, ref cursor, safeZ);
        }
        if (cursor is { } last)
            moves.Add(new ToolpathMove(last, new Vector3(last.X, last.Y, safeZ), MoveKind.Travel) { IsZHop = true });
        return moves;
    }

    private static float TopZ(IReadOnlyList<Vector3> positions)
    {
        float maxZ = float.MinValue;
        foreach (var p in positions) if (p.Z > maxZ) maxZ = p.Z;
        return maxZ;
    }

    private static void FlushSegment(List<ToolpathMove> moves, List<Hit> hits, ref Vector3? cursor, float safeZ)
    {
        if (hits.Count == 0) return;

        var first = hits[0];
        if (cursor is { } cur)
        {
            var up = new Vector3(cur.X, cur.Y, safeZ);
            if (cur.Z < safeZ - 1e-3f)
                moves.Add(new ToolpathMove(cur, up, MoveKind.Travel) { IsZHop = true });
            var over = new Vector3(first.Point.X, first.Point.Y, safeZ);
            moves.Add(new ToolpathMove(up, over, MoveKind.Travel));
            cursor = over;
        }
        else
        {
            cursor = new Vector3(first.Point.X, first.Point.Y, safeZ);
        }

        moves.Add(new ToolpathMove(cursor.Value, first.Point, MoveKind.Travel) { IsZHop = true });
        var prev = first.Point;
        for (int i = 1; i < hits.Count; i++)
        {
            var h = hits[i];
            moves.Add(new ToolpathMove(prev, h.Point, MoveKind.Mill) { Normal = h.Normal });
            prev = h.Point;
        }
        cursor = prev;
        hits.Clear();
    }

    /// <summary>An orthonormal frame whose +Z is the view/approach direction.</summary>
    private readonly struct ViewBasis
    {
        public readonly Vector3 Right, Up, Fwd;
        public ViewBasis(Vector3 v)
        {
            Fwd = v.LengthSquared() > 1e-9f ? Vector3.Normalize(v) : Vector3.UnitZ;
            var a = MathF.Abs(Fwd.Z) > 0.9f ? Vector3.UnitX : Vector3.UnitZ;
            Right = Vector3.Normalize(Vector3.Cross(a, Fwd));
            Up    = Vector3.Cross(Fwd, Right);
        }
        public Vector3 ToView(Vector3 p) => new(Vector3.Dot(p, Right), Vector3.Dot(p, Up), Vector3.Dot(p, Fwd));
        public Vector3 ToWorld(Vector3 p) => p.X * Right + p.Y * Up + p.Z * Fwd;
    }

    /// <summary>Uniform XY grid bucketing triangles for fast top-surface (downward ray) queries.</summary>
    private sealed class XyTriangleGrid
    {
        private readonly IReadOnlyList<Vector3> _pos;
        private readonly IReadOnlyList<Vector3> _nrm;
        private readonly IReadOnlyList<int> _idx;
        private readonly float _minX, _minY, _cell, _facingMin;
        private readonly int _w, _h;
        private readonly List<int>[] _cells;

        public XyTriangleGrid(IReadOnlyList<Vector3> pos, IReadOnlyList<Vector3> nrm, IReadOnlyList<int> idx,
                              float minX, float minY, float maxX, float maxY, float cell, float facingMin)
        {
            _pos = pos; _nrm = nrm; _idx = idx; _minX = minX; _minY = minY; _facingMin = facingMin;
            _cell = MathF.Max(cell, 1e-3f);
            _w = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / _cell));
            _h = Math.Max(1, (int)MathF.Ceiling((maxY - minY) / _cell));
            _cells = new List<int>[_w * _h];

            int tris = idx.Count / 3;
            for (int t = 0; t < tris; t++)
            {
                Vector3 a = pos[idx[t * 3]], b = pos[idx[t * 3 + 1]], c = pos[idx[t * 3 + 2]];
                int x0 = CellX(MathF.Min(a.X, MathF.Min(b.X, c.X)));
                int x1 = CellX(MathF.Max(a.X, MathF.Max(b.X, c.X)));
                int y0 = CellY(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)));
                int y1 = CellY(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)));
                for (int gy = y0; gy <= y1; gy++)
                    for (int gx = x0; gx <= x1; gx++)
                        (_cells[gy * _w + gx] ??= []).Add(t);
            }
        }

        private int CellX(float x) => Math.Clamp((int)((x - _minX) / _cell), 0, _w - 1);
        private int CellY(float y) => Math.Clamp((int)((y - _minY) / _cell), 0, _h - 1);

        /// <summary>Highest surface point + interpolated normal above (x,y) whose normal faces the view.</summary>
        public bool QueryTop(float x, float y, out Hit hit)
        {
            hit = default;
            var bucket = _cells[CellY(y) * _w + CellX(x)];
            if (bucket is null) return false;

            bool found = false;
            float bestZ = float.MinValue;
            foreach (int t in bucket)
            {
                Vector3 a = _pos[_idx[t * 3]], b = _pos[_idx[t * 3 + 1]], c = _pos[_idx[t * 3 + 2]];
                float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (MathF.Abs(d) < 1e-9f) continue;
                float u = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
                float v = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
                float w = 1f - u - v;
                if (u < -1e-4f || v < -1e-4f || w < -1e-4f) continue;

                float z = u * a.Z + v * b.Z + w * c.Z;
                if (z <= bestZ) continue;

                Vector3 na = _nrm[_idx[t * 3]], nb = _nrm[_idx[t * 3 + 1]], nc = _nrm[_idx[t * 3 + 2]];
                Vector3 nrm = u * na + v * nb + w * nc;
                if (nrm.LengthSquared() < 1e-12f) nrm = Vector3.UnitZ;
                nrm = Vector3.Normalize(nrm);
                if (_facingMin >= 0f && nrm.Z < _facingMin) continue;   // face must point toward the view

                bestZ = z;
                hit = new Hit(new Vector3(x, y, z), nrm);
                found = true;
            }
            return found;
        }
    }
}
