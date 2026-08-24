using System.Numerics;
using Clipper2Lib;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Bed-adhesion brim: offset loops around the FULL first-layer footprint.
///
/// Runs as the LAST toolpath step (after paint removals, X-bracing, patterns) so any
/// effect that adds or bulges first-layer geometry is enclosed — the footprint is
/// derived from the actual layer-0 extrude segments, not the mesh outline.
///
/// Loops print first, farthest → nearest, so the last one fuses against the part's first
/// bead; loop k's centreline sits (k − ½) bead widths from the footprint edge (adjacent
/// beads touching).
///
/// <para><see cref="BrimDirection"/> chooses which edges get loops. Outward offsets the
/// outer boundary; inward offsets every interior HOLE boundary into its own void. Both come
/// from the same offset pass — growing the footprint moves the outer edge out and each hole
/// edge in by the same distance — so inward costs no extra geometry work, only keeping the
/// rings the outward-only version discarded.</para>
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
        bool wantOutward = settings.BrimDirection is BrimDirection.Outward or BrimDirection.Both;
        bool wantInward  = settings.BrimDirection is BrimDirection.Inward  or BrimDirection.Both;

        // The two families are collected separately and concatenated, NOT interleaved per
        // offset. Interleaving would hand the head back and forth across the part once per
        // loop, and every one of those crossings is a within-layer travel — a dead stop the
        // extruder keeps pumping through. Grouped, the head crosses once.
        var outward = new List<PathD>();
        var inward  = new List<PathD>();
        for (int k = settings.BrimLoops; k >= 1; k--)
        {
            var rings = Clipper.InflatePaths(
                footprint, bead * (k - 0.5), JoinType.Round, EndType.Polygon);
            rings = Clipper.SimplifyPaths(rings, tol);
            foreach (var r in rings)
            {
                if (r.Count < 3) continue;
                double area = Clipper.Area(r);
                // Positive = the grown outer boundary. Negative = a hole boundary pushed
                // (k - 1/2) beads INTO its void — an inward loop, measured off the hole edge
                // instead of the outer edge. One inflate yields both; only the sign differs.
                // A hole narrower than twice the offset closes up and Clipper drops it, so
                // small holes lose their deeper loops on their own with no size test here.
                if (area > 0)      { if (wantOutward) outward.Add(r); }
                else if (area < 0) { if (wantInward)  inward.Add(Reversed(r)); }
            }
        }
        // Outward runs farthest -> nearest and inward runs deepest -> nearest, so whichever
        // family prints last ends against the part and fuses to its first bead.
        var loops = new List<PathD>(outward.Count + inward.Count);
        loops.AddRange(outward);
        loops.AddRange(inward);
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

    /// <summary>
    /// Hole rings come back wound the opposite way to outer rings — that opposite winding is
    /// how Clipper marks them, and it is what the old area &gt; 0 filter keyed off. Flip them so
    /// every brim loop is emitted in the same direction rather than alternating per family.
    /// </summary>
    private static PathD Reversed(PathD ring)
    {
        var flipped = new PathD(ring.Count);
        for (int i = ring.Count - 1; i >= 0; i--) flipped.Add(ring[i]);
        return flipped;
    }
}
