using System.Numerics;
using Clipper2Lib;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Bed-adhesion brim: outward offset loops around the FULL first-layer footprint.
///
/// Runs as the LAST toolpath step (after paint removals, X-bracing, patterns) so any
/// effect that adds or bulges first-layer geometry is enclosed — the footprint is
/// derived from the actual layer-0 extrude segments, not the mesh outline.
///
/// Loops print first, outermost → inward, so the innermost loop fuses against the
/// part's first bead; loop k's centreline sits (k − ½) bead widths outside the
/// footprint edge (adjacent beads touching).
/// </summary>
public static class BrimPlanner
{
    public static void Apply(Toolpath toolpath, SliceSettings settings)
    {
        if (!settings.BrimEnabled || settings.BrimLoops <= 0) return;
        if (toolpath.Layers.Count == 0) return;
        var layer0 = toolpath.Layers[0];
        if (layer0.Moves.Count == 0) return;

        float bead = MathF.Max(settings.BeadWidth, 0.5f);

        // 1) Footprint: dilate every layer-0 extrude segment by half a bead and union.
        var segs = new PathsD();
        float z = float.NaN;
        foreach (var m in layer0.Moves)
        {
            if (m.Kind != MoveKind.Extrude) continue;
            segs.Add(new PathD { new PointD(m.From.X, m.From.Y), new PointD(m.To.X, m.To.Y) });
            if (float.IsNaN(z)) z = m.To.Z;
        }
        if (segs.Count == 0) return;
        var footprint = Clipper.Union(
            Clipper.InflatePaths(segs, bead * 0.5, JoinType.Round, EndType.Round),
            FillRule.NonZero);
        if (footprint.Count == 0) return;

        // 2) Rings outermost → inward; drop hole boundaries (outward brim only).
        //    Round offset joins tessellate corners into many sub-mm points; simplify each
        //    ring (same as the wall contours) so the robot isn't fed points below its
        //    interpolation step — otherwise it stalls at every point while the screw keeps
        //    pumping (over-extrusion + jitter).
        double tol = Math.Max(settings.SimplificationTolerance, 0.3f);
        var loops = new List<PathD>();
        for (int k = settings.BrimLoops; k >= 1; k--)
        {
            var rings = Clipper.InflatePaths(
                footprint, bead * (k - 0.5), JoinType.Round, EndType.Polygon);
            rings = Clipper.SimplifyPaths(rings, tol);
            foreach (var r in rings)
                if (r.Count >= 3 && Clipper.Area(r) > 0)
                    loops.Add(r);
        }
        if (loops.Count == 0) return;

        // 3) Emit: extrude each ring closed, travel between rings, then travel to the
        //    original layer start. Prepended so the brim prints before everything else.
        var brim = new List<ToolpathMove>();
        Vector3? cur = null;
        foreach (var ring in loops)
        {
            var pts = new List<Vector3>(ring.Count + 1);
            foreach (var p in ring) pts.Add(new Vector3((float)p.x, (float)p.y, z));
            pts.Add(pts[0]); // close the loop
            if (cur is { } c)
                brim.Add(new ToolpathMove(c, pts[0], MoveKind.Travel) { IsBrim = true });
            for (int i = 1; i < pts.Count; i++)
                brim.Add(new ToolpathMove(pts[i - 1], pts[i], MoveKind.Extrude) { IsBrim = true });
            cur = pts[^1];
        }
        if (cur is { } last)
            brim.Add(new ToolpathMove(last, layer0.Moves[0].From, MoveKind.Travel) { IsBrim = true });

        layer0.Moves.InsertRange(0, brim);
    }
}
