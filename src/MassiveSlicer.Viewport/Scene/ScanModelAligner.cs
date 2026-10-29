using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Moves a model onto a scan. The scan is the milling position and is never written.
/// A locate step finds a general pose when the model starts far from the print,
/// including a large pitch or roll. Yaw about Z is not the only turn it tries.
/// The fine match then requires the surfaces to be within a few centimetres, and it
/// leaves <see cref="ScanModelAlignRequest.StockMm"/> of material outside the model.
/// Max shift and max rotation bound that fine match only.
/// A weak fine match returns <see cref="ScanModelAlignResult.Accepted"/> false and an identity delta.
/// </summary>
public static class ScanModelAligner
{
    public const int MaxSamplePoints = 2500;

    /// <summary>
    /// A merged scan is tens of millions of triangles. 2500 samples sit about a
    /// centimetre apart, which reads as stock error on a real print. This many
    /// samples keep a 2 mm tolerance on the surface.
    /// </summary>
    public const int MaxScanSamplePoints = 20_000;
    private const int MaxIterations = 16;
    private const float ConvergenceMm = 0.05f;

    /// <summary>Fine correspondence, in mm. The locate step is not limited by this.</summary>
    private const float FinePairMm = 40f;

    /// <summary>
    /// A shorter vote stays on the model normals, so one scanned face cannot slide
    /// the part along itself. Longer than this, the model was moved and the vote
    /// is the way back, including across that face.
    /// </summary>
    private const float FarMoveMm = 400f;

    public readonly record struct ScanModelAlignRequest(
        float StockMm,
        float ToleranceMm,
        float MinInlierFraction,
        float MaxTranslationMm,
        float MaxRotationDeg);

    public readonly record struct ScanModelAlignResult(
        bool Accepted,
        Matrix4 ModelWorldDelta,
        string Message,
        float InlierFraction,
        float MedianAbsResidualMm,
        float MedianTangentMm,
        float TranslationMm,
        float RotationDeg);

    /// <summary>
    /// Row-vector delta for the model: <c>p' = TransformPoint(p, delta)</c>.
    /// Does not write <paramref name="modelPoints"/>, <paramref name="modelNormals"/>,
    /// or <paramref name="scanPoints"/>.
    /// </summary>
    public static ScanModelAlignResult Align(
        IReadOnlyList<Vector3> modelPoints,
        IReadOnlyList<Vector3> modelNormals,
        IReadOnlyList<Vector3> scanPoints,
        ScanModelAlignRequest request)
    {
        if (modelPoints.Count < 12 || modelNormals.Count != modelPoints.Count || scanPoints.Count < 12)
        {
            return Reject("Not applied. Need a model and a scan with surface geometry.");
        }

        float stock = MathF.Max(0f, request.StockMm);
        float tol = MathF.Max(0.05f, request.ToleranceMm);
        float pair = MathF.Max(tol, FinePairMm);
        float minInlier = Math.Clamp(request.MinInlierFraction, 0.05f, 0.95f);
        float maxShift = MathF.Max(1f, request.MaxTranslationMm);
        float maxTurn = MathF.Max(1f, request.MaxRotationDeg);

        var points = new Vector3[modelPoints.Count];
        var normals = new Vector3[modelPoints.Count];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = modelPoints[i];
            normals[i] = modelNormals[i];
            if (normals[i].LengthSquared > 1e-8f)
                normals[i].Normalize();
        }

        var scanGrid = new ScanGrid(scanPoints, pair);
        var accum = Locate(points, normals, scanPoints, scanGrid, pair);
        if (accum != Matrix4.Identity)
            Apply(points, normals, accum);

        // General orientation once the model is on the print. Max shift and max
        // rotation below bound only the fine match that follows.
        Refine(points, normals, scanGrid, stock, pair, turnCap: 90f, shiftCap: MathF.Max(pair * 3f, 100f), ref accum);

        var fine = Matrix4.Identity;
        var fineCentroid = Centroid(points);
        for (int iter = 0; iter < MaxIterations; iter++)
        {
            if (!TryStep(points, normals, scanGrid, stock, pair, out var step, out float stepMm))
                break;

            Apply(points, normals, step);
            fine *= step;
            accum *= step;
            if (stepMm < ConvergenceMm)
                break;
        }

        var measure = Measure(points, normals, scanPoints, scanGrid, stock, pair);
        float shift = CentroidMotion(modelPoints, accum);
        float turn = RotationDegrees(accum);
        float fineShift = (TransformPoint(fineCentroid, fine) - fineCentroid).Length;
        float fineTurn = RotationDegrees(fine);
        if (measure.Inliers < minInlier)
        {
            return Reject(
                $"Not applied. Only {measure.Inliers * 100f:0}% of the scan lies on the model. The rest of the model is ignored.",
                measure, shift, turn);
        }

        if (measure.MedianAbs > tol)
        {
            return Reject(
                $"Not applied. Stock error on the scanned face is {measure.MedianAbs:0.0} mm (tolerance {tol:0.0} mm). That face does not match this stock thickness.",
                measure, shift, turn);
        }

        float tangentLimit = MathF.Max(4f, tol * 2f);
        if (measure.OneFace)
            tangentLimit = MathF.Max(tangentLimit, 40f);
        if (measure.MedianTangent > tangentLimit)
        {
            return Reject(
                $"Not applied. The model sits {measure.MedianTangent:0.0} mm off the side of the scan, so the match would slide.",
                measure, shift, turn);
        }

        if (fineShift > maxShift || fineTurn > maxTurn)
        {
            return Reject(
                $"Not applied. The fine match wants to move {fineShift:0} mm and turn {fineTurn:0}°, past the shift and rotation limits.",
                measure, fineShift, fineTurn);
        }

        return new ScanModelAlignResult(
            true,
            accum,
            $"Stock error {measure.MedianAbs:0.0} mm, shift {shift:0.0} mm, turn {turn:0.0}°.",
            measure.Inliers,
            measure.MedianAbs,
            measure.MedianTangent,
            shift,
            turn);
    }

    /// <summary>
    /// World nudge about <paramref name="pivot"/>: rotate in X then Y then Z, then translate.
    /// Row-vector, same convention as <see cref="TransformPoint"/>.
    /// </summary>
    public static Matrix4 WorldNudge(Vector3 pivot, Vector3 offsetMm, Vector3 rotateDeg)
    {
        var rot = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(rotateDeg.X))
                * Matrix4.CreateRotationY(MathHelper.DegreesToRadians(rotateDeg.Y))
                * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(rotateDeg.Z));
        var about = Matrix4.CreateTranslation(-pivot) * rot * Matrix4.CreateTranslation(pivot);
        return about * Matrix4.CreateTranslation(offsetMm);
    }

    /// <summary>
    /// Stride-samples every mesh. The budget is split by vertex count, so a long
    /// part stored as several meshes is not represented by the first mesh only.
    /// </summary>
    public static void CollectBalancedSamples(
        IReadOnlyList<MeshData> meshes,
        IReadOnlyList<Matrix4> worlds,
        int maxPoints,
        List<Vector3> points,
        List<Vector3>? normals)
    {
        if (maxPoints < 1 || meshes.Count == 0 || meshes.Count != worlds.Count) return;

        long total = 0;
        for (int i = 0; i < meshes.Count; i++)
        {
            int count = meshes[i].Positions.Length;
            if (count >= 3) total += count;
        }

        if (total < 1) return;

        var shares = new int[meshes.Count];
        int used = 0;
        for (int i = 0; i < meshes.Count; i++)
        {
            int count = meshes[i].Positions.Length;
            if (count < 3) continue;
            int share = (int)(maxPoints * (long)count / total);
            if (share > count) share = count;
            shares[i] = share;
            used += share;
        }

        while (used < maxPoints)
        {
            int best = -1;
            int room = 0;
            for (int i = 0; i < meshes.Count; i++)
            {
                int left = meshes[i].Positions.Length - shares[i];
                if (meshes[i].Positions.Length < 3 || left <= room) continue;
                room = left;
                best = i;
            }

            if (best < 0) break;
            shares[best]++;
            used++;
        }

        for (int i = 0; i < meshes.Count; i++)
        {
            if (shares[i] < 1) continue;
            CollectSamples(meshes[i], worlds[i], points.Count + shares[i], points, normals);
        }
    }

    /// <summary>Stride-samples a mesh in world space. Does not copy the source arrays.</summary>
    public static void CollectSamples(
        MeshData mesh, Matrix4 world, int maxPoints,
        List<Vector3> points, List<Vector3>? normals)
    {
        int count = mesh.Positions.Length;
        if (count < 3 || maxPoints < 1) return;
        int room = maxPoints - points.Count;
        if (room < 1) return;
        int step = Math.Max(1, count / room);
        bool wantNormals = normals is not null && mesh.Normals.Length == count;
        for (int i = 0; i < count && points.Count < maxPoints; i += step)
        {
            points.Add(TransformPoint(mesh.Positions[i], world));
            if (wantNormals)
                normals!.Add(TransformNormal(mesh.Normals[i], world));
        }
    }

    public static Vector3 TransformPoint(Vector3 p, Matrix4 m)
        => new(
            p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41,
            p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42,
            p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43);

    public static Vector3 TransformNormal(Vector3 n, Matrix4 m)
    {
        var x = n.X * m.M11 + n.Y * m.M21 + n.Z * m.M31;
        var y = n.X * m.M12 + n.Y * m.M22 + n.Z * m.M32;
        var z = n.X * m.M13 + n.Y * m.M23 + n.Z * m.M33;
        float len = MathF.Sqrt(x * x + y * y + z * z);
        return len > 1e-6f ? new Vector3(x / len, y / len, z / len) : n;
    }

    private static bool TryStep(
        Vector3[] points, Vector3[] normals, ScanGrid scan,
        float stock, float pair, out Matrix4 step, out float stepMm)
    {
        step = Matrix4.Identity;
        stepMm = 0f;
        var centroid = Centroid(points);
        float length = 0f;
        int counted = 0;
        for (int i = 0; i < points.Length; i++)
        {
            if (normals[i].LengthSquared < 1e-8f) continue;
            length += (points[i] - centroid).Length;
            counted++;
        }
        if (counted < 12) return false;
        length = MathF.Max(1f, length / counted);

        var ata = new double[6, 6];
        var atb = new double[6];
        int pairs = 0;
        for (int i = 0; i < points.Length; i++)
        {
            var n = normals[i];
            if (n.LengthSquared < 1e-8f) continue;
            if (!scan.TryNearest(points[i], out var q)) continue;
            if (!IsScannedFace(points[i], n, q, pair, out float along, out _)) continue;

            var arm = Vector3.Cross(points[i] - centroid, n) / length;
            double[] row = [arm.X, arm.Y, arm.Z, n.X, n.Y, n.Z];
            double rhs = along - stock;
            for (int r = 0; r < 6; r++)
            {
                atb[r] += row[r] * rhs;
                for (int c = 0; c < 6; c++)
                    ata[r, c] += row[r] * row[c];
            }
            pairs++;
        }

        if (pairs < 12) return false;
        double ridge = 0.05 * pairs;
        for (int d = 0; d < 6; d++)
            ata[d, d] += ridge;
        if (!Solve(ata, atb, out var x))
            return false;

        var omega = new Vector3((float)x[0], (float)x[1], (float)x[2]) / length;
        var t = new Vector3((float)x[3], (float)x[4], (float)x[5]);
        if (float.IsNaN(t.X) || t.Length > pair * 2f || omega.Length > 0.5f)
            return false;

        step = RigidAbout(centroid, omega, t);
        stepMm = t.Length + omega.Length * length;
        return stepMm > 1e-6f;
    }

    private readonly record struct MeasureResult(float Inliers, float MedianAbs, float MedianTangent, bool OneFace);

    /// <summary>
    /// True when the scan point sits in front of this model face, not off to the side.
    /// A scan of one face ignores the rest of the model.
    /// </summary>
    private static bool IsScannedFace(
        Vector3 point, Vector3 normal, Vector3 scanPoint, float pair, out float along, out float side)
    {
        var gap = scanPoint - point;
        along = Vector3.Dot(gap, normal);
        side = (gap - normal * along).Length;
        // Far behind this surface, or off to the side. The other faces of the model are ignored.
        if (along < -MathF.Max(8f, pair * 0.5f)) return false;
        float limit = MathF.Max(12f, MathF.Abs(along) * 1.2f);
        return side <= limit;
    }

    private static MeasureResult Measure(
        Vector3[] points, Vector3[] normals, IReadOnlyList<Vector3> scan, ScanGrid grid, float stock, float pair)
    {
        var abs = new List<float>();
        var tangent = new List<float>();
        var facing = new List<Vector3>();
        for (int i = 0; i < points.Length; i++)
        {
            var n = normals[i];
            if (n.LengthSquared < 1e-8f) continue;
            if (!grid.TryNearest(points[i], out var q)) continue;
            if (!IsScannedFace(points[i], n, q, pair, out float along, out float side)) continue;
            abs.Add(MathF.Abs(along - stock));
            tangent.Add(side);
            facing.Add(n);
        }

        float covered = ScanCoverage(points, normals, scan, pair);
        if (abs.Count == 0)
            return new MeasureResult(covered, float.MaxValue, float.MaxValue, false);
        return new MeasureResult(covered, Median(abs), Median(tangent), IsOneFace(facing));
    }

    private static bool IsOneFace(List<Vector3> normals)
    {
        if (normals.Count < 12) return false;
        var sum = Vector3.Zero;
        for (int i = 0; i < normals.Count; i++)
            sum += normals[i];
        if (sum.LengthSquared < 1e-6f) return false;
        sum.Normalize();
        int aligned = 0;
        for (int i = 0; i < normals.Count; i++)
        {
            if (Vector3.Dot(normals[i], sum) > 0.85f)
                aligned++;
        }

        return aligned > normals.Count * 0.8f;
    }

    private static float ScanCoverage(
        Vector3[] points, Vector3[] normals, IReadOnlyList<Vector3> scan, float pair)
    {
        if (scan.Count == 0) return 0f;
        float cell = MathF.Max(pair, 1f);
        float pairSq = pair * pair;
        var cells = new Dictionary<(int X, int Y, int Z), List<int>>();
        for (int i = 0; i < points.Length; i++)
        {
            if (normals[i].LengthSquared < 1e-8f) continue;
            var key = (
                (int)MathF.Floor(points[i].X / cell),
                (int)MathF.Floor(points[i].Y / cell),
                (int)MathF.Floor(points[i].Z / cell));
            if (!cells.TryGetValue(key, out var list))
            {
                list = new List<int>();
                cells[key] = list;
            }
            list.Add(i);
        }

        int covered = 0;
        for (int s = 0; s < scan.Count; s++)
        {
            var origin = (
                (int)MathF.Floor(scan[s].X / cell),
                (int)MathF.Floor(scan[s].Y / cell),
                (int)MathF.Floor(scan[s].Z / cell));
            bool hit = false;
            for (int x = -1; x <= 1 && !hit; x++)
            {
                for (int y = -1; y <= 1 && !hit; y++)
                {
                    for (int z = -1; z <= 1 && !hit; z++)
                    {
                        if (!cells.TryGetValue((origin.Item1 + x, origin.Item2 + y, origin.Item3 + z), out var list))
                            continue;
                        for (int k = 0; k < list.Count; k++)
                        {
                            int i = list[k];
                            if ((points[i] - scan[s]).LengthSquared > pairSq) continue;
                            if (!IsScannedFace(points[i], normals[i], scan[s], pair, out _, out _)) continue;
                            hit = true;
                            break;
                        }
                    }
                }
            }
            if (hit) covered++;
        }

        return covered / (float)scan.Count;
    }

    private static ScanModelAlignResult Reject(string message, MeasureResult measure = default, float shift = 0f, float turn = 0f)
        => new(false, Matrix4.Identity, message, measure.Inliers, measure.MedianAbs, measure.MedianTangent, shift, turn);

    private static void Apply(Vector3[] points, Vector3[] normals, Matrix4 delta)
    {
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = TransformPoint(points[i], delta);
            normals[i] = TransformNormal(normals[i], delta);
        }
    }

    /// <summary>
    /// General pose: a translation, yaw about Z, and a full turn when the part is
    /// pitched or rolled. A one-face scan cannot slide sideways.
    /// Returns identity when the model is already as close as a locate can make it.
    /// </summary>
    private static Matrix4 Locate(
        Vector3[] points, Vector3[] normals, IReadOnlyList<Vector3> scan, ScanGrid grid, float pair)
    {
        var here = ScorePose(points, Matrix4.Identity, grid);
        var zero = TranslationPose(points, normals, scan, pair, grid, Matrix4.Identity);
        var best = zero;
        var pivot = Centroid(points);
        if (HasHorizontalNormals(normals))
        {
            for (int k = 1; k < 24; k++)
            {
                var yaw = WorldNudge(pivot, Vector3.Zero, new Vector3(0f, 0f, k * 15f));
                var cand = TranslationPose(points, normals, scan, pair, grid, yaw);
                if (Clearer(cand, best))
                    best = cand;
            }
        }

        var full = SearchFullTurn(points, normals, scan, grid, pair, pivot);
        if (Clearer(full, best))
            best = full;

        if (best.Inliers < 0.20f || !Clearer(best, here))
            return Matrix4.Identity;
        return best.Delta;
    }

    /// <summary>
    /// Pitch and roll as well as yaw. A 45° grid, then 11° around the winner.
    /// The wide bin only ranks those turns. The seated pose is scored again at
    /// the fine pair, so a short one-face slide still cannot sneak through.
    /// </summary>
    private static PoseScore SearchFullTurn(
        Vector3[] points, Vector3[] normals, IReadOnlyList<Vector3> scan,
        ScanGrid fine, float pair, Vector3 pivot)
    {
        // A scanned face is a sheet. Stand the model's face on that sheet first.
        // The euler grid below is for a scan that shows more than one side.
        if (TrySheetAxis(scan, out var sheetAxis))
        {
            var seated = SeatOnSheet(points, normals, scan, fine, pair, pivot, sheetAxis);
            if (seated.Inliers >= 0.20f && seated.MedianMm < pair)
                return seated;
        }

        float radius = MeanRadius(points);
        float residual = radius * MathF.Sin(MathHelper.DegreesToRadians(23f));
        float coarse = MathF.Max(pair, MathF.Min(400f, residual * 1.4f));
        var rank = coarse > pair + 1f ? new ScanGrid(scan, coarse) : fine;

        var bestAng = Vector3.Zero;
        var best = ScorePose(points, Matrix4.Identity, rank);
        foreach (var ang in CoarseTurns())
        {
            var cand = TranslationPose(
                points, normals, scan, coarse, rank, WorldNudge(pivot, Vector3.Zero, ang));
            if (!BetterRank(cand, best)) continue;
            best = cand;
            bestAng = ang;
        }

        for (int ix = -2; ix <= 2; ix++)
        {
            for (int iy = -2; iy <= 2; iy++)
            {
                for (int iz = -2; iz <= 2; iz++)
                {
                    if (ix == 0 && iy == 0 && iz == 0) continue;
                    var ang = bestAng + new Vector3(ix * 11f, iy * 11f, iz * 11f);
                    var cand = TranslationPose(
                        points, normals, scan, coarse, rank, WorldNudge(pivot, Vector3.Zero, ang));
                    if (!BetterRank(cand, best)) continue;
                    best = cand;
                    bestAng = ang;
                }
            }
        }

        if (best.Inliers < 0.12f)
            return ScorePose(points, Matrix4.Identity, fine);

        var pulled = Pull(points, normals, best.Delta, rank, stock: 0f, coarse);
        var rotation = RotationOf(pulled);
        return TranslationPose(points, normals, scan, pair, fine, rotation);
    }

    /// <summary>
    /// Maps each common model normal onto the sheet, then spins about that normal.
    /// On a long part the true twist is only a degree or two wide, so a 15° sample
    /// is ranked with a bin as wide as that arc, then tightened. The scan's outline
    /// is what separates that twist from another face of the same spacing.
    /// </summary>
    private static PoseScore SeatOnSheet(
        Vector3[] points, Vector3[] normals, IReadOnlyList<Vector3> scan,
        ScanGrid fine, float pair, Vector3 pivot, Vector3 axis)
    {
        var faces = FaceNormals(normals, 8);
        float reach = MaxRadius(points) * MathF.Sin(MathHelper.DegreesToRadians(8f));
        float capture = MathF.Max(pair, MathF.Min(400f, reach));
        float coarseGate = MathF.Max(pair, MathF.Min(400f, capture * 1.25f));
        float mid = MathF.Max(pair, capture * 0.45f);

        var best = ScorePose(points, Matrix4.Identity, fine);
        float bestCover = -1f;
        float bestWorst = float.MaxValue;
        bool any = false;

        bool Prefer(float cover, float worst, bool have)
        {
            if (cover < 0.20f) return false;
            if (!have) return true;
            if (cover > bestCover + 0.02f) return true;
            if (bestCover > cover + 0.02f) return false;
            return worst + 1f < bestWorst;
        }

        foreach (var face in faces)
        {
            var members = new List<int>();
            for (int i = 0; i < normals.Length; i++)
            {
                if (Vector3.Dot(normals[i], face) > 0.85f)
                    members.Add(i);
            }

            if (members.Count < 8) continue;

            for (int sign = -1; sign <= 1; sign += 2)
            {
                var target = axis * sign;
                var align = RotationFromTo(face, target);

                bool Score(float spin, float bin, float gate, out float cover, out float worst, out Matrix4 delta)
                {
                    var pre = AboutPivot(
                        pivot, align * Matrix4.CreateFromAxisAngle(target, MathHelper.DegreesToRadians(spin)));
                    if (!TryFaceTranslation(points, normals, scan, members, bin, pre, out var shift))
                    {
                        cover = 0f;
                        worst = float.MaxValue;
                        delta = pre;
                        return false;
                    }

                    delta = pre * Matrix4.CreateTranslation(shift);
                    MeasureFaceOverlap(points, members, scan, delta, gate, out cover, out worst);
                    return true;
                }

                bool Pick(float origin, float step, int radius, float bin, float gate, out float spin, out Matrix4 delta)
                {
                    spin = origin;
                    delta = Matrix4.Identity;
                    float cover = -1f;
                    float worst = float.MaxValue;
                    bool hit = false;
                    for (int s = -radius; s <= radius; s++)
                    {
                        if (!Score(origin + s * step, bin, gate, out float candCover, out float candWorst, out var candDelta))
                            continue;
                        if (hit && candCover <= cover + 0.02f && (cover > candCover + 0.02f || candWorst >= worst))
                            continue;
                        hit = true;
                        cover = candCover;
                        worst = candWorst;
                        spin = origin + s * step;
                        delta = candDelta;
                    }

                    return hit;
                }

                if (!Pick(0f, 15f, 12, capture, coarseGate, out float twist, out _))
                    continue;
                if (!Pick(twist, 2f, 4, mid, mid, out twist, out _))
                    continue;
                if (!Pick(twist, 1f, 2, pair, pair, out twist, out _))
                    continue;
                // A degree of twist at the end of a long face is several millimetres.
                // Point-to-plane will not pull it back, because every normal on this
                // face points the same way.
                if (!Pick(twist, 0.1f, 6, pair, pair, out twist, out _))
                    continue;
                if (!Pick(twist, 0.02f, 6, pair, pair, out twist, out var seated))
                    continue;
                if (!Score(twist, pair, pair, out float tightCover, out float tightWorst, out seated))
                    continue;
                if (!Prefer(tightCover, tightWorst, any)) continue;

                var pose = ScorePose(points, seated, fine);
                if (pose.Inliers < 0.12f) continue;
                any = true;
                best = pose;
                bestCover = tightCover;
                bestWorst = tightWorst;
            }
        }

        return any ? best : ScorePose(points, Matrix4.Identity, fine);
    }

    /// <summary>
    /// Translation voted only by the face being seated, so the other sides of the
    /// model do not pull the bin. A short vote is kept along that face's normal.
    /// </summary>
    private static bool TryFaceTranslation(
        Vector3[] model, Vector3[] normals, IReadOnlyList<Vector3> scan,
        List<int> facePoints, float bin, Matrix4 pre, out Vector3 translation)
    {
        translation = Vector3.Zero;
        if (facePoints.Count < 8 || bin < 1f) return false;

        int modelStep = Math.Max(1, facePoints.Count / 80);
        int scanStep = Math.Max(1, scan.Count / 160);
        var votes = new Dictionary<(int X, int Y, int Z), (int Count, Vector3 Sum)>();
        int samples = 0;
        for (int n = 0; n < facePoints.Count; n += modelStep)
        {
            var p = TransformPoint(model[facePoints[n]], pre);
            samples++;
            for (int j = 0; j < scan.Count; j += scanStep)
            {
                var t = scan[j] - p;
                var key = (
                    (int)MathF.Floor(t.X / bin + 0.5f),
                    (int)MathF.Floor(t.Y / bin + 0.5f),
                    (int)MathF.Floor(t.Z / bin + 0.5f));
                votes.TryGetValue(key, out var slot);
                votes[key] = (slot.Count + 1, slot.Sum + t);
            }
        }

        int bestCount = 7;
        Vector3 bestSum = Vector3.Zero;
        foreach (var slot in votes.Values)
        {
            if (slot.Count <= bestCount) continue;
            bestCount = slot.Count;
            bestSum = slot.Sum;
        }

        if (bestCount < 8 || samples < 8)
            return false;

        var raw = bestSum / bestCount;
        if (float.IsNaN(raw.X) || float.IsInfinity(raw.X))
            return false;

        if (raw.Length > FarMoveMm)
        {
            translation = raw;
            return translation.LengthSquared > 1f;
        }

        var turned = new Vector3[facePoints.Count];
        for (int i = 0; i < facePoints.Count; i++)
            turned[i] = TransformNormal(normals[facePoints[i]], pre);
        translation = ProjectToConstrained(raw, turned);
        return translation.LengthSquared > 1f;
    }

    /// <summary>
    /// Fraction of the scan that lands on this face, and the farthest scan point.
    /// Euclidean on purpose: a near miss along the face is still the right twist,
    /// and a tab the face does not own stays far.
    /// </summary>
    private static void MeasureFaceOverlap(
        Vector3[] points, List<int> facePoints, IReadOnlyList<Vector3> scan,
        Matrix4 delta, float gate, out float cover, out float worst)
    {
        cover = 0f;
        worst = float.MaxValue;
        if (facePoints.Count == 0 || scan.Count == 0) return;

        int modelStep = Math.Max(1, facePoints.Count / 200);
        int scanStep = Math.Max(1, scan.Count / 400);
        var posed = new List<Vector3>(facePoints.Count / modelStep + 1);
        for (int n = 0; n < facePoints.Count; n += modelStep)
            posed.Add(TransformPoint(points[facePoints[n]], delta));

        int seen = 0;
        int hit = 0;
        float far = 0f;
        float gateSq = gate * gate;
        for (int s = 0; s < scan.Count; s += scanStep)
        {
            seen++;
            float best = float.MaxValue;
            var q = scan[s];
            for (int i = 0; i < posed.Count; i++)
            {
                float d = (posed[i] - q).LengthSquared;
                if (d < best) best = d;
            }

            if (best <= gateSq) hit++;
            float dist = MathF.Sqrt(best);
            if (dist > far) far = dist;
        }

        cover = seen == 0 ? 0f : hit / (float)seen;
        worst = far;
    }

    private static List<Vector3> FaceNormals(Vector3[] normals, int max)
    {
        const int tiltBins = 9;
        const int panBins = 18;
        var count = new int[tiltBins * panBins];
        var sum = new Vector3[tiltBins * panBins];
        for (int i = 0; i < normals.Length; i++)
        {
            var n = normals[i];
            if (n.LengthSquared < 1e-8f) continue;
            int tilt = (int)(MathF.Acos(Math.Clamp(n.Z, -1f, 1f)) / MathF.PI * tiltBins);
            if (tilt >= tiltBins) tilt = tiltBins - 1;
            if (tilt < 0) tilt = 0;
            int pan = (int)((MathF.Atan2(n.Y, n.X) + MathF.PI) / (2f * MathF.PI) * panBins);
            if (pan >= panBins) pan = 0;
            if (pan < 0) pan = 0;
            int bin = tilt * panBins + pan;
            count[bin]++;
            sum[bin] += n;
        }

        var faces = new List<Vector3>(max);
        for (int picked = 0; picked < max; picked++)
        {
            int best = -1;
            for (int i = 0; i < count.Length; i++)
            {
                if (count[i] < 8) continue;
                if (best < 0 || count[i] > count[best]) best = i;
            }

            if (best < 0) break;
            var face = sum[best] / count[best];
            if (face.LengthSquared > 1e-8f)
            {
                face.Normalize();
                faces.Add(face);
            }

            count[best] = 0;
        }

        return faces;
    }

    private static bool TrySheetAxis(IReadOnlyList<Vector3> scan, out Vector3 axis)
    {
        axis = Vector3.Zero;
        if (scan.Count < 12) return false;
        var center = Centroid(scan);
        double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        for (int i = 0; i < scan.Count; i++)
        {
            double x = scan[i].X - center.X;
            double y = scan[i].Y - center.Y;
            double z = scan[i].Z - center.Z;
            xx += x * x;
            xy += x * y;
            xz += x * z;
            yy += y * y;
            yz += y * z;
            zz += z * z;
        }

        double inv = 1.0 / scan.Count;
        var a = new double[3, 3]
        {
            { xx * inv, xy * inv, xz * inv },
            { xy * inv, yy * inv, yz * inv },
            { xz * inv, yz * inv, zz * inv },
        };
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        Jacobi3(a, v);
        int thin = 0;
        int wide = 0;
        for (int i = 1; i < 3; i++)
        {
            if (a[i, i] < a[thin, thin]) thin = i;
            if (a[i, i] > a[wide, wide]) wide = i;
        }

        if (a[wide, wide] < 1e-4 || a[thin, thin] > 0.15 * a[wide, wide])
            return false;
        axis = new Vector3((float)v[0, thin], (float)v[1, thin], (float)v[2, thin]);
        if (axis.LengthSquared < 1e-8f) return false;
        axis.Normalize();
        return true;
    }

    private static void Jacobi3(double[,] a, double[,] v)
    {
        for (int sweep = 0; sweep < 12; sweep++)
        {
            int p = 0;
            int q = 1;
            double best = Math.Abs(a[0, 1]);
            if (Math.Abs(a[0, 2]) > best)
            {
                best = Math.Abs(a[0, 2]);
                p = 0;
                q = 2;
            }

            if (Math.Abs(a[1, 2]) > best)
            {
                best = Math.Abs(a[1, 2]);
                p = 1;
                q = 2;
            }

            if (best < 1e-12) break;
            double app = a[p, p];
            double aqq = a[q, q];
            double apq = a[p, q];
            double tau = (aqq - app) / (2.0 * apq);
            double sign = tau >= 0 ? 1.0 : -1.0;
            double t = sign / (Math.Abs(tau) + Math.Sqrt(1.0 + tau * tau));
            double c = 1.0 / Math.Sqrt(1.0 + t * t);
            double s = t * c;
            a[p, p] = app - t * apq;
            a[q, q] = aqq + t * apq;
            a[p, q] = 0;
            a[q, p] = 0;
            int r = 3 - p - q;
            double arp = a[r, p];
            double arq = a[r, q];
            double rp = c * arp - s * arq;
            double rq = s * arp + c * arq;
            a[r, p] = rp;
            a[p, r] = rp;
            a[r, q] = rq;
            a[q, r] = rq;
            for (int row = 0; row < 3; row++)
            {
                double vip = v[row, p];
                double viq = v[row, q];
                v[row, p] = c * vip - s * viq;
                v[row, q] = s * vip + c * viq;
            }
        }
    }

    private static Matrix4 RotationFromTo(Vector3 from, Vector3 to)
    {
        if (from.LengthSquared < 1e-8f || to.LengthSquared < 1e-8f)
            return Matrix4.Identity;
        from.Normalize();
        to.Normalize();
        float dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot > 0.9999f) return Matrix4.Identity;
        if (dot < -0.9999f)
        {
            var pole = MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            var axis = Vector3.Cross(from, pole);
            axis.Normalize();
            return Matrix4.CreateFromAxisAngle(axis, MathF.PI);
        }

        var cross = Vector3.Cross(from, to);
        cross.Normalize();
        return Matrix4.CreateFromAxisAngle(cross, MathF.Acos(dot));
    }

    private static Matrix4 AboutPivot(Vector3 pivot, Matrix4 rotation)
        => Matrix4.CreateTranslation(-pivot) * rotation * Matrix4.CreateTranslation(pivot);

    private static bool BetterRank(PoseScore candidate, PoseScore current)
    {
        if (candidate.Inliers < 0.12f) return false;
        if (candidate.MedianMm + 15f < current.MedianMm) return true;
        if (current.MedianMm + 15f < candidate.MedianMm) return false;
        return candidate.Inliers > current.Inliers + 0.02f;
    }

    private static IEnumerable<Vector3> CoarseTurns()
    {
        float[] pitch = [-90f, -45f, 0f, 45f, 90f];
        float[] spin = [0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f];
        foreach (float x in pitch)
        {
            foreach (float y in spin)
            {
                foreach (float z in spin)
                {
                    if (x == 0f && y == 0f && z == 0f) continue;
                    yield return new Vector3(x, y, z);
                }
            }
        }
    }

    private static Matrix4 Pull(
        Vector3[] srcPoints, Vector3[] srcNormals, Matrix4 delta,
        ScanGrid grid, float stock, float pair)
    {
        var points = (Vector3[])srcPoints.Clone();
        var normals = (Vector3[])srcNormals.Clone();
        Apply(points, normals, delta);
        var accum = delta;
        Refine(points, normals, grid, stock, pair, turnCap: 50f, shiftCap: MathF.Max(pair * 3f, 800f), ref accum);
        return accum;
    }

    private static Matrix4 RotationOf(Matrix4 m)
    {
        m.M41 = 0f;
        m.M42 = 0f;
        m.M43 = 0f;
        m.M44 = 1f;
        return m;
    }

    private static float MeanRadius(Vector3[] points)
    {
        var center = Centroid(points);
        float sum = 0f;
        for (int i = 0; i < points.Length; i++)
            sum += (points[i] - center).Length;
        return MathF.Max(1f, sum / Math.Max(1, points.Length));
    }

    private static float MaxRadius(Vector3[] points)
    {
        var center = Centroid(points);
        float max = 1f;
        for (int i = 0; i < points.Length; i++)
            max = MathF.Max(max, (points[i] - center).Length);
        return max;
    }

    private readonly record struct PoseScore(Matrix4 Delta, float Inliers, float MedianMm);

    private static bool Clearer(PoseScore candidate, PoseScore current)
        => candidate.Inliers > current.Inliers + 0.08f
           || (candidate.Inliers >= current.Inliers - 0.08f && candidate.MedianMm + 2f < current.MedianMm);

    private static PoseScore TranslationPose(
        Vector3[] points, Vector3[] normals, IReadOnlyList<Vector3> scan,
        float pair, ScanGrid grid, Matrix4 pre)
    {
        if (!TryHoughTranslation(points, normals, scan, pair, pre, out var shift))
            return ScorePose(points, pre, grid);

        var delta = pre * Matrix4.CreateTranslation(shift);
        return ScorePose(points, delta, grid);
    }

    private static bool TryHoughTranslation(
        Vector3[] model, Vector3[] normals, IReadOnlyList<Vector3> scan,
        float bin, Matrix4 pre, out Vector3 translation)
    {
        translation = Vector3.Zero;
        int modelStep = Math.Max(1, model.Length / 80);
        int scanStep = Math.Max(1, scan.Count / 160);
        var votes = new Dictionary<(int X, int Y, int Z), (int Count, Vector3 Sum)>();
        int samples = 0;
        for (int i = 0; i < model.Length; i += modelStep)
        {
            var p = TransformPoint(model[i], pre);
            samples++;
            for (int j = 0; j < scan.Count; j += scanStep)
            {
                var t = scan[j] - p;
                var key = (
                    (int)MathF.Floor(t.X / bin + 0.5f),
                    (int)MathF.Floor(t.Y / bin + 0.5f),
                    (int)MathF.Floor(t.Z / bin + 0.5f));
                votes.TryGetValue(key, out var slot);
                votes[key] = (slot.Count + 1, slot.Sum + t);
            }
        }

        int bestCount = 7;
        Vector3 bestSum = Vector3.Zero;
        foreach (var slot in votes.Values)
        {
            if (slot.Count <= bestCount) continue;
            bestCount = slot.Count;
            bestSum = slot.Sum;
        }

        if (bestCount < 8 || samples < 8)
            return false;

        var raw = bestSum / bestCount;
        if (float.IsNaN(raw.X) || float.IsInfinity(raw.X))
            return false;

        var turned = new Vector3[normals.Length];
        for (int i = 0; i < normals.Length; i++)
            turned[i] = TransformNormal(normals[i], pre);
        translation = raw.Length > FarMoveMm ? raw : ProjectToConstrained(raw, turned);
        return translation.LengthSquared > 1f;
    }

    private static void Refine(
        Vector3[] points, Vector3[] normals, ScanGrid scan,
        float stock, float pair, float turnCap, float shiftCap, ref Matrix4 accum)
    {
        var origin = Centroid(points);
        var spun = Matrix4.Identity;
        for (int iter = 0; iter < MaxIterations; iter++)
        {
            if (!TryStep(points, normals, scan, stock, pair, out var step, out float stepMm))
                break;
            var next = spun * step;
            float turn = RotationDegrees(next);
            float moved = (TransformPoint(origin, next) - origin).Length;
            if (turn > turnCap || moved > shiftCap)
                break;
            Apply(points, normals, step);
            spun = next;
            accum *= step;
            if (stepMm < ConvergenceMm)
                break;
        }
    }

    private static PoseScore ScorePose(Vector3[] points, Matrix4 delta, ScanGrid grid)
    {
        var distances = new List<float>();
        for (int i = 0; i < points.Length; i++)
        {
            var p = TransformPoint(points[i], delta);
            if (!grid.TryNearest(p, out var q)) continue;
            distances.Add((q - p).Length);
        }

        float inliers = distances.Count / (float)Math.Max(1, points.Length);
        return new PoseScore(delta, inliers, Median(distances));
    }

    private static bool HasHorizontalNormals(Vector3[] normals)
    {
        double total = 0;
        double horizontal = 0;
        for (int i = 0; i < normals.Length; i++)
        {
            var n = normals[i];
            double energy = n.X * n.X + n.Y * n.Y + n.Z * n.Z;
            if (energy < 1e-8) continue;
            total += energy;
            horizontal += n.X * n.X + n.Y * n.Y;
        }

        return total > 0 && horizontal > 0.05 * total;
    }

    /// <summary>
    /// Keeps the part of <paramref name="translation"/> that the model normals can see.
    /// A single flat face keeps motion along its normal and drops the sideways slide.
    /// </summary>
    private static Vector3 ProjectToConstrained(Vector3 translation, Vector3[] normals)
    {
        var a = new double[3, 3];
        int count = 0;
        for (int i = 0; i < normals.Length; i++)
        {
            var n = normals[i];
            if (n.LengthSquared < 1e-8f) continue;
            a[0, 0] += n.X * n.X;
            a[0, 1] += n.X * n.Y;
            a[0, 2] += n.X * n.Z;
            a[1, 1] += n.Y * n.Y;
            a[1, 2] += n.Y * n.Z;
            a[2, 2] += n.Z * n.Z;
            count++;
        }

        if (count < 12) return Vector3.Zero;
        a[0, 0] /= count;
        a[0, 1] /= count;
        a[0, 2] /= count;
        a[1, 1] /= count;
        a[1, 2] /= count;
        a[2, 2] /= count;
        a[1, 0] = a[0, 1];
        a[2, 0] = a[0, 2];
        a[2, 1] = a[1, 2];

        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 12; sweep++)
        {
            int p = 0;
            int q = 1;
            double best = Math.Abs(a[0, 1]);
            if (Math.Abs(a[0, 2]) > best)
            {
                best = Math.Abs(a[0, 2]);
                p = 0;
                q = 2;
            }

            if (Math.Abs(a[1, 2]) > best)
            {
                best = Math.Abs(a[1, 2]);
                p = 1;
                q = 2;
            }

            if (best < 1e-12) break;
            double app = a[p, p];
            double aqq = a[q, q];
            double apq = a[p, q];
            double tau = (aqq - app) / (2.0 * apq);
            double sign = tau >= 0 ? 1.0 : -1.0;
            double t = sign / (Math.Abs(tau) + Math.Sqrt(1.0 + tau * tau));
            double c = 1.0 / Math.Sqrt(1.0 + t * t);
            double s = t * c;
            a[p, p] = app - t * apq;
            a[q, q] = aqq + t * apq;
            a[p, q] = 0;
            a[q, p] = 0;
            int r = 3 - p - q;
            double arp = a[r, p];
            double arq = a[r, q];
            double rp = c * arp - s * arq;
            double rq = s * arp + c * arq;
            a[r, p] = rp;
            a[p, r] = rp;
            a[r, q] = rq;
            a[q, r] = rq;
            for (int row = 0; row < 3; row++)
            {
                double vip = v[row, p];
                double viq = v[row, q];
                v[row, p] = c * vip - s * viq;
                v[row, q] = s * vip + c * viq;
            }
        }

        double lmax = Math.Max(a[0, 0], Math.Max(a[1, 1], a[2, 2]));
        if (lmax < 1e-8) return Vector3.Zero;
        var result = Vector3.Zero;
        for (int axis = 0; axis < 3; axis++)
        {
            if (a[axis, axis] < 0.15 * lmax) continue;
            var axisVec = new Vector3((float)v[0, axis], (float)v[1, axis], (float)v[2, axis]);
            result += Vector3.Dot(translation, axisVec) * axisVec;
        }

        return result;
    }

    private sealed class ScanGrid
    {
        private readonly Dictionary<(int X, int Y, int Z), List<Vector3>> _cells = new();
        private readonly float _cell;
        private readonly float _pairSq;

        public ScanGrid(IReadOnlyList<Vector3> scan, float pair)
        {
            _cell = MathF.Max(pair, 1f);
            _pairSq = pair * pair;
            for (int i = 0; i < scan.Count; i++)
            {
                var key = Key(scan[i]);
                if (!_cells.TryGetValue(key, out var list))
                {
                    list = new List<Vector3>();
                    _cells[key] = list;
                }

                list.Add(scan[i]);
            }
        }

        public bool TryNearest(Vector3 query, out Vector3 nearest)
        {
            nearest = default;
            var origin = Key(query);
            float best = _pairSq;
            bool found = false;
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        if (!_cells.TryGetValue((origin.X + x, origin.Y + y, origin.Z + z), out var list))
                            continue;
                        for (int i = 0; i < list.Count; i++)
                        {
                            float d = (list[i] - query).LengthSquared;
                            if (d > best) continue;
                            best = d;
                            nearest = list[i];
                            found = true;
                        }
                    }
                }
            }

            return found;
        }

        private (int X, int Y, int Z) Key(Vector3 p)
            => ((int)MathF.Floor(p.X / _cell), (int)MathF.Floor(p.Y / _cell), (int)MathF.Floor(p.Z / _cell));
    }

    private static Matrix4 RigidAbout(Vector3 centroid, Vector3 omega, Vector3 translation)
    {
        float theta = omega.Length;
        var rot = theta < 1e-8f
            ? Matrix4.Identity
            : Matrix4.CreateFromAxisAngle(omega / theta, theta);
        var spun = TransformPoint(centroid, rot);
        var shift = centroid - spun + translation;
        rot.M41 = shift.X;
        rot.M42 = shift.Y;
        rot.M43 = shift.Z;
        rot.M44 = 1f;
        return rot;
    }

    private static float CentroidMotion(IReadOnlyList<Vector3> original, Matrix4 delta)
    {
        var c = Centroid(original);
        return (TransformPoint(c, delta) - c).Length;
    }

    private static float RotationDegrees(Matrix4 m)
    {
        float trace = m.M11 + m.M22 + m.M33;
        float cos = Math.Clamp((trace - 1f) * 0.5f, -1f, 1f);
        return MathHelper.RadiansToDegrees(MathF.Acos(cos));
    }

    private static Vector3 Centroid(IReadOnlyList<Vector3> pts)
    {
        var sum = Vector3.Zero;
        for (int i = 0; i < pts.Count; i++)
            sum += pts[i];
        return sum / Math.Max(1, pts.Count);
    }

    private static Vector3 Centroid(Vector3[] pts)
    {
        var sum = Vector3.Zero;
        for (int i = 0; i < pts.Length; i++)
            sum += pts[i];
        return sum / Math.Max(1, pts.Length);
    }

    private static float Median(List<float> values)
    {
        values.Sort();
        int n = values.Count;
        if (n == 0) return float.MaxValue;
        return (n & 1) == 1 ? values[n / 2] : 0.5f * (values[n / 2 - 1] + values[n / 2]);
    }

    private static bool Solve(double[,] a, double[] b, out double[] x)
    {
        x = new double[6];
        var m = new double[6, 6];
        var rhs = new double[6];
        for (int r = 0; r < 6; r++)
        {
            rhs[r] = b[r];
            for (int c = 0; c < 6; c++)
                m[r, c] = a[r, c];
        }

        for (int col = 0; col < 6; col++)
        {
            int pivot = col;
            double best = Math.Abs(m[col, col]);
            for (int r = col + 1; r < 6; r++)
            {
                double v = Math.Abs(m[r, col]);
                if (v > best)
                {
                    best = v;
                    pivot = r;
                }
            }
            if (best < 1e-8)
                return false;
            if (pivot != col)
            {
                (rhs[col], rhs[pivot]) = (rhs[pivot], rhs[col]);
                for (int c = 0; c < 6; c++)
                    (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
            }

            double div = m[col, col];
            for (int c = col; c < 6; c++)
                m[col, c] /= div;
            rhs[col] /= div;
            for (int r = 0; r < 6; r++)
            {
                if (r == col) continue;
                double f = m[r, col];
                if (f == 0) continue;
                for (int c = col; c < 6; c++)
                    m[r, c] -= f * m[col, c];
                rhs[r] -= f * rhs[col];
            }
        }

        for (int i = 0; i < 6; i++)
            x[i] = rhs[i];
        return true;
    }
}
