using System;
using System.Collections.Generic;
using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Generates a 3-axis relief-milling toolpath from a <see cref="ReliefMap"/> — CNC relief
/// carving. The heightmap is sampled on a grid at the milling stepover (NO high-res displaced
/// mesh), a ball/flat-end anti-gouge inverse offset gives the non-gouging tool-tip Z per cell,
/// then gouge-free depth passes (riding the offset surface clamped to descending floors) plus a
/// full finish pass are emitted as a boustrophedon raster.
/// <para>Output is in world mm; cuts are <see cref="MoveKind.Mill"/>, repositioning/retracts are
/// <see cref="MoveKind.Travel"/> (lifts flagged <c>IsZHop</c>); all layers keep <c>PlaneNormal = +Z</c>
/// (top-down spindle). v1 references a flat nominal plane (<see cref="ReliefMap.ReferencePlaneZ"/>).</para>
/// </summary>
public static class ReliefMillSlicer
{
    public static Toolpath Slice(ReliefMap map, MillSettings mill)
    {
        var tp = new Toolpath();
        if (map.Cols < 2 || map.Rows < 2 || map.WidthMm <= 0f || map.LengthMm <= 0f) return tp;

        float step = MathF.Max(mill.StepoverMm, 0.05f);
        int nx = Math.Max(2, (int)MathF.Round(map.WidthMm  / step) + 1);
        int ny = Math.Max(2, (int)MathF.Round(map.LengthMm / step) + 1);
        float dx = map.WidthMm  / (nx - 1);
        float dy = map.LengthMm / (ny - 1);

        float WorldX(int gx) => map.OriginX + gx * dx;
        float WorldY(int gy) => map.OriginY + gy * dy;

        // Carved target surface (what the cutter tip should ultimately reach).
        var target = new float[nx * ny];
        for (int gy = 0; gy < ny; gy++)
            for (int gx = 0; gx < nx; gx++)
            {
                float z = map.SampleSurfaceZ(WorldX(gx), WorldY(gy));
                target[gy * nx + gx] = float.IsNaN(z) ? map.ReferencePlaneZ : z;
            }

        // Anti-gouge inverse offset -> non-gouging tool-TIP Z per cell.
        float r = MathF.Max(mill.ToolRadiusMm, 0f);
        int kr = r > 0f ? (int)MathF.Ceiling(r / MathF.Min(dx, dy)) : 0;
        float deepestAllowed = map.ReferencePlaneZ - mill.MaxDepthMm; // -inf when MaxDepth = +inf
        var tip = new float[nx * ny];
        for (int gy = 0; gy < ny; gy++)
            for (int gx = 0; gx < nx; gx++)
            {
                // Ball: tipZ = max(target + sqrt(r^2 - d^2)) - r. Flat: tipZ = max(target).
                float best = float.NegativeInfinity;
                for (int oy = -kr; oy <= kr; oy++)
                {
                    int ny2 = gy + oy; if (ny2 < 0 || ny2 >= ny) continue;
                    for (int ox = -kr; ox <= kr; ox++)
                    {
                        int nx2 = gx + ox; if (nx2 < 0 || nx2 >= nx) continue;
                        float ddx = ox * dx, ddy = oy * dy;
                        float d2 = ddx * ddx + ddy * ddy;
                        if (d2 > r * r) continue;
                        float t = target[ny2 * nx + nx2];
                        float cand = mill.ToolEnd == ToolEndType.Ball
                            ? t + MathF.Sqrt(MathF.Max(r * r - d2, 0f))   // ball center contribution
                            : t;                                          // flat end-mill
                        if (cand > best) best = cand;
                    }
                }
                if (float.IsNegativeInfinity(best)) best = target[gy * nx + gx] + r;
                float tz = mill.ToolEnd == ToolEndType.Ball ? best - r : best;
                tip[gy * nx + gx] = MathF.Max(tz, deepestAllowed);
            }

        float top = map.ReferencePlaneZ;     // blank top (v1 nominal flat)
        float floorZ = float.PositiveInfinity;
        foreach (var v in tip) if (v < floorZ) floorZ = v;
        if (float.IsPositiveInfinity(floorZ)) return tp;

        float safeZ = top + MathF.Max(mill.RapidZMm, 1f);
        float stepdown = MathF.Max(mill.StepdownMm, 0.1f);

        // Descending roughing floors, then a finish sentinel below floorZ (rides pure tip surface).
        var floors = new List<(float F, bool Finish)>();
        for (float f = top - stepdown; f > floorZ + 1e-3f; f -= stepdown)
            floors.Add((f, false));
        floors.Add((floorZ - 1f, true)); // final full finish pass

        int idx = 0;
        float prevF = top;
        foreach (var (F, finish) in floors)
        {
            var layer = new ToolpathLayer(idx, finish ? floorZ : F) { Height = stepdown };
            float capturedPrev = prevF;
            EmitRaster(layer, nx, ny, WorldX, WorldY, safeZ, mill,
                cut: (gx, gy) => finish || tip[gy * nx + gx] < capturedPrev - 1e-3f,
                zAt: (gx, gy) => MathF.Max(tip[gy * nx + gx], F));
            if (layer.Moves.Count > 0) { tp.Layers.Add(layer); idx++; }
            prevF = F;
        }
        return tp;
    }

    /// <summary>Boustrophedon raster over the grid: cut spans of active cells riding <paramref name="zAt"/>,
    /// retract to <paramref name="safeZ"/> and rapid between spans/rows.</summary>
    private static void EmitRaster(
        ToolpathLayer layer, int nx, int ny,
        Func<int, float> worldX, Func<int, float> worldY, float safeZ, MillSettings mill,
        Func<int, int, bool> cut, Func<int, int, float> zAt)
    {
        Vector3? cur = null;

        void Travel(Vector3 to, bool zhop) =>
            layer.Moves.Add(new ToolpathMove(cur ?? to, to, MoveKind.Travel) { IsZHop = zhop });
        void Cut(Vector3 to) =>
            layer.Moves.Add(new ToolpathMove(cur ?? to, to, MoveKind.Mill));

        for (int row = 0; row < ny; row++)
        {
            bool leftToRight = (row & 1) == 0;
            int gxStart = leftToRight ? 0 : nx - 1;
            int gxEnd   = leftToRight ? nx : -1;
            int dir     = leftToRight ? 1 : -1;

            int gx = gxStart;
            while (gx != gxEnd)
            {
                // skip inactive cells
                if (!cut(gx, row)) { gx += dir; continue; }

                // span [gx .. spanEnd] of consecutive active cells
                int spanStart = gx;
                int spanEnd = gx;
                while (spanEnd + dir != gxEnd && cut(spanEnd + dir, row)) spanEnd += dir;

                float sx = worldX(spanStart), sy = worldY(row), sz = zAt(spanStart, row);
                var start = new Vector3(sx, sy, sz);

                // approach: retract current up, rapid over at safe Z, then descend to the surface.
                if (cur is { } c && MathF.Abs(c.Z - safeZ) > 1e-3f)
                    Travel(new Vector3(c.X, c.Y, safeZ), zhop: true);
                Travel(new Vector3(sx, sy, safeZ), zhop: false);
                Travel(start, zhop: true);    // vertical descent/plunge to surface
                cur = start;

                // cut horizontally along the span (Mill moves ride the surface Z)
                for (int g = spanStart + dir; g != spanEnd + dir; g += dir)
                {
                    var p = new Vector3(worldX(g), worldY(row), zAt(g, row));
                    Cut(p); cur = p;
                }

                gx = spanEnd + dir;
            }
        }

        // final retract
        if (cur is { } e && MathF.Abs(e.Z - safeZ) > 1e-3f)
            Travel(new Vector3(e.X, e.Y, safeZ), zhop: true);
    }
}
