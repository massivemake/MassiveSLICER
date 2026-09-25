using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Candidate poses for Auto Orient: the part spun about the world vertical and slid across the
/// bed, never tilted. Pure geometry: which spins and spots fit the bed, the rigid transform for
/// each, and how scored candidates rank. The robot scoring itself lives with the IK solver.
/// </summary>
/// <remarks>
/// Spinning about the vertical and sliding in XY cannot lift any point of the part or change
/// which face is down, so a part that was flat (or deliberately flipped, or laid on a side)
/// stays exactly that way. Tilting for overhang was dropped on purpose: it ignored planar
/// printing rules (contact area, tall-and-tippy, upside-down pyramids) and could land a part a
/// fraction of a degree off flat.
/// </remarks>
public static class PlacementSearch
{
    /// <summary>
    /// How much of the bed a placement may use — the same 90 % as Fit to Cell, leaving room
    /// for a brim, a purge line and the nozzle body at the edge.
    /// </summary>
    public const float BedMargin = 0.90f;

    /// <summary>A spin (degrees about world +Z through the pivot) plus an XY slide (mm).</summary>
    public readonly record struct Candidate(float SpinDeg, float Dx, float Dy)
    {
        public bool IsCurrentPose => SpinDeg == 0f && Dx == 0f && Dy == 0f;

        /// <summary>How far the footprint centre moves, for tie-breaks toward staying put.</summary>
        public float SlideMm => MathF.Sqrt(Dx * Dx + Dy * Dy);
    }

    /// <summary>Robot verdict for one candidate over a set of sample moves.</summary>
    /// <param name="Unreachable">Samples with no IK solution inside the joint envelope.</param>
    /// <param name="MarginDeg">
    /// Worst room to spare over the samples, in degrees: the smaller of the distance to the
    /// nearest joint limit and how far A5 stays past the 5° wrist-singularity band.
    /// Negative means some sample is inside the singularity band.
    /// </param>
    public readonly record struct Score(int Unreachable, float MarginDeg);

    /// <summary>
    /// The rigid transform for <paramref name="c"/>: spin about the vertical line through
    /// <paramref name="pivot"/>, then slide. Row-vector convention (<c>point * M</c>), matching
    /// <see cref="Vector3.Transform(Vector3, Matrix4x4)"/>. Z is never touched.
    /// </summary>
    public static Matrix4x4 Transform(Candidate c, Vector2 pivot)
    {
        var p = new Vector3(pivot, 0f);
        return Matrix4x4.CreateTranslation(-p)
             * Matrix4x4.CreateRotationZ(c.SpinDeg * MathF.PI / 180f)
             * Matrix4x4.CreateTranslation(p + new Vector3(c.Dx, c.Dy, 0f));
    }

    /// <summary>
    /// Every spin in <paramref name="spinStepDeg"/> steps, each at the part's current spot and
    /// at a grid of spots over the bed, keeping only poses whose footprint fits the bed.
    /// The current pose always comes first, fitting or not — the operator put it there.
    /// </summary>
    /// <param name="footprint">The part's XY outline in world space (any superset, e.g. a hull).</param>
    /// <param name="pivot">Spin centre in world XY (the footprint's bounding-box centre).</param>
    /// <param name="bed">Bed to fit against; null = no bed, spins in place only.</param>
    /// <param name="bedCenter">World XY of the bed's printable-surface centre.</param>
    /// <param name="gridPerAxis">Spots per bed axis (rectangular) or per diameter (rotary).</param>
    public static List<Candidate> Generate(
        IReadOnlyList<Vector2> footprint, Vector2 pivot, BedCellConfig? bed, Vector2 bedCenter,
        float spinStepDeg = 15f, int gridPerAxis = 7)
    {
        var result = new List<Candidate> { new(0f, 0f, 0f) };
        if (footprint.Count == 0) return result;
        spinStepDeg = Math.Clamp(spinStepDeg, 1f, 180f);
        gridPerAxis = Math.Max(1, gridPerAxis);

        for (float spin = 0f; spin < 360f - 1e-3f; spin += spinStepDeg)
        {
            var spun = Spin(footprint, pivot, spin);

            // The part's own spot, turned in place.
            if (spin != 0f && Fits(spun, Vector2.Zero, bed, bedCenter))
                result.Add(new Candidate(spin, 0f, 0f));

            if (bed is null) continue;

            var (min, max) = Bounds(spun);
            var centre = (min + max) * 0.5f;
            foreach (var spot in Spots(bed, bedCenter, gridPerAxis))
            {
                var d = spot - centre;
                if (MathF.Abs(d.X) < 1f && MathF.Abs(d.Y) < 1f) continue;   // same as "in place"
                if (Fits(spun, d, bed, bedCenter))
                    result.Add(new Candidate(spin, d.X, d.Y));
            }
        }
        return result;
    }

    /// <summary>
    /// Room to spare, in degrees, that counts as comfortable. Every candidate at or above it is
    /// as good as any other on robot grounds, so the choice between them goes to the bed centre.
    /// </summary>
    public const float ComfortableMarginDeg = 5f;

    /// <summary>
    /// Ranking, best first. The shop prefers parts near the middle of the bed and gives up edge
    /// clearance only when it has to, so:
    /// fewest unreachable samples; then comfortable poses (≥ <see cref="ComfortableMarginDeg"/>)
    /// ahead of tight ones; among comfortable poses, closest to the bed centre; then the most
    /// room to spare; then the smallest move and the smallest turn.
    /// </summary>
    /// <param name="pivot">Spin centre (the footprint centre at the current pose), world XY.</param>
    /// <param name="bedCenter">Bed centre, or null with no bed (then "closest" means least moved).</param>
    public static Comparison<(Candidate c, Score s)> Ranking(Vector2 pivot, Vector2? bedCenter)
    {
        float OffCentre(Candidate c) => bedCenter is { } bc
            ? Vector2.Distance(pivot + new Vector2(c.Dx, c.Dy), bc)
            : c.SlideMm;

        return (a, b) =>
        {
            int byReach = a.s.Unreachable.CompareTo(b.s.Unreachable);
            if (byReach != 0) return byReach;

            bool comfyA = a.s.MarginDeg >= ComfortableMarginDeg;
            bool comfyB = b.s.MarginDeg >= ComfortableMarginDeg;
            if (comfyA != comfyB) return comfyA ? -1 : 1;

            // Within 10 mm is the same spot for this purpose.
            float offA = OffCentre(a.c), offB = OffCentre(b.c);
            if (comfyA && MathF.Abs(offA - offB) > 10f) return offA.CompareTo(offB);

            // Margins within half a degree are a tie — not worth moving the part for.
            if (MathF.Abs(a.s.MarginDeg - b.s.MarginDeg) > 0.5f)
                return b.s.MarginDeg.CompareTo(a.s.MarginDeg);
            if (MathF.Abs(offA - offB) > 10f) return offA.CompareTo(offB);

            int bySlide = a.c.SlideMm.CompareTo(b.c.SlideMm);
            if (bySlide != 0) return bySlide;
            return MathF.Abs(Wrap(a.c.SpinDeg)).CompareTo(MathF.Abs(Wrap(b.c.SpinDeg)));
        };
    }

    /// <summary>Spin wrapped to (-180, 180] so 345° reads as the 15° turn it is.</summary>
    public static float Wrap(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        if (deg <= -180f) deg += 360f;
        return deg;
    }

    /// <summary>
    /// Whether <paramref name="footprint"/> slid by <paramref name="slide"/> lies inside the bed's
    /// usable area. Rectangular beds are taken axis-aligned in world, like Fit to Cell.
    /// </summary>
    public static bool Fits(IReadOnlyList<Vector2> footprint, Vector2 slide, BedCellConfig? bed, Vector2 bedCenter)
    {
        if (bed is null) return true;
        if (bed.IsRotaryPrintBed && bed.Diameter is > 0 and var diameter)
        {
            float r = diameter * BedMargin * 0.5f;
            float r2 = r * r;
            foreach (var p in footprint)
                if (Vector2.DistanceSquared(p + slide, bedCenter) > r2) return false;
            return true;
        }

        float hx = bed.Width * BedMargin * 0.5f, hy = bed.Depth * BedMargin * 0.5f;
        foreach (var p in footprint)
        {
            var q = p + slide - bedCenter;
            if (MathF.Abs(q.X) > hx || MathF.Abs(q.Y) > hy) return false;
        }
        return true;
    }

    private static IEnumerable<Vector2> Spots(BedCellConfig bed, Vector2 bedCenter, int n)
    {
        if (bed.IsRotaryPrintBed && bed.Diameter is > 0 and var diameter)
        {
            float r = diameter * BedMargin * 0.5f;
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                float u = n == 1 ? 0f : -r + 2f * r * i / (n - 1);
                float v = n == 1 ? 0f : -r + 2f * r * j / (n - 1);
                if (u * u + v * v <= r * r) yield return bedCenter + new Vector2(u, v);
            }
            yield break;
        }

        float hx = bed.Width * BedMargin * 0.5f, hy = bed.Depth * BedMargin * 0.5f;
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            float u = n == 1 ? 0f : -hx + 2f * hx * i / (n - 1);
            float v = n == 1 ? 0f : -hy + 2f * hy * j / (n - 1);
            yield return bedCenter + new Vector2(u, v);
        }
    }

    private static Vector2[] Spin(IReadOnlyList<Vector2> pts, Vector2 pivot, float deg)
    {
        float a = deg * MathF.PI / 180f, cs = MathF.Cos(a), sn = MathF.Sin(a);
        var result = new Vector2[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            var d = pts[i] - pivot;
            result[i] = pivot + new Vector2(d.X * cs - d.Y * sn, d.X * sn + d.Y * cs);
        }
        return result;
    }

    private static (Vector2 min, Vector2 max) Bounds(IReadOnlyList<Vector2> pts)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var p in pts) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
        return (min, max);
    }

    /// <summary>
    /// Convex hull of <paramref name="points"/> (Andrew's monotone chain) — a small outline that
    /// contains the whole footprint, so bed-fit checks stay cheap on a million-move toolpath.
    /// </summary>
    public static List<Vector2> Hull(IEnumerable<Vector2> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3) return pts;
        static float Cross(Vector2 o, Vector2 a, Vector2 b)
            => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<Vector2>(pts.Count * 2);
        foreach (var p in pts)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0f) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        int lower = hull.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--)
        {
            var p = pts[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0f) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }
}
