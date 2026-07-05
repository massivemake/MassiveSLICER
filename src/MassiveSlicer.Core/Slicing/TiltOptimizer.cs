using System.Numerics;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Finds the angled-slicer tilt (X/Y tilt degrees) that minimises overhang risk for a mesh.
///
/// Risk model: for a candidate slice direction <c>d</c> (the layer-advance normal used by
/// <see cref="AngledPlanarSlicer"/>), a surface triangle overhangs when the angle between its
/// outward normal and <c>d</c> exceeds 90° + the critical overhang angle — i.e. it faces away
/// from the already-printed material. Risk is the area-weighted sum of that severity over all
/// triangles (bed-supported bottom faces excluded), normalised by total area, plus a small
/// tilt-magnitude penalty so flatter solutions win ties (extreme tilts strain robot kinematics).
///
/// Triangle normals are binned on the unit sphere first, so evaluating thousands of candidate
/// directions costs O(bins), not O(triangles).
/// </summary>
public static class TiltOptimizer
{
    /// <summary>Overhang steeper than this many degrees past vertical counts as risky (LFAM beads are forgiving).</summary>
    private const float CriticalOverhangDeg = 45f;

    /// <summary>Preference weight for smaller tilts — tie-breaker, small vs. risk values in [0,1].</summary>
    private const float TiltPenalty = 0.03f;

    /// <summary>Sphere-binning resolution in degrees.</summary>
    private const float BinDeg = 4f;

    public sealed record Result(
        float TiltXDeg,
        float TiltYDeg,
        /// <summary>Yaw (degrees, about world Z) to apply to the mesh — non-zero only in rotate-mesh mode.</summary>
        float MeshYawDeg,
        /// <summary>Overhang-risk fraction (0-1) at the current tilt, for comparison.</summary>
        float RiskBefore,
        /// <summary>Overhang-risk fraction (0-1) at the optimised tilt.</summary>
        float RiskAfter);

    /// <summary>
    /// Optimises the tilt for <paramref name="meshes"/> (world-space triangle soup, same format
    /// <see cref="AngledPlanarSlicer.Slice"/> consumes). With <paramref name="allowMeshYaw"/> the
    /// search covers every lean azimuth and reports the mesh yaw that turns the winner into a pure
    /// Y-tilt (X tilt = 0) — the caller rotates the mesh; otherwise only X/Y tilt combinations
    /// reachable without moving the part are searched.
    /// </summary>
    public static Result Optimize(
        IReadOnlyList<Vector3[]> meshes,
        float currentTiltXDeg,
        float currentTiltYDeg,
        bool allowMeshYaw,
        float maxTiltDeg = 60f)
    {
        var bins = BuildNormalBins(meshes);
        float before = bins.Length == 0 ? 0f
            : Risk(bins, TiltToDirection(currentTiltXDeg, currentTiltYDeg));

        if (bins.Length == 0)
            return new Result(currentTiltXDeg, currentTiltYDeg, 0f, 0f, 0f);

        return allowMeshYaw
            ? OptimizeFreeAzimuth(bins, maxTiltDeg, before)
            : OptimizeTiltOnly(bins, maxTiltDeg, before);
    }

    /// <summary>Slice-plane normal for a tilt pair — must match <see cref="AngledPlanarSlicer.Slice"/>.</summary>
    public static Vector3 TiltToDirection(float tiltXDeg, float tiltYDeg)
    {
        float ty = tiltYDeg * MathF.PI / 180f;
        float tx = tiltXDeg * MathF.PI / 180f;
        return Vector3.Normalize(new Vector3(
            MathF.Sin(ty),
            -MathF.Sin(tx) * MathF.Cos(ty),
             MathF.Cos(tx) * MathF.Cos(ty)));
    }

    // -- Search ------------------------------------------------------------

    private static Result OptimizeTiltOnly((Vector3 n, float area)[] bins, float maxTilt, float before)
    {
        float bestX = 0f, bestY = 0f, bestScore = float.MaxValue;

        void Consider(float tx, float ty)
        {
            float score = Scored(bins, tx, ty, maxTilt);
            if (score < bestScore) { bestScore = score; bestX = tx; bestY = ty; }
        }

        for (float tx = -maxTilt; tx <= maxTilt; tx += 3f)
            for (float ty = -maxTilt; ty <= maxTilt; ty += 3f)
                Consider(tx, ty);

        // Local refinement around the coarse winner.
        (float cx, float cy) = (bestX, bestY);
        for (float tx = cx - 3f; tx <= cx + 3f; tx += 0.5f)
            for (float ty = cy - 3f; ty <= cy + 3f; ty += 0.5f)
                Consider(Math.Clamp(tx, -maxTilt, maxTilt), Math.Clamp(ty, -maxTilt, maxTilt));

        return new Result(bestX, bestY, 0f, before, Risk(bins, TiltToDirection(bestX, bestY)));
    }

    private static Result OptimizeFreeAzimuth((Vector3 n, float area)[] bins, float maxTilt, float before)
    {
        float bestTheta = 0f, bestPsi = 0f, bestScore = float.MaxValue;

        void Consider(float thetaDeg, float psiDeg)
        {
            var d = SphereDirection(thetaDeg, psiDeg);
            float score = Objective(bins, d) + TiltPenalty * (thetaDeg / 90f) * (thetaDeg / 90f);
            if (score < bestScore) { bestScore = score; bestTheta = thetaDeg; bestPsi = psiDeg; }
        }

        for (float theta = 0f; theta <= maxTilt; theta += 3f)
            for (float psi = 0f; psi < 360f; psi += 4f)
            {
                Consider(theta, psi);
                if (theta == 0f) break;   // azimuth is meaningless when vertical
            }

        (float ct, float cp) = (bestTheta, bestPsi);
        for (float theta = MathF.Max(0f, ct - 3f); theta <= MathF.Min(maxTilt, ct + 3f); theta += 0.5f)
            for (float psi = cp - 4f; psi <= cp + 4f; psi += 0.5f)
                Consider(theta, (psi + 360f) % 360f);

        // Yaw the mesh so the winning lean azimuth lands on +X → pure Y tilt, X tilt 0.
        // Rotating the mesh by yaw carries its optimal direction with it: we need the direction's
        // azimuth ψ to become 0, so yaw = -ψ (normalised to ±180 for the shortest turn).
        float yaw = -bestPsi;
        while (yaw <= -180f) yaw += 360f;
        while (yaw >   180f) yaw -= 360f;

        return new Result(
            TiltXDeg: 0f,
            TiltYDeg: bestTheta,
            MeshYawDeg: yaw,
            RiskBefore: before,
            RiskAfter: Risk(bins, SphereDirection(bestTheta, bestPsi)));
    }

    private static float Scored((Vector3 n, float area)[] bins, float tx, float ty, float maxTilt)
    {
        var d = TiltToDirection(tx, ty);
        // Tilt-from-vertical for the penalty: angle between d and +Z.
        float tiltDeg = MathF.Acos(Math.Clamp(d.Z, -1f, 1f)) * 180f / MathF.PI;
        // Prefer single-axis tilts: a diagonal must beat the aligned solution by a clear margin,
        // otherwise near-ties on symmetric parts resolve to arbitrary-looking diagonals.
        float minAxis = MathF.Min(MathF.Abs(tx), MathF.Abs(ty));
        return Objective(bins, d)
             + TiltPenalty * (tiltDeg / 90f) * (tiltDeg / 90f)
             + 0.05f * (minAxis / 90f);
    }

    private static Vector3 SphereDirection(float thetaDeg, float psiDeg)
    {
        float th = thetaDeg * MathF.PI / 180f;
        float ps = psiDeg * MathF.PI / 180f;
        return new Vector3(MathF.Sin(th) * MathF.Cos(ps),
                           MathF.Sin(th) * MathF.Sin(ps),
                           MathF.Cos(th));
    }

    // -- Risk model ----------------------------------------------------------

    /// <summary>
    /// Reported metric: area fraction (0..1) at overhangs past the critical angle, weighted by
    /// how far past. This is the "will it actually fail" number shown to the user.
    /// </summary>
    private static float Risk((Vector3 n, float area)[] bins, Vector3 d)
    {
        // Overhang starts where the face normal is more than 90°+critical away from d.
        float cosLimit = MathF.Cos((90f + CriticalOverhangDeg) * MathF.PI / 180f);   // ≈ -0.707

        float total = 0f, risky = 0f;
        foreach (var (n, area) in bins)
        {
            total += area;
            float dot = Vector3.Dot(n, d);
            if (dot < cosLimit)
            {
                // Severity ramps 0→1 from the critical angle to fully downward-facing.
                float severity = (cosLimit - dot) / (cosLimit + 1f);
                risky += area * severity;
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
    private static float Objective((Vector3 n, float area)[] bins, Vector3 d)
    {
        float cosLimit = MathF.Cos((90f + CriticalOverhangDeg) * MathF.PI / 180f);   // ≈ -0.707

        float total = 0f, score = 0f;
        foreach (var (n, area) in bins)
        {
            total += area;
            float dot = Vector3.Dot(n, d);

            if (dot < cosLimit)
            {
                // Past critical: dominant term. Kept nearly flat (1→1.5) on purpose — a steep
                // ramp makes the search chase marginal ceiling-angle reductions with odd
                // diagonal tilts instead of minimising the unprintable area itself.
                score += area * (1f + 0.5f * (cosLimit - dot) / (cosLimit + 1f));
            }
            else if (dot < 0f)
            {
                // Below critical but downward-facing: quadratic margin pressure. Needs a real
                // gradient — a flatter curve leaves a plateau of near-equal directions once all
                // overhangs clear critical, making the winner arbitrary.
                float phi = MathF.Acos(Math.Clamp(dot, -1f, 1f)) * 180f / MathF.PI - 90f;   // 0..critical
                float t = phi / CriticalOverhangDeg;
                score += area * 0.3f * t * t;
            }
        }
        return total > 0f ? score / total : 0f;
    }

    /// <summary>
    /// Bins triangle normals on the sphere with accumulated area. Bed-supported faces —
    /// down-facing triangles whose vertices all sit within a band above the part's lowest
    /// point — are excluded: the bed holds them, they are not overhangs.
    /// </summary>
    private static (Vector3 n, float area)[] BuildNormalBins(IReadOnlyList<Vector3[]> meshes)
    {
        float minZ = float.MaxValue;
        foreach (var verts in meshes)
            foreach (var v in verts)
                if (v.Z < minZ) minZ = v.Z;
        float bedTop = minZ + 3f;   // ~one LFAM layer

        int thetaBins = (int)MathF.Ceiling(180f / BinDeg);
        int phiBins   = (int)MathF.Ceiling(360f / BinDeg);
        var areaAcc   = new float[thetaBins * phiBins];
        var normalAcc = new Vector3[thetaBins * phiBins];

        foreach (var verts in meshes)
        {
            for (int i = 0; i + 2 < verts.Length; i += 3)
            {
                var a = verts[i]; var b = verts[i + 1]; var c = verts[i + 2];
                var cross = Vector3.Cross(b - a, c - a);
                float len = cross.Length();
                if (len < 1e-9f) continue;
                var n = cross / len;
                float area = len * 0.5f;

                // Bed-supported bottom face → not an overhang.
                if (n.Z < -0.7f && a.Z < bedTop && b.Z < bedTop && c.Z < bedTop)
                    continue;

                float theta = MathF.Acos(Math.Clamp(n.Z, -1f, 1f)) * 180f / MathF.PI;
                float phi   = (MathF.Atan2(n.Y, n.X) * 180f / MathF.PI + 360f) % 360f;
                int bi = Math.Min(thetaBins - 1, (int)(theta / BinDeg)) * phiBins
                       + Math.Min(phiBins - 1, (int)(phi / BinDeg));
                areaAcc[bi]   += area;
                normalAcc[bi] += n * area;
            }
        }

        var result = new List<(Vector3, float)>(256);
        for (int i = 0; i < areaAcc.Length; i++)
        {
            if (areaAcc[i] <= 0f) continue;
            var n = normalAcc[i];
            float len = n.Length();
            if (len < 1e-9f) continue;
            result.Add((n / len, areaAcc[i]));
        }
        return result.ToArray();
    }
}
