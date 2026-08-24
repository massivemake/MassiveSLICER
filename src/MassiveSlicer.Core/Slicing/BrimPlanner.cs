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

        // Order and connect so the head never crosses anything already laid, and never lifts.
        //
        // Brim is PREPENDED, so while it prints the only printed material anywhere is earlier brim -
        // the walls do not exist yet. That makes "do not cross printed material" achievable by
        // ordering alone:
        //
        //   inner pockets first, each ring stepping OUTWARD onto virgin bed
        //   pocket to pocket crosses where walls will be, which is bare plate
        //   outer loops last, farthest -> nearest, each step INWARD onto virgin bed
        //   hand off to the part from the innermost outer loop, which already hugs it
        //
        // Inner-first is what makes the handoff free: ending on the outer loop leaves the head
        // against the part's own wall instead of deep in a pocket, and that loop is laid immediately
        // before the part so the two fuse while both are fresh.
        var partStart = layer0.Moves[0].From;
        var cursor = new PointD(partStart.X, partStart.Y);

        var ordered = new List<PathD>();
        if (wantInside)
        {
            // Greedy nearest-neighbour. Rings 1 bead apart cluster by pocket on their own, so this
            // works a pocket out before moving on without needing to group them explicitly.
            var pending = new List<PathD>(inner);
            while (pending.Count > 0)
            {
                int best = 0; double bestD = double.MaxValue; int bestVert = 0;
                for (int i = 0; i < pending.Count; i++)
                {
                    (int v, double d) = NearestVertex(pending[i], cursor);
                    if (d < bestD) { bestD = d; best = i; bestVert = v; }
                }
                var ring = Rotate(pending[best], bestVert);
                pending.RemoveAt(best);
                ordered.Add(ring);
                cursor = ring[0];
            }
        }
        if (wantOutside)
        {
            // Keep the farthest-first order the offset loop produced; only choose where each ring
            // STARTS, so the step between them is the radial one rather than a chord across the part.
            foreach (var ring in outer)
            {
                (int v, _) = NearestVertex(ring, cursor);
                var rot = Rotate(ring, v);
                ordered.Add(rot);
                cursor = rot[0];
            }
        }
        if (ordered.Count == 0) return;
        // A closed ring ends where it starts, so the LAST ring's start point is also where the head
        // is left standing when the brim finishes. Re-start that one at the vertex nearest the part's
        // own first move instead of nearest the previous loop: the handoff then costs about a bead
        // rather than a run around the perimeter. It pushes the one unavoidable travel earlier, into
        // the gap between loops, where it crosses bare plate.
        if (ordered.Count > 0)
        {
            (int v, _) = NearestVertex(ordered[^1], cursor = new PointD(partStart.X, partStart.Y));
            ordered[^1] = Rotate(ordered[^1], v);
        }

        // Emit. A ring is closed, so it ends where it began: the gap to the next ring is the distance
        // between their chosen start points. A short gap is the radial step between concentric loops
        // and is laid as BEAD - that is what removes the travel between loops entirely, and 1 bead of
        // extra material is nothing. A long gap is a genuine crossing to another pocket and stays a
        // travel, but at layer height with nothing to lift over.
        double connectAsBead = bead * 2.0;
        var brim = new List<ToolpathMove>();
        Vector3? cur = null;
        foreach (var ring in ordered)
        {
            var pts = new List<Vector3>(ring.Count + 1);
            foreach (var pt in ring) pts.Add(new Vector3((float)pt.x, (float)pt.y, z));
            pts.Add(pts[0]);
            if (pts.Count < 3) continue;
            if (cur is { } c)
            {
                bool near = Vector3.Distance(c, pts[0]) <= connectAsBead;
                brim.Add(new ToolpathMove(c, pts[0],
                    near ? MoveKind.Extrude : MoveKind.Travel) { IsBrim = true });
            }
            for (int i = 1; i < pts.Count; i++)
                brim.Add(new ToolpathMove(pts[i - 1], pts[i], MoveKind.Extrude) { IsBrim = true });
            cur = pts[^1];
        }
        if (brim.Count == 0) return;
        if (cur is { } last && Vector3.Distance(last, partStart) > 1e-3f)
            brim.Add(new ToolpathMove(last, partStart, MoveKind.Travel) { IsBrim = true });

        layer0.Moves.InsertRange(0, brim);
    }

    /// <summary>Index of the ring vertex closest to a point, and that distance.</summary>
    private static (int Index, double Distance) NearestVertex(PathD ring, PointD from)
    {
        int best = 0; double bestD = double.MaxValue;
        for (int i = 0; i < ring.Count; i++)
        {
            double dx = ring[i].x - from.x, dy = ring[i].y - from.y;
            double d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = i; }
        }
        return (best, Math.Sqrt(bestD));
    }

    /// <summary>
    /// Re-starts a closed ring at <paramref name="start"/>. The vertex order and therefore the
    /// print direction are unchanged — only where the loop begins moves, which is what turns the
    /// hop between concentric loops into a radial step instead of a chord across the part.
    /// </summary>
    private static PathD Rotate(PathD ring, int start)
    {
        if (start <= 0 || start >= ring.Count) return ring;
        var rotated = new PathD(ring.Count);
        for (int i = 0; i < ring.Count; i++) rotated.Add(ring[(start + i) % ring.Count]);
        return rotated;
    }

    /// <summary>Flips a ring's winding.</summary>
    private static PathD Reversed(PathD ring)
    {
        var flipped = new PathD(ring.Count);
        for (int i = ring.Count - 1; i >= 0; i--) flipped.Add(ring[i]);
        return flipped;
    }
}
