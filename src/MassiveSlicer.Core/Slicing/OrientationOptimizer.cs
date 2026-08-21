using System.Numerics;
using MassiveSlicer.Core.Collision;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Finds the whole-part orientation (any face down, not just a small lean) that minimises
/// overhang risk, then picks the yaw about that new vertical which fits the bed best.
///
/// Risk model is <see cref="TiltOptimizer"/>'s, unchanged: for a build direction <c>u</c> a
/// surface triangle overhangs when its outward normal is more than 90° + the critical angle
/// away from <c>u</c>, and risk is the area-weighted severity over all triangles, normalised
/// by total area.
///
/// The one thing that cannot carry over from <see cref="TiltOptimizer"/> is its bed-supported
/// exclusion. That test uses a fixed world-Z band above the part's lowest point, which is only
/// valid while the search stays near the current vertical — flip the part onto another face and
/// "what the bed holds" is a completely different set of triangles. Here the supported set is
/// recomputed for every candidate direction: a triangle is bed-supported when it faces away
/// from <c>u</c> and all three of its vertices project to within a layer of the part's minimum
/// projection along <c>u</c>. The minimum projection comes from a convex hull built once
/// (<c>dot(hull.Support(-u), u)</c>), which is exact — the hull's vertex set contains every
/// extremal point of the mesh — and costs ≤256 dot products instead of a full vertex scan.
/// </summary>
public static class OrientationOptimizer
{
    /// <summary>Overhang steeper than this many degrees past vertical counts as risky (LFAM beads are forgiving).</summary>
    private const float CriticalOverhangDeg = 45f;

    /// <summary>Band above the lowest point (along the candidate direction) that the bed holds up.</summary>
    private const float BedBandMm = 3f;

    /// <summary>How squarely a face must point at the bed to count as resting on it.</summary>
    private const float BedFaceDot = -0.7f;

    /// <summary>
    /// How much of the bed a candidate is allowed to use — mirrors <c>FitToCellMargin</c> in the
    /// viewport's Fit to Cell, and for the same reason: a part on the exact declared edge leaves
    /// no room for a brim, a purge line, or the nozzle body.
    /// </summary>
    private const float FitToCellMargin = 0.90f;

    /// <summary>Full-sphere coarse resolution (Fibonacci lattice) — ~11° between neighbours.</summary>
    private const int CoarseSamples = 320;

    /// <summary>Coarse winners taken through to local refinement.</summary>
    private const int CoarseKeep = 12;

    /// <summary>Refined directions closer than this collapse to one candidate.</summary>
    private const float DedupDeg = 15f;

    /// <summary>Local refine half-window and step, in degrees.</summary>
    private const float RefineSpanDeg = 8f;
    private const float RefineStepDeg = 2f;

    /// <param name="Rotation">
    /// Rotation taking the mesh from its CURRENT world orientation into this candidate's.
    /// Row-vector convention, matching <see cref="Vector3.Transform(Vector3, Matrix4x4)"/> and
    /// the viewport's OpenTK matrices: apply as <c>point * Rotation</c>.
    /// </param>
    /// <param name="RiskBefore">Overhang risk (0-1) at the mesh's current orientation.</param>
    /// <param name="RiskAfter">Overhang risk (0-1) once rotated into this candidate.</param>
    /// <param name="FitsBed">
    /// Whether this candidate's own rotated footprint is small enough for the bed AT ALL. A
    /// position-independent extent test only — it says nothing about where on the bed the part
    /// would have to sit, which is the placement search's job (<see cref="SuggestPlacements"/>).
    /// </param>
    /// <param name="BedFitMarginPct">Headroom against the bed limit at the chosen yaw; 100 = exactly on the limit.</param>
    /// <param name="FootprintExtentX">Rotated footprint size along world X (mm) at the chosen yaw.</param>
    /// <param name="FootprintExtentY">Rotated footprint size along world Y (mm) at the chosen yaw.</param>
    public sealed record Candidate(
        Matrix4x4 Rotation,
        float RiskBefore,
        float RiskAfter,
        bool FitsBed,
        float BedFitMarginPct,
        float FootprintExtentX,
        float FootprintExtentY);

    /// <summary>
    /// Searches every possible "which face is down" orientation (not just small tilts) for the
    /// mesh that minimizes overhang risk, then — independently, since yaw about the new vertical
    /// doesn't affect overhang at all — picks the yaw whose footprint fits the bed best (a smaller
    /// footprint is easier to place). Returns up to <paramref name="maxCandidates"/> candidates
    /// ranked by overhang risk ascending (best first).
    /// </summary>
    /// <remarks>
    /// The only bed test applied here is position-independent: a candidate is dropped when its own
    /// rotated footprint is larger than the bed can ever hold, whatever spot you pick for it. WHERE
    /// the part ends up is deliberately not decided here — the caller searches real placements with
    /// <see cref="SuggestPlacements"/> and each candidate carries the footprint size that search
    /// needs to know how big a spot it must find.
    /// </remarks>
    /// <param name="meshesWorld">World-space triangle soup, the format <see cref="PlanarSlicer"/> consumes.</param>
    /// <param name="bed">Bed to fit against; null = no constraint (every candidate "fits").</param>
    public static IReadOnlyList<Candidate> FindCandidates(
        IReadOnlyList<Vector3[]> meshesWorld,
        BedCellConfig? bed,
        int maxCandidates = 5)
    {
        var mesh = BuildTriangles(meshesWorld);
        if (mesh.Count == 0) return [];

        float riskCurrent = Risk(mesh, Vector3.UnitZ);

        // 1) Coarse full-sphere sweep. Both a direction and its opposite are sampled on
        //    purpose: they are genuinely different orientations once the per-direction
        //    bed exclusion is applied (opposite faces end up on the plate).
        var coarse = FibonacciSphere(CoarseSamples);
        var coarseScores = new float[coarse.Length];
        Parallel.For(0, coarse.Length, i => coarseScores[i] = Objective(mesh, coarse[i]));

        var ranked = new int[coarse.Length];
        for (int i = 0; i < ranked.Length; i++) ranked[i] = i;
        Array.Sort(coarseScores, ranked);   // sorts both, score ascending

        int keep = Math.Min(CoarseKeep, ranked.Length);

        // 2) Local refine each coarse winner past the lattice's own resolution.
        var refined = new (Vector3 dir, float score)[keep];
        Parallel.For(0, keep, k => refined[k] = Refine(mesh, coarse[ranked[k]]));

        // 3) De-duplicate: near-symmetric parts otherwise return the same orientation
        //    several times over, and the operator sees five identical choices.
        Array.Sort(refined, (a, b) => a.score.CompareTo(b.score));
        float dedupDot = MathF.Cos(DedupDeg * MathF.PI / 180f);
        var unique = new List<Vector3>(keep);
        foreach (var (dir, _) in refined)
        {
            bool duplicate = false;
            foreach (var accepted in unique)
                if (Vector3.Dot(dir, accepted) > dedupDot) { duplicate = true; break; }
            if (!duplicate) unique.Add(dir);
        }

        // 4) Per surviving direction: level it, then find the yaw that fits the bed best.
        var candidates = new List<Candidate>(unique.Count);
        foreach (var u in unique)
        {
            var r0 = RotationTo(u, Vector3.UnitZ);
            var (yawRad, margin, extX, extY) = BestYaw(mesh.Hull, r0, bed);

            // Row-vector convention: A * B applies A first. Level the part (r0), THEN spin it
            // about the new vertical — the reverse order would spin before levelling and u
            // would no longer land on +Z.
            var rotation = r0 * Matrix4x4.CreateRotationZ(yawRad);

            candidates.Add(new Candidate(
                Rotation:         rotation,
                RiskBefore:       riskCurrent,
                RiskAfter:        Risk(mesh, u),
                FitsBed:          margin >= 1f,
                BedFitMarginPct:  margin * 100f,
                FootprintExtentX: extX,
                FootprintExtentY: extY));
        }

        // Extent-only sanity filter: a footprint bigger than the whole bed is unplaceable no
        // matter where it goes. Everything else is left for the placement search to try.
        return candidates
            .Where(c => c.FitsBed)
            .OrderBy(c => c.RiskAfter)
            .Take(Math.Max(0, maxCandidates))
            .ToList();
    }

    // -- Placement search ----------------------------------------------------

    /// <summary>Offsets smaller than this buy nothing — the part barely moves.</summary>
    private const float PlacementSlackEpsMm = 1f;

    /// <summary>
    /// A handful of candidate footprint-center placements to try on the bed, closest-to-center
    /// first: bed center (most likely reachable for most cells), then a small ring of alternates
    /// scaled to how much headroom the bed has once this footprint's own size is accounted for.
    /// </summary>
    /// <remarks>
    /// Deliberately coarse. Bed centre is the answer almost every time; the alternates exist only
    /// so a cell whose centre is blocked (fixture, robot base, an awkward reach) has somewhere else
    /// to be tried, and every extra entry costs a full re-slice plus IK sweep in the caller.
    /// </remarks>
    /// <param name="bedCenter">World XY of the bed's printable-surface centre.</param>
    /// <param name="bed">Bed being placed on; null = nothing to offset against, centre only.</param>
    /// <param name="footprintExtentX">Candidate's own footprint size along X (mm).</param>
    /// <param name="footprintExtentY">Candidate's own footprint size along Y (mm).</param>
    public static IReadOnlyList<(float x, float y)> SuggestPlacements(
        (float x, float y) bedCenter, BedCellConfig? bed, float footprintExtentX, float footprintExtentY,
        int maxPlacements = 5)
    {
        var result = new List<(float x, float y)> { bedCenter };
        if (bed is null || maxPlacements <= 1) return result;

        int alternates = maxPlacements - 1;

        if (bed.IsRotaryPrintBed && bed.Diameter is > 0)
        {
            // Whatever radius is left once the footprint's own half-diagonal is parked at it.
            float halfDiag = 0.5f * MathF.Sqrt(
                footprintExtentX * footprintExtentX + footprintExtentY * footprintExtentY);
            float slackR = MathF.Max(0f, bed.Diameter.Value * FitToCellMargin * 0.5f - halfDiag);
            if (slackR < PlacementSlackEpsMm) return result;

            foreach (float deg in new[] { 45f, 135f, 225f, 315f })
            {
                if (result.Count > alternates) break;
                float rad = deg * MathF.PI / 180f;
                result.Add((bedCenter.x + slackR * MathF.Cos(rad),
                            bedCenter.y + slackR * MathF.Sin(rad)));
            }
            return result;
        }

        float slackX = MathF.Max(0f, (bed.Width * FitToCellMargin - footprintExtentX) * 0.5f);
        float slackY = MathF.Max(0f, (bed.Depth * FitToCellMargin - footprintExtentY) * 0.5f);

        foreach (var (dx, dy) in new[]
                 {
                     ( slackX, 0f), (-slackX, 0f),
                     (0f,  slackY), (0f, -slackY),
                 })
        {
            if (result.Count > alternates) break;
            if (MathF.Abs(dx) < PlacementSlackEpsMm && MathF.Abs(dy) < PlacementSlackEpsMm) continue;
            result.Add((bedCenter.x + dx, bedCenter.y + dy));
        }

        return result;
    }

    // -- Geometry ------------------------------------------------------------

    /// <summary>
    /// Per-triangle vertices, unit normal and area, plus a convex hull of every vertex.
    /// Normals and areas are direction-independent, so the cross products are paid once here
    /// and every candidate direction costs only dot products.
    /// </summary>
    private sealed class TriangleSet
    {
        public Vector3[] A = [], B = [], C = [], N = [];
        public float[] Area = [];
        public float TotalArea;
        public int Count;
        public ConvexHull Hull = new([Vector3.Zero]);
    }

    private static TriangleSet BuildTriangles(IReadOnlyList<Vector3[]> meshesWorld)
    {
        int cap = 0;
        foreach (var verts in meshesWorld) cap += verts.Length / 3;

        var a = new List<Vector3>(cap);
        var b = new List<Vector3>(cap);
        var c = new List<Vector3>(cap);
        var n = new List<Vector3>(cap);
        var area = new List<float>(cap);
        var allVerts = new List<Vector3>(cap * 3);
        float totalArea = 0f;

        foreach (var verts in meshesWorld)
        {
            foreach (var v in verts) allVerts.Add(v);
            for (int i = 0; i + 2 < verts.Length; i += 3)
            {
                var p0 = verts[i]; var p1 = verts[i + 1]; var p2 = verts[i + 2];
                var cross = Vector3.Cross(p1 - p0, p2 - p0);
                float len = cross.Length();
                if (len < 1e-9f) continue;   // degenerate sliver
                a.Add(p0); b.Add(p1); c.Add(p2);
                n.Add(cross / len);
                float tri = len * 0.5f;
                area.Add(tri);
                totalArea += tri;
            }
        }

        return new TriangleSet
        {
            A = [.. a], B = [.. b], C = [.. c], N = [.. n], Area = [.. area],
            TotalArea = totalArea,
            Count = a.Count,
            Hull = allVerts.Count > 0 ? Quickhull.Build(allVerts, maxVerts: 256) : new ConvexHull([Vector3.Zero]),
        };
    }

    /// <summary>
    /// Lowest projection of the whole mesh onto <paramref name="u"/>. Exact: the hull's vertex
    /// set is a superset of the mesh's extremal points, so its support point along −u is the
    /// mesh's own minimum along u.
    /// </summary>
    private static float MinProjection(ConvexHull hull, Vector3 u)
        => Vector3.Dot(hull.Support(-u), u);

    /// <summary>Evenly spread directions over the FULL sphere (Fibonacci lattice).</summary>
    private static Vector3[] FibonacciSphere(int count)
    {
        var result = new Vector3[count];
        float goldenAngle = MathF.PI * (3f - MathF.Sqrt(5f));
        for (int i = 0; i < count; i++)
        {
            float y = 1f - 2f * (i + 0.5f) / count;
            float r = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
            float theta = i * goldenAngle;
            result[i] = Vector3.Normalize(new Vector3(
                r * MathF.Cos(theta), r * MathF.Sin(theta), y));
        }
        return result;
    }

    /// <summary>
    /// Polishes one coarse winner with a small sweep in its own tangent plane — the coarse
    /// lattice only resolves ~11°, which is coarser than the difference between "clears the
    /// critical angle" and "doesn't".
    /// </summary>
    private static (Vector3 dir, float score) Refine(TriangleSet mesh, Vector3 seed)
    {
        var (t1, t2) = TangentFrame(seed);
        var bestDir = seed;
        float bestScore = Objective(mesh, seed);

        for (float da = -RefineSpanDeg; da <= RefineSpanDeg; da += RefineStepDeg)
        for (float db = -RefineSpanDeg; db <= RefineSpanDeg; db += RefineStepDeg)
        {
            if (da == 0f && db == 0f) continue;
            var dir = Vector3.Normalize(
                seed + t1 * MathF.Tan(da * MathF.PI / 180f)
                     + t2 * MathF.Tan(db * MathF.PI / 180f));
            float score = Objective(mesh, dir);
            if (score < bestScore) { bestScore = score; bestDir = dir; }
        }

        return (bestDir, bestScore);
    }

    /// <summary>Any two unit vectors completing an orthonormal frame with <paramref name="u"/>.</summary>
    private static (Vector3 t1, Vector3 t2) TangentFrame(Vector3 u)
    {
        // Cross with whichever axis u is least aligned to, so the cross never degenerates.
        var helper = MathF.Abs(u.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        var t1 = Vector3.Normalize(Vector3.Cross(u, helper));
        var t2 = Vector3.Normalize(Vector3.Cross(u, t1));
        return (t1, t2);
    }

    /// <summary>
    /// Rotation taking <paramref name="from"/> onto <paramref name="to"/> (both unit),
    /// row-vector convention: <c>Vector3.Transform(from, result) == to</c>.
    /// </summary>
    private static Matrix4x4 RotationTo(Vector3 from, Vector3 to)
    {
        float dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot > 0.999999f) return Matrix4x4.Identity;
        if (dot < -0.999999f)
        {
            // Anti-parallel: cross() is zero, so pick any perpendicular and flip 180°.
            var (perp, _) = TangentFrame(from);
            return Matrix4x4.CreateFromAxisAngle(perp, MathF.PI);
        }
        var axis = Vector3.Normalize(Vector3.Cross(from, to));
        return Matrix4x4.CreateFromAxisAngle(axis, MathF.Acos(dot));
    }

    // -- Bed fit -------------------------------------------------------------

    /// <summary>
    /// Sweeps yaw about the levelled part's vertical for the best bed-fit margin, and reports the
    /// footprint that yaw produces. The hull's AABB is the mesh's AABB (hull vertices include every
    /// extreme), so this is exact and costs ≤256 transforms per yaw instead of a full mesh pass.
    /// </summary>
    /// <remarks>
    /// The margin is a footprint-SIZE score, not a position test: extents are translation-invariant,
    /// so a yaw that scores well here is one whose footprint is compact enough to be easy to place,
    /// wherever it ends up going.
    /// </remarks>
    private static (float yawRad, float margin, float extX, float extY) BestYaw(
        ConvexHull hull, Matrix4x4 r0, BedCellConfig? bed)
    {
        float bestYaw = 0f, bestMargin = float.MinValue;
        float bestExtX = 0f, bestExtY = 0f;

        void Consider(float yawDeg)
        {
            float yawRad = yawDeg * MathF.PI / 180f;
            var m = r0 * Matrix4x4.CreateRotationZ(yawRad);
            var (extX, extY) = HullExtentsXY(hull, m);
            float margin = FitMargin(extX, extY, bed);
            if (margin > bestMargin)
            {
                bestMargin = margin; bestYaw = yawRad;
                bestExtX = extX; bestExtY = extY;
            }
        }

        for (float yaw = 0f; yaw < 360f; yaw += 5f) Consider(yaw);

        float coarse = bestYaw * 180f / MathF.PI;
        for (float yaw = coarse - 5f; yaw <= coarse + 5f; yaw += 1f) Consider(yaw);

        return (bestYaw, bestMargin, bestExtX, bestExtY);
    }

    /// <summary>Footprint size of the hull under <paramref name="m"/>, world X and Y (mm).</summary>
    private static (float extX, float extY) HullExtentsXY(ConvexHull hull, Matrix4x4 m)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var v in hull.Vertices)
        {
            var p = Vector3.Transform(v, m);
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        return (MathF.Max(maxX - minX, 1e-3f), MathF.Max(maxY - minY, 1e-3f));
    }

    /// <summary>
    /// Bed headroom for a footprint of <paramref name="extX"/> × <paramref name="extY"/>: ≥1 can
    /// fit somewhere on the plate, &lt;1 is too big for it at any position. Mirrors the viewport's
    /// Fit to Cell test — rectangular beds constrain X and Y separately, a round platter constrains
    /// the footprint diagonal.
    /// </summary>
    private static float FitMargin(float extX, float extY, BedCellConfig? bed)
    {
        if (bed is null) return 1f;   // no cell selected — nothing to fit against

        if (bed.IsRotaryPrintBed && bed.Diameter is > 0)
        {
            float allowed  = bed.Diameter.Value * FitToCellMargin;
            float diagonal = MathF.Max(MathF.Sqrt(extX * extX + extY * extY), 1e-3f);
            return allowed / diagonal;
        }

        float allowedX = bed.Width * FitToCellMargin;
        float allowedY = bed.Depth * FitToCellMargin;
        return MathF.Min(allowedX / extX, allowedY / extY);
    }

    // -- Risk model (TiltOptimizer's, with a per-direction bed exclusion) ----

    /// <summary>
    /// Reported metric: area fraction (0..1) at overhangs past the critical angle, weighted by
    /// how far past. This is the "will it actually fail" number shown to the user.
    /// </summary>
    private static float Risk(TriangleSet mesh, Vector3 u)
    {
        float cosLimit = MathF.Cos((90f + CriticalOverhangDeg) * MathF.PI / 180f);   // ≈ -0.707
        float bedTop = MinProjection(mesh.Hull, u) + BedBandMm;

        float total = 0f, risky = 0f;
        for (int i = 0; i < mesh.Count; i++)
        {
            float dot = Vector3.Dot(mesh.N[i], u);
            if (IsBedSupported(mesh, i, u, dot, bedTop)) continue;

            total += mesh.Area[i];
            if (dot < cosLimit)
            {
                // Severity ramps 0→1 from the critical angle to fully downward-facing.
                float severity = (cosLimit - dot) / (cosLimit + 1f);
                risky += mesh.Area[i] * severity;
            }
        }
        return total > 0f ? risky / total : 0f;
    }

    /// <summary>
    /// Search objective: hard-overhang risk plus a small sub-critical margin term. Without the
    /// margin term the search would park surfaces exactly on the critical angle (zero risk, zero
    /// safety margin); the quartic ramp keeps pressure on until overhangs are comfortably inside
    /// the limit, while staying negligible for mild slopes.
    /// </summary>
    private static float Objective(TriangleSet mesh, Vector3 u)
    {
        float cosLimit = MathF.Cos((90f + CriticalOverhangDeg) * MathF.PI / 180f);   // ≈ -0.707
        float bedTop = MinProjection(mesh.Hull, u) + BedBandMm;

        float total = 0f, score = 0f;
        for (int i = 0; i < mesh.Count; i++)
        {
            float dot = Vector3.Dot(mesh.N[i], u);
            if (IsBedSupported(mesh, i, u, dot, bedTop)) continue;

            total += mesh.Area[i];

            if (dot < cosLimit)
            {
                // Past critical: dominant term. Kept nearly flat (1→1.5) on purpose — a steep
                // ramp makes the search chase marginal ceiling-angle reductions instead of
                // minimising the unprintable area itself.
                score += mesh.Area[i] * (1f + 0.5f * (cosLimit - dot) / (cosLimit + 1f));
            }
            else if (dot < 0f)
            {
                // Below critical but downward-facing: quadratic margin pressure. Needs a real
                // gradient — a flatter curve leaves a plateau of near-equal directions once all
                // overhangs clear critical, making the winner arbitrary.
                float phi = MathF.Acos(Math.Clamp(dot, -1f, 1f)) * 180f / MathF.PI - 90f;   // 0..critical
                float t = phi / CriticalOverhangDeg;
                score += mesh.Area[i] * 0.3f * t * t;
            }
        }
        return total > 0f ? score / total : 0f;
    }

    /// <summary>
    /// Whether triangle <paramref name="i"/> rests on the bed when <paramref name="u"/> is up:
    /// it faces away from the build direction and all three vertices sit within a layer of the
    /// part's lowest projection along <paramref name="u"/>. The bed holds these — they are not
    /// overhangs.
    /// </summary>
    private static bool IsBedSupported(TriangleSet mesh, int i, Vector3 u, float normalDot, float bedTop)
        => normalDot < BedFaceDot
           && Vector3.Dot(mesh.A[i], u) < bedTop
           && Vector3.Dot(mesh.B[i], u) < bedTop
           && Vector3.Dot(mesh.C[i], u) < bedTop;
}
