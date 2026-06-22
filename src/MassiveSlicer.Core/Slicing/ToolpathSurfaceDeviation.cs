using System;
using System.Collections.Generic;
using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Measures how faithfully a ball-nose surface-follow toolpath reproduces the ideal displaced
/// surface. Each cut move contacts the surface with a ball of radius r whose centre sits at
/// <c>contact + r * toolAxis</c>; the tool removes everything inside any such ball. For every
/// sampled point on the ideal surface we take the signed distance to the nearest ball surface:
/// negative = the point lies inside a ball (the tool cut past it -> <b>gouge</b>/over-cut),
/// positive beyond tolerance = no ball reached it (material left proud -> <b>residual</b>/under-cut).
/// Reports the fraction of the surface in each category — the user-set displacement distance and
/// stepover drive these numbers (tighter curvature or coarser stepover -> more gouge/residual).
/// </summary>
public static class ToolpathSurfaceDeviation
{
    public readonly record struct Report(
        int Samples, float GougePct, float ResidualPct, float OkPct,
        float MaxGougeMm, float MaxResidualMm, float ToleranceMm)
    {
        public float FailPct => GougePct + ResidualPct;
    }

    public static Report Analyze(
        IReadOnlyList<Vector3> surfacePoints,
        Toolpath toolpath,
        float toolRadiusMm,
        float toleranceMm)
    {
        float r = MathF.Max(0f, toolRadiusMm);
        float tol = MathF.Max(1e-4f, toleranceMm);

        // Ball centres from every cut move's contact + tool axis.
        var centers = new List<Vector3>();
        foreach (var layer in toolpath.Layers)
            foreach (var m in layer.Moves)
                if (m.Kind == MoveKind.Mill)
                {
                    var axis = m.Normal.LengthSquared() > 1e-9f ? Vector3.Normalize(m.Normal) : layer.PlaneNormal;
                    centers.Add(m.To + axis * r);
                }

        if (centers.Count == 0 || surfacePoints.Count == 0)
            return new Report(0, 0, 0, 0, 0, 0, tol);

        var grid = new XyPointGrid(centers, MathF.Max(r, 1f));
        float searchR = 2f * r + tol + grid.Cell;   // spheres that can reach a sample

        int gouge = 0, residual = 0, ok = 0;
        float maxGouge = 0f, maxResidual = 0f;

        foreach (var q in surfacePoints)
        {
            float best = float.MaxValue;   // signed distance to nearest ball surface
            foreach (int ci in grid.Near(q, searchR))
            {
                float d = Vector3.Distance(q, centers[ci]) - r;
                if (d < best) best = d;
            }
            if (best == float.MaxValue) { residual++; continue; }   // no ball anywhere near -> uncut

            if (best < -tol) { gouge++; if (-best > maxGouge) maxGouge = -best; }
            else if (best > tol) { residual++; if (best > maxResidual) maxResidual = best; }
            else ok++;
        }

        int n = surfacePoints.Count;
        return new Report(n,
            100f * gouge / n, 100f * residual / n, 100f * ok / n,
            maxGouge, maxResidual, tol);
    }

    /// <summary>Uniform XY grid of points for radius queries.</summary>
    private sealed class XyPointGrid
    {
        private readonly List<Vector3> _pts;
        private readonly float _minX, _minY;
        public readonly float Cell;
        private readonly int _w, _h;
        private readonly List<int>[] _cells;

        public XyPointGrid(List<Vector3> pts, float cell)
        {
            _pts = pts;
            Cell = MathF.Max(cell, 1e-3f);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            }
            _minX = minX; _minY = minY;
            _w = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / Cell) + 1);
            _h = Math.Max(1, (int)MathF.Ceiling((maxY - minY) / Cell) + 1);
            _cells = new List<int>[_w * _h];
            for (int i = 0; i < pts.Count; i++)
                (_cells[Cy(pts[i].Y) * _w + Cx(pts[i].X)] ??= []).Add(i);
        }

        private int Cx(float x) => Math.Clamp((int)((x - _minX) / Cell), 0, _w - 1);
        private int Cy(float y) => Math.Clamp((int)((y - _minY) / Cell), 0, _h - 1);

        public IEnumerable<int> Near(Vector3 q, float radius)
        {
            int win = (int)MathF.Ceiling(radius / Cell);
            int cx = Cx(q.X), cy = Cy(q.Y);
            for (int gy = Math.Max(0, cy - win); gy <= Math.Min(_h - 1, cy + win); gy++)
                for (int gx = Math.Max(0, cx - win); gx <= Math.Min(_w - 1, cx + win); gx++)
                {
                    var bucket = _cells[gy * _w + gx];
                    if (bucket is null) continue;
                    foreach (int i in bucket) yield return i;
                }
        }
    }
}
