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
/// <para><b>Topology comes from the MESH cross-section, not the toolpath.</b> A toolpath
/// footprint carries the slicer's own internal structure: on a real capital it had 300
/// interior holes where the mesh had none, and every one got a brim offset into it. The mesh
/// silhouette is unioned with the toolpath (so pattern bulges outside it are still enclosed)
/// and every hole the mesh does not itself have is filled.</para>
///
/// <para><see cref="BrimDirection"/> then selects by ring SIGN: positive rings are outer
/// boundaries, negative rings are hole boundaries pushed into their void. Mesh cross-sections
/// are always closed, so the sign is sufficient — note a hole can be created by the offset
/// itself closing over a concavity, which is a real enclosed pocket and belongs to Inside.</para>
/// </summary>
public static class BrimPlanner
{

    /// <param name="meshContours">
    /// Layer 0's MESH cross-section (closed contours only). This defines the footprint TOPOLOGY.
    /// Deriving it from the toolpath instead drags in every gap between wall passes and every
    /// infill void: measured on a real capital, the toolpath footprint had 300 interior holes
    /// where the mesh had 0, and brim offset into all of them - 50 m of bead scattered through
    /// the part. Null falls back to the toolpath, for callers that have no mesh.
    /// </param>
    public static void Apply(
        Toolpath toolpath,
        SliceSettings settings,
        IReadOnlyList<IReadOnlyList<Vector2>>? meshContours = null)
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

        // The mesh decides the topology; the toolpath only widens the outer extent.
        //
        // Union the two so a pattern bulge or X-bracing detour poking outside the silhouette is still
        // enclosed, then FILL every hole the mesh does not itself have. That one step removes the
        // slicer's own internal structure - wall gaps, infill voids, seams, connecting paths - none of
        // which are part of the shape and none of which want a brim offset into them.
        var meshRegion = MeshRegion(meshContours);
        var footprint = toolpathRegion;
        if (meshRegion.Count > 0)
        {
            var combined = Clipper.Union(toolpathRegion, meshRegion, FillRule.NonZero);
            // Keep only positive (outer) rings: unioning them fills every hole at once.
            var outers = new PathsD();
            foreach (var r in combined) if (Clipper.Area(r) > 0) outers.Add(r);
            var filled = Clipper.Union(outers, FillRule.NonZero);
            // Put back only the mesh's OWN holes - a real bore keeps its inside brim.
            var meshHoles = new PathsD();
            foreach (var r in meshRegion) if (Clipper.Area(r) < 0) meshHoles.Add(Reversed(r));
            footprint = meshHoles.Count > 0
                ? Clipper.Difference(filled, meshHoles, FillRule.NonZero)
                : filled;
        }
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

    /// <summary>Closed mesh contours as a Clipper region. Open contours enclose nothing.</summary>
    private static PathsD MeshRegion(IReadOnlyList<IReadOnlyList<Vector2>>? contours)
    {
        var paths = new PathsD();
        if (contours is null) return paths;
        foreach (var c in contours)
        {
            if (c.Count < 3) continue;
            var path = new PathD(c.Count);
            foreach (var v in c) path.Add(new PointD(v.X, v.Y));
            // A contour that encloses nothing (a hairline or a doubled-back sliver) contributes
            // no region and would only add noise to the union.
            if (Math.Abs(Clipper.Area(path)) < 1e-6) continue;
            paths.Add(path);
        }
        if (paths.Count == 0) return paths;
        // Union with EvenOdd, not NonZero: the raw contours arrive with whatever winding the mesh
        // gave them, and EvenOdd resolves nesting into outer/hole by containment instead of
        // trusting that direction. A bore wound the same way as its outer wall would otherwise
        // fill in and lose its inside brim.
        return Clipper.Union(paths, FillRule.EvenOdd);
    }

    /// <summary>Flips a ring's winding.</summary>
    private static PathD Reversed(PathD ring)
    {
        var flipped = new PathD(ring.Count);
        for (int i = ring.Count - 1; i >= 0; i--) flipped.Add(ring[i]);
        return flipped;
    }
}
