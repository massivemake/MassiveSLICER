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
        var accum = Locate(points, normals, scanPoints, scanGrid, pair, stock);
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

        // Point-to-plane cannot see a slide along the sheet. The seat is already
        // on the face, so the scan points within 15 mm are that face, and their
        // mean gap is the slide. Points past the part are outside this snap.
        var snap = SnapInsetShift(points, normals, scanGrid, stock);
        if (snap.M41 * snap.M41 + snap.M42 * snap.M42 + snap.M43 * snap.M43 > 1e-8f)
        {
            Apply(points, normals, snap);
            accum *= snap;
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
        Vector3[] points, Vector3[] normals, IReadOnlyList<Vector3> scan, ScanGrid grid, float pair, float stock)
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

        var full = SearchFullTurn(points, normals, scan, grid, pair, pivot, stock);
        if (Clearer(full, best))
            best = full;

        // A scanned face can be a small part of the model. Keep a pose that puts
        // the scan on that face even when most of the model is elsewhere.
        if (!Clearer(best, here))
            return Matrix4.Identity;
        if (best.Inliers < 0.20f && PortionOfScanOnModel(points, scan, best.Delta, pair) < 0.35f)
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
        ScanGrid fine, float pair, Vector3 pivot, float stock)
    {
        // A scanned face is a sheet. Stand the model's face on that sheet first.
        // The euler grid below is for a scan that shows more than one side.
        // A seat that only covers a stripe of the scan does not skip that search.
        PoseScore? sheet = null;
        float sheetCover = 0f;
        if (TrySheetAxis(scan, out var sheetAxis))
        {
            if (TryMeanSurfaceNormal(scan, sheetAxis, out var surface))
                sheetAxis = surface;
            var seated = SeatOnSheet(points, normals, scan, fine, pair, pivot, sheetAxis, stock);
            sheetCover = PortionOfScanOnModel(points, scan, seated.Delta, pair);
            // A one-face scan longer than the part covers about a third of the
            // points. The euler grid does not improve that seat, and it is the
            // slow part of a merged scan. A stripe below this still falls through.
            if (sheetCover >= 0.30f && seated.MedianMm < pair)
                return seated;
            if (sheetCover >= 0.20f)
                sheet = seated;
        }

        float radius = MeanRadius(points);
        float residual = radius * MathF.Sin(MathHelper.DegreesToRadians(23f));
        float coarse = MathF.Max(pair, MathF.Min(400f, residual * 1.4f));
        // A merged scan is tens of thousands of samples. A few hundred is a test
        // part or one face, and thinning those cells drops the shape.
        var rank = coarse > pair + 1f
            ? new ScanGrid(scan, coarse, spread: scan.Count > 4000)
            : fine;

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

        PoseScore euler;
        if (best.Inliers < 0.12f)
            euler = ScorePose(points, Matrix4.Identity, fine);
        else
        {
            var pulled = Pull(points, normals, best.Delta, rank, stock: 0f, coarse);
            var rotation = RotationOf(pulled);
            euler = TranslationPose(points, normals, scan, pair, fine, rotation);
        }

        if (sheet is PoseScore seatedPose
            && sheetCover >= PortionOfScanOnModel(points, scan, euler.Delta, pair))
            return seatedPose;
        return euler;
    }

    /// <summary>
    /// Maps each common model normal onto the sheet, then spins about that normal.
    /// On a long part the true twist is only a degree or two wide, so a 15° sample
    /// is ranked with a bin as wide as that arc, then tightened. The scan's outline
    /// is what separates that twist from another face of the same spacing.
    /// </summary>
    private static PoseScore SeatOnSheet(
        Vector3[] points, Vector3[] normals, IReadOnlyList<Vector3> scan,
        ScanGrid fine, float pair, Vector3 pivot, Vector3 axis, float stock)
    {
        var faces = FaceNormals(normals, 8);
        float reach = MaxRadius(points) * MathF.Sin(MathHelper.DegreesToRadians(8f));
        float capture = MathF.Max(pair, MathF.Min(400f, reach));
        float coarseGate = MathF.Max(pair, MathF.Min(400f, capture * 1.25f));
        float mid = MathF.Max(pair, capture * 0.45f);

        var best = ScorePose(points, Matrix4.Identity, fine);
        float bestCover = -1f;
        float bestWorst = float.MaxValue;
        float bestTurn = float.MaxValue;
        bool any = false;

        // A symmetric face fits equally well flipped over. When the scan coverage
        // and the farthest gap tie, keep the smaller turn so the normal stays
        // pointed at the scan.
        bool Prefer(float cover, float worst, float turn, bool have)
        {
            if (cover < 0.20f) return false;
            if (!have) return true;
            if (cover > bestCover + 0.02f) return true;
            if (bestCover > cover + 0.02f) return false;
            if (worst + 1f < bestWorst) return true;
            if (bestWorst + 1f < worst) return false;
            return turn + 1f < bestTurn;
        }

        var tried = new List<Vector3>();
        foreach (var face in faces)
        {
            // One flat bin is a stripe of a curved face. Average the facing
            // normals so the whole curve stands on the sheet, not that stripe.
            if (!TryCurvedFace(normals, face, out var widened, out var members))
                continue;
            bool already = false;
            for (int t = 0; t < tried.Count; t++)
            {
                if (Vector3.Dot(tried[t], widened) > 0.98f)
                {
                    already = true;
                    break;
                }
            }

            if (already) continue;
            tried.Add(widened);

            // 1 when the scan is this face. Lower when the sheet continues past
            // the part, so the far end of that sheet is not the rank.
            float rankAt = OverlapRank(points, members, scan);
            for (int sign = -1; sign <= 1; sign += 2)
            {
                var target = axis * sign;
                var align = RotationFromTo(widened, target);

                bool Score(float spin, float bin, float gate, bool polish, out float cover, out float worst, out Matrix4 delta)
                {
                    var pre = AboutPivot(
                        pivot, align * Matrix4.CreateFromAxisAngle(target, MathHelper.DegreesToRadians(spin)));
                    if (!TryFaceTranslation(points, normals, scan, members, bin, pre, stock, out var shift))
                    {
                        cover = 0f;
                        worst = float.MaxValue;
                        delta = pre;
                        return false;
                    }

                    delta = pre * Matrix4.CreateTranslation(shift);
                    MeasureFaceOverlap(
                        points, normals, members, scan, delta, gate, rankAt, stock,
                        out cover, out worst, out float edgeGap);
                    // The last fraction of a degree is the corner. The sheet past
                    // the part is not that corner.
                    if (polish)
                        worst = edgeGap;
                    return true;
                }

                bool Pick(float origin, float step, int radius, float bin, float gate, bool polish, out float spin, out Matrix4 delta)
                {
                    spin = origin;
                    delta = Matrix4.Identity;
                    float cover = -1f;
                    float worst = float.MaxValue;
                    bool hit = false;
                    for (int s = -radius; s <= radius; s++)
                    {
                        if (!Score(origin + s * step, bin, gate, polish, out float candCover, out float candWorst, out var candDelta))
                            continue;
                        float candSpin = origin + s * step;
                        bool betterCover = candCover > cover + 0.02f;
                        bool coverTied = candCover >= cover - 0.02f;
                        // The last tenth of a degree moves a long face by a
                        // fraction of a millimetre. The coarse rank stays at 1 mm.
                        float worstGain = polish ? 0.2f : 1f;
                        bool betterWorst = candWorst + worstGain < worst;
                        // Only a face that truly coincides at two spins (a square
                        // grid) may prefer the smaller spin. A long face's edge
                        // moves more than this between samples.
                        bool sameSeat = coverTied && MathF.Abs(candWorst - worst) <= 0.25f;
                        bool closerSpin = sameSeat && MathF.Abs(candSpin) + 0.01f < MathF.Abs(spin);
                        if (hit && !betterCover && !(coverTied && betterWorst) && !closerSpin)
                            continue;
                        hit = true;
                        cover = candCover;
                        worst = candWorst;
                        spin = candSpin;
                        delta = candDelta;
                    }

                    return hit;
                }

                if (!Pick(0f, 15f, 12, capture, coarseGate, false, out float twist, out _))
                    continue;
                if (!Pick(twist, 2f, 4, mid, mid, false, out twist, out _))
                    continue;
                if (!Pick(twist, 1f, 2, pair, pair, false, out twist, out _))
                    continue;
                // A degree of twist at the end of a long face is several millimetres.
                // Point-to-plane will not pull it back, because every normal on this
                // face points the same way. Polish the corner, not the sheet past it.
                if (!Pick(twist, 0.1f, 6, pair, pair, true, out twist, out _))
                    continue;
                if (!Pick(twist, 0.02f, 6, pair, pair, true, out twist, out _))
                    continue;
                // A curved face's estimated normal is still a fraction of a degree
                // off. Point-to-plane cannot see the twist that leaves, so tilt
                // the seat until the far side of the face lands.
                float minDot = 1f;
                for (int n = 0; n < members.Count; n++)
                    minDot = MathF.Min(minDot, Vector3.Dot(normals[members[n]], widened));
                // A failed tilt must not throw away the spin search. The untilted
                // seat is already on the face.
                bool tilted = false;
                float tightCover = 0f;
                float tightWorst = float.MaxValue;
                Matrix4 seated = Matrix4.Identity;
                if (minDot < 0.96f)
                {
                    tilted = TryTiltedSeat(
                        points, normals, members, scan, fine, pivot, align, target, twist, pair, rankAt, stock,
                        out seated, out tightCover, out tightWorst);
                }

                if (!tilted && !Score(twist, pair, pair, true, out tightCover, out tightWorst, out seated))
                    continue;
                float turn = RotationDegrees(seated);
                if (!Prefer(tightCover, tightWorst, turn, any)) continue;
                var pose = ScorePose(points, seated, fine);
                if (pose.Inliers < 0.12f && tightCover < 0.50f) continue;
                any = true;
                best = pose;
                bestCover = tightCover;
                bestWorst = tightWorst;
                bestTurn = turn;
            }
        }

        return any ? best : ScorePose(points, Matrix4.Identity, fine);
    }

    /// <summary>
    /// Spins stay on <paramref name="target"/>. A curved sheet's normal is only
    /// known to a fraction of a degree, so try a small tilt about the two axes
    /// in the sheet and keep the seat whose face edge is closest. The shift is
    /// the face vote, nudged back onto the scan, not the centroid of a scan
    /// that continues past the part.
    /// </summary>
    private static bool TryTiltedSeat(
        Vector3[] points, Vector3[] normals, List<int> members, IReadOnlyList<Vector3> scan, ScanGrid grid,
        Vector3 pivot, Matrix4 align, Vector3 target, float twist, float gate, float rankAt, float stock,
        out Matrix4 delta, out float cover, out float worst)
    {
        delta = Matrix4.Identity;
        cover = 0f;
        worst = float.MaxValue;
        var pole = MathF.Abs(target.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var u = Vector3.Cross(target, pole);
        if (u.LengthSquared < 1e-8f) return false;
        u.Normalize();
        var v = Vector3.Cross(target, u);
        v.Normalize();
        var spin = Matrix4.CreateFromAxisAngle(target, MathHelper.DegreesToRadians(twist));
        var basePre = AboutPivot(pivot, align * spin);
        if (!TryFaceTranslation(points, normals, scan, members, MathF.Max(gate, 1f), basePre, stock, out var baseShift))
            return false;
        if (baseShift.LengthSquared <= 1f) return false;
        bool hit = false;
        float bestEdge = float.MaxValue;
        for (int ix = -8; ix <= 8; ix++)
        {
            for (int iy = -8; iy <= 8; iy++)
            {
                var tilt = Matrix4.CreateFromAxisAngle(u, MathHelper.DegreesToRadians(ix * 0.05f))
                         * Matrix4.CreateFromAxisAngle(v, MathHelper.DegreesToRadians(iy * 0.05f));
                var pre = AboutPivot(pivot, align * spin * tilt);
                // A fraction of a degree does not change which bin wins. Nudge
                // the seated shift onto the scan instead of voting it again.
                var shift = NudgeShift(points, normals, members, grid, pre, baseShift, stock);
                if (shift.LengthSquared <= 1f) continue;
                var cand = pre * Matrix4.CreateTranslation(shift);
                MeasureFaceOverlap(
                    points, normals, members, scan, cand, gate, rankAt, stock,
                    out float candCover, out float candWorst, out float candEdge);
                if (candCover < 0.20f) continue;
                // The corner of the face, not the sheet past the part. A similar
                // corner keeps the seat whose body sits closer.
                bool betterEdge = candEdge + 0.05f < bestEdge;
                bool edgeClose = candEdge <= bestEdge + 0.05f;
                bool betterBody = candWorst + 0.05f < worst;
                if (hit && !betterEdge && !(edgeClose && betterBody))
                    continue;
                hit = true;
                bestEdge = candEdge;
                worst = candWorst;
                cover = candCover;
                delta = cand;
            }
        }

        return hit;
    }

    /// <summary>
    /// Moves <paramref name="shift"/> onto the scan points already within 15 mm
    /// of this tilted face. A fraction of a degree does not need another vote.
    /// </summary>
    private static Vector3 NudgeShift(
        Vector3[] points, Vector3[] normals, List<int> members, ScanGrid scan,
        Matrix4 pre, Vector3 shift, float stock)
    {
        const float limit = 15f;
        float limitSq = limit * limit;
        int step = Math.Max(1, members.Count / 200);
        var sum = Vector3.Zero;
        int count = 0;
        for (int i = 0; i < members.Count; i += step)
        {
            var n = TransformNormal(normals[members[i]], pre);
            if (n.LengthSquared < 1e-8f) continue;
            var target = TransformPoint(points[members[i]], pre) + shift + n * stock;
            if (!scan.TryNearest(target, out var q)) continue;
            var gap = q - target;
            if (gap.LengthSquared > limitSq) continue;
            sum += gap;
            count++;
        }

        if (count < 8) return shift;
        var corr = sum / count;
        if (corr.Length > 8f) return shift;
        return shift + corr;
    }

    /// <summary>
    /// The facing surface around <paramref name="seed"/>. A flat face stays that
    /// face. A curve wider than one bin is walked to its own mean, so the far
    /// side is included and the normal is not left tilted toward the seed.
    /// </summary>
    private static bool TryCurvedFace(
        Vector3[] normals, Vector3 seed, out Vector3 widened, out List<int> members)
    {
        widened = seed;
        members = new List<int>();
        if (widened.LengthSquared < 1e-8f) return false;
        widened.Normalize();

        for (int pass = 0; pass < 8; pass++)
        {
            var sum = Vector3.Zero;
            int count = 0;
            for (int i = 0; i < normals.Length; i++)
            {
                if (Vector3.Dot(normals[i], widened) <= 0.5f) continue;
                sum += normals[i];
                count++;
            }

            if (count < 8 || sum.LengthSquared < 1e-8f) return false;
            sum.Normalize();
            float settled = Vector3.Dot(sum, widened);
            widened = sum;
            if (settled > 0.999f) break;
        }

        for (int i = 0; i < normals.Length; i++)
        {
            if (Vector3.Dot(normals[i], widened) > 0.5f)
                members.Add(i);
        }

        return members.Count >= 8;
    }

    /// <summary>Fraction of the scan that lands within <paramref name="gate"/> of the posed model.</summary>
    private static float PortionOfScanOnModel(
        Vector3[] points, IReadOnlyList<Vector3> scan, Matrix4 delta, float gate)
    {
        if (points.Length == 0 || scan.Count == 0) return 0f;
        int modelStep = Math.Max(1, points.Length / 200);
        int scanStep = Math.Max(1, scan.Count / 400);
        var posed = new List<Vector3>(points.Length / modelStep + 1);
        for (int i = 0; i < points.Length; i += modelStep)
            posed.Add(TransformPoint(points[i], delta));

        int seen = 0;
        int hit = 0;
        float gateSq = gate * gate;
        for (int s = 0; s < scan.Count; s += scanStep)
        {
            seen++;
            var q = scan[s];
            bool near = false;
            for (int i = 0; i < posed.Count; i++)
            {
                if ((posed[i] - q).LengthSquared <= gateSq)
                {
                    near = true;
                    break;
                }
            }

            if (near) hit++;
        }

        return seen == 0 ? 0f : hit / (float)seen;
    }

    /// <summary>
    /// Point-to-plane leaves a slide along the sheet. The pose is already on the
    /// face, so each point takes the scan point within 15 mm of the stocked
    /// surface and the pose steps by their mean gap. The gaps have to agree:
    /// a twist, a sampled corner, a correction under 1 mm or over 8 mm, or
    /// fewer than 8 points, is left alone. Points past the part and a sideways
    /// slide have no such neighbours.
    /// </summary>
    private static Matrix4 SnapInsetShift(
        Vector3[] points, Vector3[] normals, ScanGrid scan, float stock)
    {
        const float limit = 15f;
        float limitSq = limit * limit;
        var sum = Vector3.Zero;
        int count = 0;
        float sqSum = 0f;
        for (int i = 0; i < points.Length; i++)
        {
            var n = normals[i];
            if (n.LengthSquared < 1e-8f) continue;
            var target = points[i] + n * stock;
            if (!scan.TryNearest(target, out var q)) continue;
            var gap = q - target;
            if (gap.LengthSquared > limitSq) continue;
            sum += gap;
            sqSum += gap.LengthSquared;
            count++;
        }

        if (count < 8) return Matrix4.Identity;
        var corr = sum / count;
        // A twist or a sampled corner scatters the gaps. Averaging those moves
        // the far corner the wrong way. A real slide points the same way.
        float rms = MathF.Sqrt(MathF.Max(0f, sqSum / count - corr.LengthSquared));
        if (corr.Length < 1f || corr.Length < rms * 2f || corr.Length > 8f)
            return Matrix4.Identity;
        return Matrix4.CreateTranslation(corr);
    }

    /// <summary>
    /// Translation voted only by the face being seated, so the other sides of the
    /// model do not pull the bin. A short vote is kept along that face's normal.
    /// </summary>
    private static bool TryFaceTranslation(
        Vector3[] model, Vector3[] normals, IReadOnlyList<Vector3> scan,
        List<int> facePoints, float bin, Matrix4 pre, float stock, out Vector3 translation)
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
            // The scan is the outside of the stock. Vote the model surface, or a
            // curved face's differing normals pull the bin off the rigid shift.
            var inset = TransformNormal(normals[facePoints[n]], pre) * stock;
            samples++;
            for (int j = 0; j < scan.Count; j += scanStep)
            {
                var t = scan[j] - p - inset;
                var key = (
                    (int)MathF.Floor(t.X / bin + 0.5f),
                    (int)MathF.Floor(t.Y / bin + 0.5f),
                    (int)MathF.Floor(t.Z / bin + 0.5f));
                votes.TryGetValue(key, out var slot);
                votes[key] = (slot.Count + 1, slot.Sum + t);
            }
        }

        int bestCount = 7;
        foreach (var slot in votes.Values)
            if (slot.Count > bestCount) bestCount = slot.Count;
        if (bestCount < 8 || samples < 8)
            return false;

        // A busy bin's mean is pulled off a curved face by the other pairs that
        // land in it. The face-to-scan centroid is one candidate. Rank candidates
        // by the closest third of the scan, so points past the part do not make
        // every shift look equally far and a tight stripe cannot win on the median.
        var turned = new Vector3[facePoints.Count];
        var posed = new List<Vector3>(samples);
        var posedNormal = new List<Vector3>(samples);
        var faceSum = Vector3.Zero;
        for (int i = 0; i < facePoints.Count; i++)
        {
            var p = TransformPoint(model[facePoints[i]], pre);
            faceSum += p;
            turned[i] = TransformNormal(normals[facePoints[i]], pre);
            if (i % modelStep == 0)
            {
                posed.Add(p);
                posedNormal.Add(turned[i]);
            }
        }

        var scanSum = Vector3.Zero;
        for (int j = 0; j < scan.Count; j++)
            scanSum += scan[j];

        var candidates = new List<Vector3>(25);
        if (posed.Count > 0 && scan.Count > 0)
            candidates.Add(scanSum / scan.Count - faceSum / facePoints.Count);
        foreach (var slot in votes.Values.OrderByDescending(slot => slot.Count).Take(24))
        {
            if (slot.Count < 8) break;
            candidates.Add(slot.Sum / slot.Count);
        }

        float bestOwned = float.MaxValue;
        float bestMedian = float.MaxValue;
        float bestWorst = float.MaxValue;
        bool found = false;
        foreach (var raw in candidates)
        {
            if (float.IsNaN(raw.X) || float.IsInfinity(raw.X)) continue;
            var shift = raw.Length > FarMoveMm ? raw : ProjectToConstrained(raw, turned);
            if (shift.LengthSquared <= 1f) continue;
            var distances = new List<float>();
            for (int j = 0; j < scan.Count; j += scanStep)
            {
                var q = scan[j];
                float best = float.MaxValue;
                for (int i = 0; i < posed.Count; i++)
                {
                    // Rank the model surface under the stock, not the outer skin.
                    // Otherwise the closest shift sits on the scan and the curve
                    // cannot pull that tangent back.
                    var onScan = posed[i] + shift + posedNormal[i] * stock;
                    float d = (onScan - q).LengthSquared;
                    if (d < best) best = d;
                }

                distances.Add(MathF.Sqrt(best));
            }

            if (distances.Count == 0) continue;
            distances.Sort();
            // The closest third of the scan is the part. The rest may be bed
            // past the model, and that tail makes every shift look equally far.
            float owned = distances[(int)((distances.Count - 1) * 0.35f)];
            float median = distances[distances.Count / 2];
            float worst = distances[distances.Count - 1];
            bool betterOwned = owned + 5f < bestOwned;
            bool ownedTied = owned <= bestOwned + 5f;
            bool betterMedian = median + 0.5f < bestMedian
                || (median < bestMedian + 0.5f && worst < bestWorst);
            if (!found || betterOwned || (ownedTied && betterMedian))
            {
                found = true;
                bestOwned = owned;
                bestMedian = median;
                bestWorst = worst;
                translation = shift;
            }
        }

        return found;
    }

    /// <summary>
    /// Fraction of the scan that lands on this face, and how far the part's own
    /// share of that scan sits. Euclidean on purpose: a near miss along the face
    /// is still the right twist, and a tab the face does not own stays far.
    /// </summary>
    private static void MeasureFaceOverlap(
        Vector3[] points, Vector3[] normals, List<int> facePoints, IReadOnlyList<Vector3> scan,
        Matrix4 delta, float gate, float rankAt, float stock,
        out float cover, out float worst, out float edgeGap)
    {
        cover = 0f;
        worst = float.MaxValue;
        edgeGap = float.MaxValue;
        if (facePoints.Count == 0 || scan.Count == 0) return;

        int modelStep = Math.Max(1, facePoints.Count / 200);
        int scanStep = Math.Max(1, scan.Count / 400);
        var posed = new List<Vector3>(facePoints.Count / modelStep + 1);
        var posedNormal = new List<Vector3>(facePoints.Count / modelStep + 1);
        for (int n = 0; n < facePoints.Count; n += modelStep)
        {
            posed.Add(TransformPoint(points[facePoints[n]], delta));
            posedNormal.Add(TransformNormal(normals[facePoints[n]], delta));
        }

        int seen = 0;
        int hit = 0;
        var distances = new List<float>();
        var modelBest = new float[posed.Count];
        Array.Fill(modelBest, float.MaxValue);
        float gateSq = gate * gate;
        for (int s = 0; s < scan.Count; s += scanStep)
        {
            seen++;
            float best = float.MaxValue;
            var q = scan[s];
            for (int i = 0; i < posed.Count; i++)
            {
                // The stock thickness hides a twist: 4 mm of stock and 2 mm of
                // slide are almost the same distance. Rank the stocked surface.
                var onScan = posed[i] + posedNormal[i] * stock;
                float d = (onScan - q).LengthSquared;
                if (d < best) best = d;
                if (d < modelBest[i]) modelBest[i] = d;
            }

            if (best <= gateSq) hit++;
            distances.Add(MathF.Sqrt(best));
        }

        cover = seen == 0 ? 0f : hit / (float)seen;
        if (distances.Count == 0)
        {
            worst = float.MaxValue;
            edgeGap = float.MaxValue;
            return;
        }

        distances.Sort();
        // Spin ranking uses the part's share of the sheet. The farthest scan
        // point is the same for every spin once the sheet continues past the part.
        float rank = Math.Clamp(rankAt, 0.25f, 1f);
        worst = distances[(int)((distances.Count - 1) * rank)];

        // The model's own corner, not the scan past the part. A connected sheet
        // has no gap to walk, and the corner is what a fraction of a degree moves.
        edgeGap = 0f;
        for (int i = 0; i < modelBest.Length; i++)
        {
            if (modelBest[i] == float.MaxValue) continue;
            edgeGap = MathF.Max(edgeGap, MathF.Sqrt(modelBest[i]));
        }
    }

    /// <summary>
    /// 1 when the scan's in-plane spread matches the face. Lower when the sheet
    /// continues past the part. Rotation invariant, so the model can still be
    /// far away and turned.
    /// </summary>
    private static float OverlapRank(Vector3[] model, List<int> face, IReadOnlyList<Vector3> scan)
    {
        if (face.Count < 8 || scan.Count < 8) return 1f;
        var facePts = new List<Vector3>(face.Count);
        for (int i = 0; i < face.Count; i++)
            facePts.Add(model[face[i]]);
        if (!TryPrincipalAxes(facePts, out _, out _, out float faceMid, out float faceWide))
            return 1f;
        if (!TryPrincipalAxes(scan, out _, out _, out float scanMid, out float scanWide))
            return 1f;
        float faceArea = MathF.Sqrt(MathF.Max(faceMid, 0f) * MathF.Max(faceWide, 0f));
        float scanArea = MathF.Sqrt(MathF.Max(scanMid, 0f) * MathF.Max(scanWide, 0f));
        if (faceArea < 1f || scanArea < 1f) return 1f;
        float share = faceArea / scanArea;
        if (share >= 0.8f) return 1f;
        return Math.Clamp(share, 0.35f, 0.7f);
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
        if (!TryPrincipalAxes(scan, out var thinAxis, out float thin, out _, out float wide))
            return false;
        if (wide < 1e-4f || thin > 0.15f * wide)
            return false;
        axis = thinAxis;
        return true;
    }

    /// <summary>
    /// Mean of the scan's local surface normals. The best-fit plane of a curved
    /// face is tilted off that mean, and standing the model on the plane leaves
    /// only a stripe of the scan within the match gate.
    /// </summary>
    private static bool TryMeanSurfaceNormal(IReadOnlyList<Vector3> scan, Vector3 plane, out Vector3 mean)
    {
        mean = plane;
        if (scan.Count < 12 || plane.LengthSquared < 1e-8f) return false;

        int probes = Math.Min(40, scan.Count);
        int probeStep = Math.Max(1, scan.Count / probes);
        var nearest = new List<float>(probes);
        for (int i = 0; i < scan.Count; i += probeStep)
        {
            float best = float.MaxValue;
            var p = scan[i];
            for (int j = 0; j < scan.Count; j++)
            {
                if (j == i) continue;
                float d = (scan[j] - p).LengthSquared;
                if (d > 1e-4f && d < best) best = d;
            }

            if (best < float.MaxValue)
                nearest.Add(MathF.Sqrt(best));
        }

        if (nearest.Count < 8) return false;
        nearest.Sort();
        float radius = Math.Clamp(nearest[nearest.Count / 2] * 4f, 15f, 250f);
        float radiusSq = radius * radius;

        int seeds = Math.Min(80, scan.Count);
        int seedStep = Math.Max(1, scan.Count / seeds);
        var accepted = new List<Vector3>();
        for (int i = 0; i < scan.Count; i += seedStep)
        {
            var origin = scan[i];
            var patch = new List<Vector3>();
            for (int j = 0; j < scan.Count; j++)
            {
                if ((scan[j] - origin).LengthSquared <= radiusSq)
                    patch.Add(scan[j]);
            }

            if (patch.Count < 8) continue;
            if (!TryPrincipalAxes(patch, out var normal, out float thin, out float middle, out _))
                continue;
            if (middle <= 1e-6f || thin > 0.2f * middle) continue;
            if (Vector3.Dot(normal, plane) < 0f)
                normal = -normal;
            accepted.Add(normal);
        }

        if (accepted.Count < 8) return false;

        // A flat extension past the part shares one normal. A curved face does
        // not. Cap that spike so the extension cannot tilt the axis off the face.
        // Under the cap the sum is the sample mean, so a face with no extension
        // is unchanged.
        int cap = Math.Max(4, accepted.Count / 10);
        var bins = new Dictionary<(int Tilt, int Pan), (int Count, Vector3 Sum)>();
        for (int i = 0; i < accepted.Count; i++)
        {
            var n = accepted[i];
            int tilt = (int)MathF.Round(MathF.Acos(Math.Clamp(n.Z, -1f, 1f)) / MathF.PI * 36f);
            if (tilt < 0) tilt = 0;
            if (tilt > 36) tilt = 36;
            int pan = (int)MathF.Round((MathF.Atan2(n.Y, n.X) + MathF.PI) / (2f * MathF.PI) * 36f);
            if (pan < 0) pan = 0;
            if (pan >= 36) pan = 0;
            var key = (tilt, pan);
            bins.TryGetValue(key, out var slot);
            bins[key] = (slot.Count + 1, slot.Sum + n);
        }

        var sum = Vector3.Zero;
        foreach (var slot in bins.Values)
        {
            float w = Math.Min(slot.Count, cap);
            sum += slot.Sum * (w / slot.Count);
        }

        if (sum.LengthSquared < 1e-8f) return false;
        sum.Normalize();
        if (Vector3.Dot(sum, plane) < 0f)
            sum = -sum;
        mean = sum;
        return true;
    }

    private static bool TryPrincipalAxes(
        IReadOnlyList<Vector3> pts,
        out Vector3 thinAxis, out float thin, out float middle, out float wide)
    {
        thinAxis = Vector3.Zero;
        thin = 0f;
        middle = 0f;
        wide = 0f;
        if (pts.Count < 8) return false;

        var center = Centroid(pts);
        double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            double x = pts[i].X - center.X;
            double y = pts[i].Y - center.Y;
            double z = pts[i].Z - center.Z;
            xx += x * x;
            xy += x * y;
            xz += x * z;
            yy += y * y;
            yz += y * z;
            zz += z * z;
        }

        double inv = 1.0 / pts.Count;
        var a = new double[3, 3]
        {
            { xx * inv, xy * inv, xz * inv },
            { xy * inv, yy * inv, yz * inv },
            { xz * inv, yz * inv, zz * inv },
        };
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        Jacobi3(a, v);
        int thinI = 0;
        int wideI = 0;
        for (int i = 1; i < 3; i++)
        {
            if (a[i, i] < a[thinI, thinI]) thinI = i;
            if (a[i, i] > a[wideI, wideI]) wideI = i;
        }

        if (thinI == wideI) return false;
        int midI = 3 - thinI - wideI;
        thin = (float)a[thinI, thinI];
        middle = (float)a[midI, midI];
        wide = (float)a[wideI, wideI];
        thinAxis = new Vector3((float)v[0, thinI], (float)v[1, thinI], (float)v[2, thinI]);
        if (thinAxis.LengthSquared < 1e-8f) return false;
        thinAxis.Normalize();
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

        public ScanGrid(IReadOnlyList<Vector3> scan, float pair, bool spread = false)
        {
            _cell = MathF.Max(pair, 1f);
            _pairSq = pair * pair;
            // The coarse rank grid's cells are hundreds of millimetres. Keeping
            // every scan point makes each pose test walk the whole cloud. One
            // point per half-cell is enough to tell a turn, and the fine grid
            // still holds every point for the fit that is applied.
            float sub = spread ? _cell * 0.5f : 0f;
            var seen = spread ? new HashSet<(int X, int Y, int Z)>() : null;
            for (int i = 0; i < scan.Count; i++)
            {
                if (seen is not null)
                {
                    var fine = (
                        (int)MathF.Floor(scan[i].X / sub),
                        (int)MathF.Floor(scan[i].Y / sub),
                        (int)MathF.Floor(scan[i].Z / sub));
                    if (!seen.Add(fine)) continue;
                }

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
