using System;
using System.Collections.Generic;
using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Generates a multi-axis surface-following finish toolpath over a displaced surface. The mesh
/// is rastered top-down at the tool stepover; at each contact the ball-nose tip rides the surface
/// point and the <b>tool axis follows the surface normal</b> (carried on <see cref="ToolpathMove.Normal"/>,
/// which the KRL exporter turns into per-move A/B/C). For a ball-nose aligned to the normal the tip
/// coincides with the contact point, so the path points are exactly the surface samples.
/// <para>
/// v1 drive is a single top-down raster (handles relief / textured detail and tilts the spindle to
/// stay normal to the surface). Full wrap-around of vertical walls and undercuts needs additional
/// drive directions and is a later step.
/// </para>
/// </summary>
public static class SurfaceFollowMillGenerator
{
    private readonly record struct Hit(Vector3 Point, Vector3 Normal);

    public static Toolpath Generate(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<int> indices,
        MillSettings mill,
        float? sampleSpacingMm = null)
    {
        var tp = new Toolpath();
        if (positions.Count == 0 || indices.Count < 3) return tp;

        float stepover = MathF.Max(0.1f, mill.StepoverMm);
        float sample   = MathF.Max(0.05f, sampleSpacingMm ?? stepover);

        // World XY bounds + top Z.
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var p in positions)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            if (p.Z > maxZ) maxZ = p.Z;
        }
        float safeZ = maxZ + MathF.Max(1f, mill.RapidZMm);

        var grid = new XyTriangleGrid(positions, normals, indices, minX, minY, maxX, maxY, stepover);

        var layer = new ToolpathLayer(0, maxZ) { PlaneNormal = Vector3.UnitZ };
        Vector3? cursor = null;
        int rows = Math.Max(1, (int)MathF.Ceiling((maxY - minY) / stepover));
        int cols = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / sample));

        for (int r = 0; r <= rows; r++)
        {
            float y = minY + r * stepover;
            if (y > maxY) y = maxY;

            // Boustrophedon: reverse X each row.
            var rowHits = new List<Hit>();
            for (int c = 0; c <= cols; c++)
            {
                int cc = (r & 1) == 0 ? c : cols - c;
                float x = minX + cc * sample;
                if (x > maxX) x = maxX;
                if (grid.QueryTop(x, y, out var hit)) rowHits.Add(hit);
                else FlushSegment(layer, rowHits, ref cursor, safeZ);
            }
            FlushSegment(layer, rowHits, ref cursor, safeZ);
        }

        // Final retract.
        if (cursor is { } last)
            layer.Moves.Add(new ToolpathMove(last, new Vector3(last.X, last.Y, safeZ), MoveKind.Travel) { IsZHop = true });

        if (layer.Moves.Count > 0) tp.Layers.Add(layer);
        return tp;
    }

    // Emits one continuous contact run as approach + cuts; clears the buffer.
    private static void FlushSegment(ToolpathLayer layer, List<Hit> hits, ref Vector3? cursor, float safeZ)
    {
        if (hits.Count == 0) return;

        var first = hits[0];
        // Retract the previous run to safe height, then rapid across.
        if (cursor is { } cur)
        {
            var up = new Vector3(cur.X, cur.Y, safeZ);
            if (cur.Z < safeZ - 1e-3f)
                layer.Moves.Add(new ToolpathMove(cur, up, MoveKind.Travel) { IsZHop = true });
            var over = new Vector3(first.Point.X, first.Point.Y, safeZ);
            layer.Moves.Add(new ToolpathMove(up, over, MoveKind.Travel));
            cursor = over;
        }
        else
        {
            cursor = new Vector3(first.Point.X, first.Point.Y, safeZ);
        }

        // Descend onto the first contact, then cut along the run.
        layer.Moves.Add(new ToolpathMove(cursor.Value, first.Point, MoveKind.Travel) { IsZHop = true });
        var prev = first.Point;
        for (int i = 1; i < hits.Count; i++)
        {
            var h = hits[i];
            layer.Moves.Add(new ToolpathMove(prev, h.Point, MoveKind.Mill) { Normal = h.Normal });
            prev = h.Point;
        }
        cursor = prev;
        hits.Clear();
    }

    /// <summary>Uniform XY grid bucketing triangles for fast top-surface (downward ray) queries.</summary>
    private sealed class XyTriangleGrid
    {
        private readonly IReadOnlyList<Vector3> _pos;
        private readonly IReadOnlyList<Vector3> _nrm;
        private readonly IReadOnlyList<int> _idx;
        private readonly float _minX, _minY, _cell;
        private readonly int _w, _h;
        private readonly List<int>[] _cells;

        public XyTriangleGrid(IReadOnlyList<Vector3> pos, IReadOnlyList<Vector3> nrm, IReadOnlyList<int> idx,
                              float minX, float minY, float maxX, float maxY, float cell)
        {
            _pos = pos; _nrm = nrm; _idx = idx; _minX = minX; _minY = minY;
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

        /// <summary>Highest surface point + interpolated normal directly above/below (x,y), if any.</summary>
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
                // 2D barycentric of (x,y) in the triangle's XY projection.
                float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (MathF.Abs(d) < 1e-9f) continue;
                float u = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
                float v = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
                float w = 1f - u - v;
                if (u < -1e-4f || v < -1e-4f || w < -1e-4f) continue;

                float z = u * a.Z + v * b.Z + w * c.Z;
                if (z <= bestZ) continue;
                bestZ = z;
                Vector3 na = _nrm[_idx[t * 3]], nb = _nrm[_idx[t * 3 + 1]], nc = _nrm[_idx[t * 3 + 2]];
                Vector3 n = u * na + v * nb + w * nc;
                if (n.LengthSquared() < 1e-12f) n = Vector3.UnitZ;
                hit = new Hit(new Vector3(x, y, z), Vector3.Normalize(n));
                found = true;
            }
            return found;
        }
    }
}
