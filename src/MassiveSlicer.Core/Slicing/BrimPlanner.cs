using System.Numerics;
using Clipper2Lib;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Bed-adhesion brim: offset loops alongside the FULL first-layer footprint.
///
/// Runs as the LAST toolpath step (after paint removals, X-bracing, patterns) so any
/// effect that adds or bulges first-layer geometry is enclosed — the footprint is
/// derived from the actual layer-0 extrude segments, not the mesh outline.
///
/// Loops print farthest → nearest, so the last one fuses against the part's first bead;
/// loop k's centreline sits (k − ½) bead widths from the footprint edge (adjacent beads
/// touching).
///
/// <para><b>What "inside" means.</b> Air in a 2D slice, flood-filled from infinity: it wraps
/// the whole exterior and pushes in through every gap that connects, including down a seam
/// into an open arm — so that arm is OUTSIDE. What air cannot reach is INSIDE, and
/// topologically that is exactly the HOLES of the bead region, which is what the ring sign
/// tests. Sub-bead slivers close under the offset and drop out on their own.</para>
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
        var toolpathRegion = Clipper.Union(
            Clipper.InflatePaths(segs, bead * 0.5, JoinType.Round, EndType.Round),
            FillRule.NonZero);
        if (toolpathRegion.Count == 0) return;

        // The footprint IS the toolpath bead region. "Inside" means the air in a 2D slice cannot
        // reach it: flood-filling from infinity wraps the whole exterior and pushes in through every
        // gap that connects, including down a seam into an open arm - so that arm reads as OUTSIDE.
        // Topologically the unreachable air is exactly this region's HOLES, which is what the ring
        // sign below tests.
        //
        // Measured on a real capital: 300 holes, of which the top 5 hold 99.9% of the sealed area
        // (four interiors around 420,000 mm2 each) and 287 are smaller than one bead square, many of
        // them zero - numerical slivers where beads meet. Those cannot hold a loop, so inflating the
        // material closes them and Clipper drops them without any size test here. Do NOT "clean up"
        // the hole count: the big ones are the real interiors and they are the whole point.
        var footprint = toolpathRegion;
        if (footprint.Count == 0) return;

        // 2) Offset rings, farthest first. Round offset joins tessellate corners into many
        //    sub-mm points; simplify each ring (same as the wall contours) so the robot isn't
        //    fed points below its interpolation step — otherwise it stalls at every point while
        //    the screw keeps pumping (over-extrusion + jitter).
        double tol = Math.Max(settings.SimplificationTolerance, 0.3f);
        // Positive area = an outer boundary. Negative = a hole boundary pushed (k - 1/2) beads INTO
        // its void. One inflate yields both; the sign is the whole distinction.
        //
        // This replaced a side-of-travel classifier. That was built to cope with the OPEN paths the
        // toolpath footprint produced, and it got this part wrong: offsetting the capital outward by
        // 15mm closes the mouth of its crescent cross-section, and the resulting pocket ring was
        // being lumped in with the outer family, so Inside came back empty and Both equalled Outside.
        // Mesh cross-sections are always CLOSED, so with the footprint coming from the mesh the sign
        // is sufficient and there is nothing left for a side rule to do.
        var outer = new List<PathD>();
        var inner = new List<PathD>();
        for (int k = settings.BrimLoops; k >= 1; k--)
        {
            var rings = Clipper.InflatePaths(
                footprint, bead * (k - 0.5), JoinType.Round, EndType.Polygon);
            rings = Clipper.SimplifyPaths(rings, tol);
            foreach (var ring in rings)
            {
                if (ring.Count < 3) continue;
                double area = Clipper.Area(ring);
                if (area > 0)      outer.Add(ring);
                else if (area < 0) inner.Add(Reversed(ring));
            }
        }

        bool wantOutside = settings.BrimDirection is BrimDirection.Outside or BrimDirection.Both;
        bool wantInside  = settings.BrimDirection is BrimDirection.Inside  or BrimDirection.Both;

        // Grouped, NOT interleaved per offset. Interleaving hands the head across the part once per
        // loop, and every crossing is a within-layer travel - a dead stop the screw pumps through.
        // Farthest-first order within each family is preserved, so the last loop laid on a side is
        // the one against the part.
        var keep = new List<PathD>(outer.Count + inner.Count);
        if (wantOutside) keep.AddRange(outer);
        if (wantInside)  keep.AddRange(inner);
        if (keep.Count == 0) return;

        // 4) Emit. A run covering a whole ring closes; a partial run stays open, because
        //    closing it would draw a chord straight across the part.
        var brim = new List<ToolpathMove>();
        Vector3? cur = null;
        foreach (var ring in keep)
        {
            var pts = new List<Vector3>(ring.Count + 1);
            foreach (var pt in ring) pts.Add(new Vector3((float)pt.x, (float)pt.y, z));
            pts.Add(pts[0]); // closed loop
            if (pts.Count < 3) continue;
            if (cur is { } c)
                brim.Add(new ToolpathMove(c, pts[0], MoveKind.Travel) { IsBrim = true });
            for (int i = 1; i < pts.Count; i++)
                brim.Add(new ToolpathMove(pts[i - 1], pts[i], MoveKind.Extrude) { IsBrim = true });
            cur = pts[^1];
        }
        if (brim.Count == 0) return;
        if (cur is { } last)
            brim.Add(new ToolpathMove(last, layer0.Moves[0].From, MoveKind.Travel) { IsBrim = true });

        layer0.Moves.InsertRange(0, brim);
    }

    /// <summary>Flips a ring's winding.</summary>
    private static PathD Reversed(PathD ring)
    {
        var flipped = new PathD(ring.Count);
        for (int i = ring.Count - 1; i >= 0; i--) flipped.Add(ring[i]);
        return flipped;
    }
}
