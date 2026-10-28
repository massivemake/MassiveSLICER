using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

public enum DrillSiteKind { Hole, Edge, Face }

/// <summary>A hole found on the mesh, or a drill point the user placed on an edge or a face.</summary>
public sealed class DrillSite
{
    public int Id { get; set; }
    public DrillSiteKind Kind { get; init; }
    public Vector3 Position { get; set; }
    public Vector3 Axis { get; set; } = Vector3.UnitZ;
    public float DetectedDiameterMm { get; set; }
    public float SpanMm { get; set; }
    public float? DiameterOverrideMm { get; set; }
    public float? DepthOverrideMm { get; set; }
    public bool Included { get; set; }
    /// <summary>Round plunge, counterbore, countersink, or an outline the tool follows.</summary>
    public DrillCutKind Cut { get; set; }
    /// <summary>Through-hole diameter under a counterbore or countersink. Zero on a plain hole.</summary>
    public float MinorDiameterMm { get; set; }
    /// <summary>How far the large end of a counterbore or countersink goes before the through hole.</summary>
    public float ShoulderDepthMm { get; set; }
    /// <summary>Hole, Square, Slot, Polygon, Counterbore, or Countersink. Shown in the list.</summary>
    public string ShapeName { get; set; } = "Hole";
    /// <summary>The mesh rim this hole or edge point came from. Closed chains repeat the first point.</summary>
    public List<Vector3>? Rim { get; set; }

    public float DiameterMm => DiameterOverrideMm ?? DetectedDiameterMm;

    /// <summary>
    /// Plunge length. An edited depth is used as typed. An unedited hole uses the
    /// mesh span at that location plus the card's breakthrough.
    /// </summary>
    public float DepthMm(float breakthroughMm) =>
        DepthOverrideMm ?? MathF.Max(0f, SpanMm + MathF.Max(0f, breakthroughMm));
}

public sealed class DrillSurvey
{
    public IReadOnlyList<DrillSite> Holes { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<Vector3>> Edges { get; init; } = [];
}

/// <summary>
/// Finds round hole rims and other edge chains on a mesh. A solid part has no open
/// boundary, so a hole is the sharp circle where the wall meets the face, and the
/// face does not cover the middle. Other sharp chains and open boundaries are edges.
/// </summary>
public static class DrillFeatureFinder
{
    public const float WeldMm = 0.05f;
    public const float MinHoleDiameterMm = 2f;
    public const float MaxRadiusVariation = 0.18f;
    public const float SharpRadians = 30f * MathF.PI / 180f;

    public static DrillSurvey Find(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices)
    {
        if (positions.Count == 0 || indices.Count < 3)
            return new DrillSurvey();

        var (verts, tris) = Weld(positions, indices);
        if (tris.Count == 0) return new DrillSurvey();

        var holes = new List<DrillSite>();
        var edges = new List<IReadOnlyList<Vector3>>();
        int id = 1;
        void Take(List<ChainWalk> chains)
        {
            foreach (var chain in chains)
            {
                if (chain.Indices.Count < 2) continue;
                var poly = new List<Vector3>(chain.Indices.Count);
                foreach (int i in chain.Indices) poly.Add(verts[i]);
                if (ChainLength(poly) < 1f) continue;

                if (chain.Closed && TryHole(poly, chain.FaceNormal, positions, verts, tris, out var site))
                {
                    site.Id = id++;
                    holes.Add(site);
                }
                else if (chain.Closed)
                {
                    // Repeat the start so a closed rim draws and picks its last edge.
                    // Open fragments of a face walk are not edges the user can drill.
                    if (poly.Count >= 2)
                        poly.Add(poly[0]);
                    edges.Add(poly);
                }
            }
        }

        Take(Walk(BoundaryEdges(verts, tris)));
        Take(FeatureChains(verts, tris));
        AddSemicircleNotches(edges, holes, positions);
        DedupeMouths(holes);
        MergeInnerHoles(holes);
        return new DrillSurvey { Holes = holes, Edges = edges };
    }

    /// <summary>
    /// The hole whose rim is one side of <paramref name="wall"/>. A square cutout's
    /// side is a tall rectangle; clicking that wall should drill the opening, not a slot.
    /// </summary>
    public static DrillSite? OpeningForWall(IReadOnlyList<Vector3> wall, IReadOnlyList<DrillSite> holes)
    {
        if (wall.Count < 2) return null;
        foreach (var hole in holes)
        {
            var rim = hole.Rim;
            if (rim is null || rim.Count < 2) continue;
            for (int i = 0; i < wall.Count - 1; i++)
            {
                if (RimHasSegment(rim, wall[i], wall[i + 1]))
                    return hole;
            }
        }
        return null;
    }

    static bool RimHasSegment(IReadOnlyList<Vector3> rim, Vector3 a, Vector3 b)
    {
        const float tol = 0.35f;
        for (int i = 0; i < rim.Count - 1; i++)
        {
            bool forward = Vector3.Distance(rim[i], a) <= tol && Vector3.Distance(rim[i + 1], b) <= tol;
            bool back = Vector3.Distance(rim[i], b) <= tol && Vector3.Distance(rim[i + 1], a) <= tol;
            if (forward || back) return true;
        }
        return false;
    }

    /// <summary>
    /// A half-round notch in the outer edge is not its own polygon. It is the run of
    /// the outline between two corners. A drill centered on that chord cuts the notch.
    /// </summary>
    static void AddSemicircleNotches(
        List<IReadOnlyList<Vector3>> edges, List<DrillSite> holes, IReadOnlyList<Vector3> positions)
    {
        foreach (var edge in edges)
        {
            var poly = new List<Vector3>(edge.Count);
            foreach (var p in edge) poly.Add(p);
            if (poly.Count >= 2 && Vector3.Distance(poly[0], poly[^1]) <= 0.2f)
                poly.RemoveAt(poly.Count - 1);
            int n = poly.Count;
            if (n < 8) continue;
            var corners = new List<int>();
            for (int i = 0; i < n; i++)
            {
                var prev = poly[i] - poly[(i + n - 1) % n];
                var next = poly[(i + 1) % n] - poly[i];
                if (prev.LengthSquared() < 1e-6f || next.LengthSquared() < 1e-6f) continue;
                float dot = Math.Clamp(Vector3.Dot(Vector3.Normalize(prev), Vector3.Normalize(next)), -1f, 1f);
                if (MathF.Acos(dot) > 45f * MathF.PI / 180f)
                    corners.Add(i);
            }
            if (corners.Count < 2) continue;
            for (int c = 0; c < corners.Count; c++)
            {
                int from = corners[c];
                int to = corners[(c + 1) % corners.Count];
                var run = new List<Vector3>();
                for (int i = from; run.Count <= n; i = (i + 1) % n)
                {
                    run.Add(poly[i]);
                    if (i == to) break;
                }
                if (TrySemicircle(run, positions, out var site))
                    holes.Add(site);
            }
        }
    }

    static bool TrySemicircle(List<Vector3> run, IReadOnlyList<Vector3> positions, out DrillSite site)
    {
        site = null!;
        if (run.Count < 5) return false;
        var a = run[0];
        var b = run[^1];
        float chord = Vector3.Distance(a, b);
        if (chord < MinHoleDiameterMm) return false;
        var center = (a + b) * 0.5f;
        float radius = chord * 0.5f;
        float tol = MathF.Max(0.35f, radius * 0.08f);
        float bulge = 0f;
        foreach (var p in run)
        {
            if (MathF.Abs(Vector3.Distance(p, center) - radius) > tol) return false;
            float fromChord = DistToSegment(p, a, b);
            if (fromChord > bulge) bulge = fromChord;
        }
        // A half-circle stands about one radius off its chord. A fillet does not fit this circle.
        if (bulge < radius * 0.75f || bulge > radius * 1.15f) return false;

        var newell = Newell(run);
        var axis = newell.LengthSquared() > 1e-8f ? Vector3.Normalize(newell) : Vector3.UnitZ;
        if (axis.Z < -0.5f) axis = -axis;

        var mouth = center;
        float span = 0f;
        if (Extent(positions, center, axis, radius, out float minT, out float maxT))
        {
            mouth = center + axis * maxT;
            span = MathF.Max(0f, maxT - minT);
        }

        var rim = new List<Vector3>(run.Count + 1);
        rim.AddRange(run);
        rim.Add(run[0]);
        site = new DrillSite
        {
            Kind = DrillSiteKind.Hole,
            Position = mouth,
            Axis = axis,
            DetectedDiameterMm = chord,
            SpanMm = span,
            Included = false,
            Rim = rim,
            Cut = DrillCutKind.Round,
            ShapeName = "Hole",
        };
        return true;
    }

    static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-8f) return Vector3.Distance(p, a);
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector3.Distance(p, a + ab * t);
    }

    /// <summary>
    /// A through-hole has a mouth on each face. Keep the one that opens upward
    /// so the list shows one hole, entered from the top.
    /// </summary>
    static void DedupeMouths(List<DrillSite> holes)
    {
        var kept = new List<DrillSite>(holes.Count);
        foreach (var hole in holes)
        {
            int at = -1;
            for (int i = 0; i < kept.Count; i++)
            {
                if (!SameMouth(kept[i], hole)) continue;
                at = i;
                break;
            }
            if (at < 0) kept.Add(hole);
            else if (MouthScore(hole) > MouthScore(kept[at])) kept[at] = hole;
        }
        holes.Clear();
        holes.AddRange(kept);
        for (int i = 0; i < holes.Count; i++)
            holes[i].Id = i + 1;
    }

    static bool SameMouth(DrillSite a, DrillSite b)
    {
        var ax = a.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(a.Axis) : Vector3.UnitZ;
        var bx = b.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(b.Axis) : Vector3.UnitZ;
        if (MathF.Abs(Vector3.Dot(ax, bx)) < 0.85f) return false;
        float ra = a.DetectedDiameterMm * 0.5f;
        float rb = b.DetectedDiameterMm * 0.5f;
        if (ra < 0.5f || rb < 0.5f) return false;
        float ratio = ra > rb ? ra / rb : rb / ra;
        if (ratio > 1.35f) return false;
        var d = b.Position - a.Position;
        float along = Vector3.Dot(d, ax);
        float radial = (d - ax * along).Length();
        return radial <= 0.45f * MathF.Min(ra, rb);
    }

    static float MouthScore(DrillSite site)
    {
        var axis = site.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(site.Axis) : Vector3.UnitZ;
        return axis.Z * 1000f + site.SpanMm;
    }

    /// <summary>How far the mesh extends along <paramref name="outwardAxis"/> through <paramref name="mouth"/>.</summary>
    public static float MeasureSpan(
        IReadOnlyList<Vector3> positions, Vector3 mouth, Vector3 outwardAxis, float probeRadiusMm)
    {
        if (!Extent(positions, mouth, outwardAxis, probeRadiusMm, out float minT, out float maxT))
            return 0f;
        return MathF.Max(0f, maxT - minT);
    }

    public static bool TryClosestOnChain(
        IReadOnlyList<Vector3> chain, Vector3 hit, out Vector3 closest, out float distance)
    {
        closest = hit;
        distance = float.MaxValue;
        if (chain.Count == 0) return false;
        if (chain.Count == 1)
        {
            closest = chain[0];
            distance = Vector3.Distance(hit, closest);
            return true;
        }
        for (int i = 0; i < chain.Count - 1; i++)
        {
            var a = chain[i];
            var b = chain[i + 1];
            var p = ClosestOnSegment(a, b, hit);
            float d = Vector3.Distance(hit, p);
            if (d < distance)
            {
                distance = d;
                closest = p;
            }
        }
        return true;
    }

    /// <summary>Center and axis of a closed rim. The radius is the mean distance from that center.</summary>
    public static bool TryLoopFrame(
        IReadOnlyList<Vector3> chain, out Vector3 center, out Vector3 axis, out float radius)
    {
        center = default;
        axis = Vector3.UnitZ;
        radius = 0f;
        int n = chain.Count;
        if (n >= 2 && Vector3.Distance(chain[0], chain[n - 1]) < 1e-3f) n--;
        if (n < 3) return false;
        var poly = new List<Vector3>(n);
        for (int i = 0; i < n; i++) poly.Add(chain[i]);
        center = Vector3.Zero;
        foreach (var p in poly) center += p;
        center /= n;
        var newell = Newell(poly);
        if (newell.LengthSquared() > 1e-8f)
            axis = Vector3.Normalize(newell);
        float sum = 0f;
        foreach (var p in poly) sum += Radial(p, center, axis);
        radius = sum / n;
        return true;
    }

    static bool TryHole(
        List<Vector3> poly, Vector3 faceNormal, IReadOnlyList<Vector3> positions,
        List<Vector3> verts, List<(int A, int B, int C)> tris, out DrillSite site)
    {
        site = null!;
        // Four corners is a square. Three is only a triangle and stays an edge.
        if (poly.Count < 4) return false;
        var newell = Newell(poly);
        float nLen = newell.Length();
        if (nLen < 1e-4f) return false;
        var axis = newell / nLen;
        if (faceNormal.LengthSquared() > 1e-8f && Vector3.Dot(axis, faceNormal) < 0f)
            axis = -axis;

        var center = Vector3.Zero;
        foreach (var p in poly) center += p;
        center /= poly.Count;

        var radii = new List<float>(poly.Count * 2);
        float vertexSum = 0f;
        for (int i = 0; i < poly.Count; i++)
        {
            float vertexR = Radial(poly[i], center, axis);
            vertexSum += vertexR;
            radii.Add(vertexR);
            var mid = (poly[i] + poly[(i + 1) % poly.Count]) * 0.5f;
            radii.Add(Radial(mid, center, axis));
        }
        // The hole size is the rim. Edge midpoints sit inside a faceted circle,
        // so they only check that the loop is round.
        float vertexMean = vertexSum / poly.Count;
        float minorWidth = MinorWidth(poly, axis);
        if (vertexMean * 2f < MinHoleDiameterMm && minorWidth < MinHoleDiameterMm) return false;
        bool circular = IsCircular(poly, axis);
        float openProbe = MathF.Max(0.4f, minorWidth * 0.4f);
        if (!IsHoleOpening(verts, tris, center, axis, openProbe)) return false;

        float probe = MathF.Max(vertexMean, minorWidth * 0.5f) * 1.15f;
        Vector3 mouth = center;
        float span = 0f;
        if (Extent(positions, center, axis, probe, out float minT, out float maxT))
        {
            mouth = center + axis * maxT;
            span = MathF.Max(0f, maxT - minT);
        }

        site = new DrillSite
        {
            Kind = DrillSiteKind.Hole,
            Position = mouth,
            Axis = axis,
            DetectedDiameterMm = circular ? vertexMean * 2f : (minorWidth > 0.5f ? minorWidth : vertexMean * 2f),
            SpanMm = span,
            Included = false,
            Rim = ClosedRim(poly),
            Cut = circular ? DrillCutKind.Round : DrillCutKind.Profile,
            ShapeName = circular ? "Hole" : ProfileName(poly, axis),
        };
        if (circular)
            ClassifyRound(site, verts, tris);
        return true;
    }

    /// <summary>A ctrl-clicked edge keeps its outline when it is not a circle.</summary>
    public static void TagOpening(DrillSite site)
    {
        var rim = site.Rim;
        if (rim is null || rim.Count < 4) return;
        int n = rim.Count;
        bool closed = Vector3.Distance(rim[0], rim[n - 1]) < 0.2f;
        if (!closed) return;
        var poly = new List<Vector3>(n);
        int use = closed ? n - 1 : n;
        if (use < 4) return;
        for (int i = 0; i < use; i++) poly.Add(rim[i]);
        var axis = site.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(site.Axis) : Vector3.UnitZ;
        if (IsCircular(poly, axis)) return;
        site.Cut = DrillCutKind.Profile;
        site.ShapeName = ProfileName(poly, axis);
        float width = MinorWidth(poly, axis);
        if (width > 0.5f) site.DetectedDiameterMm = width;
    }

    static List<Vector3> ClosedRim(List<Vector3> poly)
    {
        var rim = new List<Vector3>(poly.Count + 1);
        rim.AddRange(poly);
        if (rim.Count >= 2)
            rim.Add(rim[0]);
        return rim;
    }

    /// <summary>
    /// Most of the loop sits on one radius. A square fails because its edge
    /// midpoints are far inside the corners. One stray vertex does not.
    /// </summary>
    static bool IsRound(List<float> radii)
    {
        if (radii.Count < 8) return false;
        var sorted = radii.ToArray();
        Array.Sort(sorted);
        float median = sorted[sorted.Length / 2];
        if (median < 1e-4f) return false;
        int close = 0;
        float band = median * MaxRadiusVariation;
        foreach (float r in radii)
            if (MathF.Abs(r - median) <= band) close++;
        return close >= radii.Count * 0.85f;
    }

    /// <summary>
    /// A circle stays a circle. A square, hexagon, or other few-sided opening does not,
    /// even when its corners sit on one radius.
    /// </summary>
    static bool IsCircular(List<Vector3> poly, Vector3 axis)
    {
        if (poly.Count < 3) return false;
        var center = Vector3.Zero;
        foreach (var p in poly) center += p;
        center /= poly.Count;
        var radii = new List<float>(poly.Count * 2);
        for (int i = 0; i < poly.Count; i++)
        {
            radii.Add(Radial(poly[i], center, axis));
            var mid = (poly[i] + poly[(i + 1) % poly.Count]) * 0.5f;
            radii.Add(Radial(mid, center, axis));
        }
        if (!IsRound(radii)) return false;
        // A slot with round ends puts most of its points on those ends, so the
        // radii look circular. The long outline is still not a hole to bore.
        PlanarSpan(poly, axis, out float width, out float height);
        float minor = MathF.Max(0.1f, MathF.Min(width, height));
        if (MathF.Max(width, height) / minor >= 1.35f) return false;
        int corners = SharpCorners(poly, axis);
        return corners == 0 || corners > 8;
    }

    static int SharpCorners(List<Vector3> poly, Vector3 axis)
    {
        int n = poly.Count;
        if (n < 3) return 0;
        DrillMarkers.Basis(axis, out var u, out var v);
        var uv = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            var d = poly[i];
            uv[i] = new Vector2(Vector3.Dot(d, u), Vector3.Dot(d, v));
        }
        int corners = 0;
        for (int i = 0; i < n; i++)
        {
            var prev = uv[i] - uv[(i + n - 1) % n];
            var next = uv[(i + 1) % n] - uv[i];
            if (prev.LengthSquared() < 1e-6f || next.LengthSquared() < 1e-6f) continue;
            prev = Vector2.Normalize(prev);
            next = Vector2.Normalize(next);
            float dot = Math.Clamp(Vector2.Dot(prev, next), -1f, 1f);
            if (MathF.Acos(dot) > 35f * MathF.PI / 180f) corners++;
        }
        return corners;
    }

    static float MinorWidth(List<Vector3> poly, Vector3 axis)
    {
        if (poly.Count < 2) return 0f;
        PlanarSpan(poly, axis, out float width, out float height);
        return MathF.Min(width, height);
    }

    static void PlanarSpan(List<Vector3> poly, Vector3 axis, out float width, out float height)
    {
        DrillMarkers.Basis(axis, out var u, out var v);
        float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
        foreach (var p in poly)
        {
            float uu = Vector3.Dot(p, u);
            float vv = Vector3.Dot(p, v);
            if (uu < minU) minU = uu;
            if (uu > maxU) maxU = uu;
            if (vv < minV) minV = vv;
            if (vv > maxV) maxV = vv;
        }
        width = MathF.Max(0f, maxU - minU);
        height = MathF.Max(0f, maxV - minV);
    }

    static string ProfileName(List<Vector3> poly, Vector3 axis)
    {
        PlanarSpan(poly, axis, out float width, out float height);
        float minor = MathF.Max(0.1f, MathF.Min(width, height));
        float aspect = MathF.Max(width, height) / minor;
        int corners = SharpCorners(poly, axis);
        if (corners == 4 && aspect < 1.35f) return "Square";
        if (aspect >= 1.65f) return "Slot";
        return "Polygon";
    }

    /// <summary>
    /// A round mouth may still be a counterbore or a countersink. The wall radius
    /// along the axis tells which: a step, a cone, or a straight cylinder.
    /// </summary>
    static void ClassifyRound(DrillSite site, List<Vector3> verts, List<(int A, int B, int C)> tris)
    {
        float mouthR = MathF.Max(0.5f, site.DetectedDiameterMm * 0.5f);
        var axis = site.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(site.Axis) : Vector3.UnitZ;
        float limit = site.SpanMm > 1f ? site.SpanMm : mouthR * 4f;
        // A short countersink on a deep hole is only a few millimetres. A step
        // coarser than 1 mm can turn that cone into a single drop.
        float step = Math.Clamp(limit / 24f, 0.5f, 1f);
        var samples = new List<(float Depth, float Radius)>();
        for (float d = step * 0.5f; d < limit; d += step)
        {
            float r = OpeningRadius(verts, tris, site.Position, axis, d, mouthR * 2.4f);
            if (r < 0f)
            {
                if (samples.Count >= 3) break;
                continue;
            }
            samples.Add((d, r));
        }
        if (samples.Count < 4) return;
        float top = 0.5f * (samples[0].Radius + samples[1].Radius);
        float bottom = 0.5f * (samples[^1].Radius + samples[^2].Radius);
        if (top < 0.6f) return;
        if (top < bottom * 1.28f && bottom < top * 1.28f)
        {
            // The rim can be a circle drawn around a smaller hole. The wall is the size.
            float wall = top * 2f;
            if (wall > 1f && site.DetectedDiameterMm > wall * 1.35f)
            {
                site.DetectedDiameterMm = wall;
                site.Rim = null;
            }
            return;
        }

        // The small end is the deepest point before the hole opens up again.
        // A counterbore sits wide, then drops. A countersink keeps falling.
        int tip = 0;
        float minR = samples[0].Radius;
        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i].Radius < minR - 0.05f)
            {
                minR = samples[i].Radius;
                tip = i;
            }
            else if (tip > 0 && samples[i].Radius > minR + MathF.Max(0.4f, minR * 0.35f))
                break;
        }
        float totalDrop = samples[0].Radius - minR;
        if (tip < 2 || totalDrop < MathF.Max(0.6f, samples[0].Radius * 0.18f)) return;

        float band = MathF.Max(0.25f, samples[0].Radius * 0.04f);
        int flatRun = 0;
        for (int i = 0; i < tip; i++)
        {
            if (samples[0].Radius - samples[i].Radius > band) break;
            flatRun++;
        }
        int sloping = 0;
        for (int i = 1; i <= tip; i++)
            if (samples[i - 1].Radius - samples[i].Radius > 0.15f) sloping++;
        bool cone = sloping >= 2 && flatRun <= Math.Max(1, tip / 3);
        if (!cone && flatRun < 2) return;
        if (!cone && samples[tip].Depth >= limit * 0.92f) return;

        site.Cut = cone ? DrillCutKind.Countersink : DrillCutKind.Counterbore;
        site.ShapeName = cone ? "Countersink" : "Counterbore";
        // The mouth rim is the size at the top. A sample just inside a chamfer
        // can read smaller, and the bore still has to open the mouth.
        site.DetectedDiameterMm = MathF.Max(site.DetectedDiameterMm, top * 2f);
        site.MinorDiameterMm = minR * 2f;
        site.ShoulderDepthMm = samples[tip].Depth;
    }

    static float OpeningRadius(
        List<Vector3> verts, List<(int A, int B, int C)> tris,
        Vector3 mouth, Vector3 axis, float depth, float maxR)
    {
        var origin = mouth - axis * depth;
        float best = float.MaxValue;
        bool any = false;
        float maxR2 = maxR * maxR;
        foreach (var (ia, ib, ic) in tris)
        {
            var a = verts[ia];
            var b = verts[ib];
            var c = verts[ic];
            if (Radial(a, origin, axis) > maxR && Radial(b, origin, axis) > maxR && Radial(c, origin, axis) > maxR)
                continue;
            var n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared() < 1e-12f) continue;
            n = Vector3.Normalize(n);
            // A countersink wall leans. Skip only a face that is nearly horizontal,
            // which is the floor or a counterbore shelf, not the cone.
            if (MathF.Abs(Vector3.Dot(n, axis)) > 0.90f) continue;
            var hit0 = Vector3.Zero;
            var hit1 = Vector3.Zero;
            int h = 0;
            void Take(Vector3 p, Vector3 q)
            {
                if (h >= 2) return;
                float dp = Vector3.Dot(p - origin, axis);
                float dq = Vector3.Dot(q - origin, axis);
                if (dp * dq > 1e-8f) return;
                float den = dp - dq;
                if (MathF.Abs(den) < 1e-8f) return;
                var hit = p + (q - p) * (dp / den);
                if (h == 0) hit0 = hit;
                else hit1 = hit;
                h++;
            }
            Take(a, b);
            Take(b, c);
            Take(c, a);
            if (h < 2) continue;
            float dist = DistanceToSegment(origin, hit0, hit1);
            if (dist < 0.2f || dist * dist > maxR2) continue;
            if (dist < best)
            {
                best = dist;
                any = true;
            }
        }
        return any ? best : -1f;
    }

    static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-10f) return Vector3.Distance(point, a);
        float t = Math.Clamp(Vector3.Dot(point - a, ab) / len2, 0f, 1f);
        return Vector3.Distance(point, a + ab * t);
    }

    static void MergeInnerHoles(List<DrillSite> holes)
    {
        var drop = new bool[holes.Count];
        for (int i = 0; i < holes.Count; i++)
        {
            if (drop[i] || holes[i].Cut == DrillCutKind.Profile) continue;
            for (int j = i + 1; j < holes.Count; j++)
            {
                if (drop[j] || holes[j].Cut == DrillCutKind.Profile) continue;
                if (!TryHostInner(holes[i], holes[j], out int hostSide, out int innerSide)) continue;
                int hostAt = hostSide == 0 ? i : j;
                int innerAt = innerSide == 0 ? i : j;
                var host = holes[hostAt];
                var inner = holes[innerAt];
                var axis = host.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(host.Axis) : Vector3.UnitZ;
                float along = MathF.Abs(Vector3.Dot(host.Position - inner.Position, axis));
                if (host.MinorDiameterMm < 0.5f)
                    host.MinorDiameterMm = inner.DetectedDiameterMm;
                if (host.Cut == DrillCutKind.Round)
                {
                    if (along < 0.4f) continue;
                    host.Cut = DrillCutKind.Counterbore;
                    host.ShapeName = "Counterbore";
                    host.ShoulderDepthMm = along;
                }
                else if (host.ShoulderDepthMm < 0.2f && along > 0.4f)
                    host.ShoulderDepthMm = along;
                drop[innerAt] = true;
            }
        }
        for (int i = holes.Count - 1; i >= 0; i--)
            if (drop[i]) holes.RemoveAt(i);
        for (int i = 0; i < holes.Count; i++)
            holes[i].Id = i + 1;
    }

    static bool TryHostInner(DrillSite a, DrillSite b, out int host, out int inner)
    {
        host = 0;
        inner = 1;
        var axis = a.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(a.Axis) : Vector3.UnitZ;
        var other = b.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(b.Axis) : Vector3.UnitZ;
        if (MathF.Abs(Vector3.Dot(axis, other)) < 0.85f) return false;
        float da = a.DetectedDiameterMm;
        float db = b.DetectedDiameterMm;
        if (da < 0.5f || db < 0.5f) return false;
        float ratio = da > db ? da / db : db / da;
        if (ratio < 1.25f) return false;
        bool aHost = da >= db;
        var major = aHost ? a : b;
        var minor = aHost ? b : a;
        float radial = (minor.Position - major.Position - axis * Vector3.Dot(minor.Position - major.Position, axis)).Length();
        if (radial > 0.45f * minor.DetectedDiameterMm) return false;
        host = aHost ? 0 : 1;
        inner = aHost ? 1 : 0;
        return true;
    }

    static (List<Vector3> Verts, List<(int A, int B, int C)> Tris) Weld(
        IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices)
    {
        var map = new Dictionary<(int, int, int), int>();
        var verts = new List<Vector3>();
        int WeldOne(Vector3 p)
        {
            var key = (
                (int)MathF.Round(p.X / WeldMm),
                (int)MathF.Round(p.Y / WeldMm),
                (int)MathF.Round(p.Z / WeldMm));
            if (map.TryGetValue(key, out int id)) return id;
            id = verts.Count;
            map[key] = id;
            verts.Add(p);
            return id;
        }

        var tris = new List<(int, int, int)>();
        for (int t = 0; t + 2 < indices.Count; t += 3)
        {
            int ia = indices[t], ib = indices[t + 1], ic = indices[t + 2];
            if ((uint)ia >= (uint)positions.Count || (uint)ib >= (uint)positions.Count || (uint)ic >= (uint)positions.Count)
                continue;
            int a = WeldOne(positions[ia]);
            int b = WeldOne(positions[ib]);
            int c = WeldOne(positions[ic]);
            if (a == b || b == c || c == a) continue;
            tris.Add((a, b, c));
        }
        return (verts, tris);
    }

    readonly record struct DirectedEdge(int From, int To, Vector3 FaceNormal);

    static List<DirectedEdge> BoundaryEdges(List<Vector3> verts, List<(int A, int B, int C)> tris)
    {
        var count = new Dictionary<(int, int), int>();
        var directed = new List<DirectedEdge>(tris.Count * 3);
        void Add(int a, int b, Vector3 n)
        {
            var key = a < b ? (a, b) : (b, a);
            count.TryGetValue(key, out int c);
            count[key] = c + 1;
            directed.Add(new DirectedEdge(a, b, n));
        }
        foreach (var (a, b, c) in tris)
        {
            var n = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
            if (n.LengthSquared() > 1e-12f) n = Vector3.Normalize(n);
            Add(a, b, n);
            Add(b, c, n);
            Add(c, a, n);
        }
        var boundary = new List<DirectedEdge>();
        foreach (var e in directed)
        {
            var key = e.From < e.To ? (e.From, e.To) : (e.To, e.From);
            if (count.TryGetValue(key, out int c) && c == 1)
                boundary.Add(e);
        }
        return boundary;
    }

    static List<ChainWalk> FeatureChains(List<Vector3> verts, List<(int A, int B, int C)> tris)
    {
        var faces = new Dictionary<(int, int), List<Vector3>>();
        void Add(int a, int b, Vector3 n)
        {
            var key = a < b ? (a, b) : (b, a);
            if (!faces.TryGetValue(key, out var list))
            {
                list = [];
                faces[key] = list;
            }
            list.Add(n);
        }
        foreach (var (a, b, c) in tris)
        {
            var n = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
            if (n.LengthSquared() > 1e-12f) n = Vector3.Normalize(n);
            Add(a, b, n);
            Add(b, c, n);
            Add(c, a, n);
        }

        var sharp = new List<(int A, int B, Vector3 N1, Vector3 N2)>();
        foreach (var (key, normals) in faces)
        {
            // Duplicate triangles make an edge show up more than twice. The
            // sharpest pair is still the rim between the face and the wall.
            if (normals.Count < 2) continue;
            float bestAngle = 0f;
            var nA = normals[0];
            var nB = normals[1];
            for (int i = 0; i < normals.Count; i++)
            {
                for (int j = i + 1; j < normals.Count; j++)
                {
                    float dot = Math.Clamp(Vector3.Dot(normals[i], normals[j]), -1f, 1f);
                    float angle = MathF.Acos(dot);
                    if (angle <= bestAngle) continue;
                    bestAngle = angle;
                    nA = normals[i];
                    nB = normals[j];
                }
            }
            if (bestAngle < SharpRadians) continue;
            sharp.Add((key.Item1, key.Item2, nA, nB));
        }
        return FaceLoops(verts, sharp);
    }

    /// <summary>
    /// Walk each smooth face's sharp edges as its own loop. A square corner is a
    /// right angle, so the straightest next edge is a tie with the wall and the
    /// walk would leave the opening. Edges that share the face normal do not.
    /// </summary>
    static List<ChainWalk> FaceLoops(List<Vector3> verts, List<(int A, int B, Vector3 N1, Vector3 N2)> sharp)
    {
        var seeds = new List<(int A, int B, Vector3 N)>(sharp.Count * 2);
        foreach (var e in sharp)
        {
            seeds.Add((e.A, e.B, e.N1));
            seeds.Add((e.A, e.B, e.N2));
        }
        var clusterOf = new int[seeds.Count];
        var reps = new List<Vector3>();
        for (int i = 0; i < seeds.Count; i++)
        {
            int found = -1;
            for (int c = 0; c < reps.Count; c++)
            {
                if (Vector3.Dot(seeds[i].N, reps[c]) > 0.97f)
                {
                    found = c;
                    break;
                }
            }
            if (found < 0)
            {
                found = reps.Count;
                reps.Add(seeds[i].N);
            }
            clusterOf[i] = found;
        }

        var chains = new List<ChainWalk>();
        for (int c = 0; c < reps.Count; c++)
        {
            var adj = new Dictionary<int, List<(int Other, Vector3 N1, Vector3 N2)>>();
            var unused = new HashSet<(int, int)>();
            for (int i = 0; i < seeds.Count; i++)
            {
                if (clusterOf[i] != c) continue;
                var e = seeds[i];
                unused.Add(e.A < e.B ? (e.A, e.B) : (e.B, e.A));
                void Link(int from, int to)
                {
                    if (!adj.TryGetValue(from, out var list))
                    {
                        list = [];
                        adj[from] = list;
                    }
                    list.Add((to, e.N, reps[c]));
                }
                Link(e.A, e.B);
                Link(e.B, e.A);
            }

            while (unused.Count > 0)
            {
                (int A, int B) seed = default;
                foreach (var e in unused) { seed = e; break; }
                var chain = new ChainWalk();
                int start = seed.A;
                int prev = seed.A;
                int cur = seed.B;
                chain.Indices.Add(prev);
                var normals = new List<Vector3>();
                int guard = unused.Count + 2;
                while (guard-- > 0)
                {
                    var key = prev < cur ? (prev, cur) : (cur, prev);
                    if (!unused.Remove(key)) break;
                    chain.Indices.Add(cur);
                    if (adj.TryGetValue(prev, out var links))
                    {
                        foreach (var link in links)
                        {
                            if (link.Other != cur) continue;
                            normals.Add(link.N1);
                            normals.Add(link.N2);
                            break;
                        }
                    }
                    if (cur == start)
                    {
                        chain.Closed = true;
                        break;
                    }
                    if (!adj.TryGetValue(cur, out var forward)) break;
                    var incoming = cur == prev ? Vector3.UnitX : Vector3.Normalize(verts[cur] - verts[prev]);
                    int chosen = -1;
                    float best = -2f;
                    foreach (var link in forward)
                    {
                        if (link.Other == prev) continue;
                        var ek = cur < link.Other ? (cur, link.Other) : (link.Other, cur);
                        if (!unused.Contains(ek) && link.Other != start) continue;
                        var outward = Vector3.Normalize(verts[link.Other] - verts[cur]);
                        float score = Vector3.Dot(incoming, outward);
                        if (score > best)
                        {
                            best = score;
                            chosen = link.Other;
                        }
                    }
                    if (chosen < 0) break;
                    prev = cur;
                    cur = chosen;
                }
                if (chain.Closed && chain.Indices.Count > 1 && chain.Indices[0] == chain.Indices[^1])
                    chain.Indices.RemoveAt(chain.Indices.Count - 1);
                // A face fragment of one edge is already part of the real loop.
                if (chain.Indices.Count < 3) continue;
                var poly = new List<Vector3>(chain.Indices.Count);
                foreach (int i in chain.Indices) poly.Add(verts[i]);
                var newell = Newell(poly);
                if (newell.LengthSquared() > 1e-8f)
                {
                    var hint = Vector3.Normalize(newell);
                    Vector3 opening = hint;
                    float bestN = 0f;
                    foreach (var n in normals)
                    {
                        float d = MathF.Abs(Vector3.Dot(n, hint));
                        if (d > bestN) { bestN = d; opening = n; }
                    }
                    chain.FaceNormal = Vector3.Dot(hint, opening) < 0f ? -hint : hint;
                }
                chains.Add(chain);
            }
        }
        return chains;
    }

    static bool IsHoleOpening(
        List<Vector3> verts, List<(int A, int B, int C)> tris,
        Vector3 center, Vector3 axis, float radius)
    {
        DrillMarkers.Basis(axis, out var u, out var v);
        int inside = 0;
        for (int i = 0; i < 8; i++)
        {
            float a = MathF.Tau * i / 8f;
            var dir = u * MathF.Cos(a) + v * MathF.Sin(a);
            if (Covered(verts, tris, center + dir * (radius * 0.72f), axis)) inside++;
        }
        // An open middle is the hole. A solid cap, such as a boss top, covers it.
        return inside <= 2;
    }

    static bool Covered(
        List<Vector3> verts, List<(int A, int B, int C)> tris, Vector3 point, Vector3 axis)
    {
        DrillMarkers.Basis(axis, out var u, out var v);
        var p = new Vector2(Vector3.Dot(point, u), Vector3.Dot(point, v));
        foreach (var (a, b, c) in tris)
        {
            float ta = Vector3.Dot(verts[a] - point, axis);
            float tb = Vector3.Dot(verts[b] - point, axis);
            float tc = Vector3.Dot(verts[c] - point, axis);
            // The whole triangle has to sit in the rim plane. A wall that merely
            // crosses that plane must not count as a cap over the hole.
            if (MathF.Abs(ta) > 0.8f || MathF.Abs(tb) > 0.8f || MathF.Abs(tc) > 0.8f) continue;
            var pa = new Vector2(Vector3.Dot(verts[a], u), Vector3.Dot(verts[a], v));
            var pb = new Vector2(Vector3.Dot(verts[b], u), Vector3.Dot(verts[b], v));
            var pc = new Vector2(Vector3.Dot(verts[c], u), Vector3.Dot(verts[c], v));
            if (PointInTri(p, pa, pb, pc)) return true;
        }
        return false;
    }

    static bool PointInTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        var v0 = c - a;
        var v1 = b - a;
        var v2 = p - a;
        float dot00 = Vector2.Dot(v0, v0);
        float dot01 = Vector2.Dot(v0, v1);
        float dot02 = Vector2.Dot(v0, v2);
        float dot11 = Vector2.Dot(v1, v1);
        float dot12 = Vector2.Dot(v1, v2);
        float denom = dot00 * dot11 - dot01 * dot01;
        if (MathF.Abs(denom) < 1e-8f) return false;
        float inv = 1f / denom;
        float u = (dot11 * dot02 - dot01 * dot12) * inv;
        float v = (dot00 * dot12 - dot01 * dot02) * inv;
        return u >= -0.02f && v >= -0.02f && u + v <= 1.02f;
    }

    static float Radial(Vector3 point, Vector3 center, Vector3 axis)
    {
        var d = point - center;
        float axial = Vector3.Dot(d, axis);
        return (d - axis * axial).Length();
    }

    sealed class ChainWalk
    {
        public List<int> Indices { get; } = [];
        public bool Closed { get; set; }
        public Vector3 FaceNormal { get; set; }
    }

    static List<ChainWalk> Walk(List<DirectedEdge> boundary)
    {
        var next = new Dictionary<int, List<DirectedEdge>>();
        var unused = new HashSet<(int, int)>();
        foreach (var e in boundary)
        {
            if (!next.TryGetValue(e.From, out var list))
            {
                list = [];
                next[e.From] = list;
            }
            list.Add(e);
            unused.Add((e.From, e.To));
        }

        var chains = new List<ChainWalk>();
        while (unused.Count > 0)
        {
            (int From, int To) seed = default;
            foreach (var e in unused) { seed = e; break; }
            var chain = new ChainWalk();
            int start = seed.From;
            int a = seed.From;
            int b = seed.To;
            chain.Indices.Add(a);
            var normal = Vector3.Zero;
            int guard = unused.Count + 2;
            while (guard-- > 0)
            {
                if (!unused.Remove((a, b))) break;
                chain.Indices.Add(b);
                if (next.TryGetValue(a, out var outs))
                {
                    foreach (var e in outs)
                        if (e.To == b) { normal += e.FaceNormal; break; }
                }
                if (b == start)
                {
                    chain.Closed = true;
                    break;
                }
                if (!next.TryGetValue(b, out var forward)) break;
                int chosen = -1;
                foreach (var e in forward)
                {
                    if (unused.Contains((b, e.To))) { chosen = e.To; break; }
                }
                if (chosen < 0) break;
                a = b;
                b = chosen;
            }
            if (chain.Closed && chain.Indices.Count > 1
                && chain.Indices[0] == chain.Indices[^1])
                chain.Indices.RemoveAt(chain.Indices.Count - 1);
            if (normal.LengthSquared() > 1e-12f)
                chain.FaceNormal = Vector3.Normalize(normal);
            if (chain.Indices.Count >= 2)
                chains.Add(chain);
        }
        return chains;
    }

    static bool Extent(
        IReadOnlyList<Vector3> positions, Vector3 origin, Vector3 axis, float radius,
        out float minT, out float maxT)
    {
        minT = 0f;
        maxT = 0f;
        if (axis.LengthSquared() < 1e-12f) axis = Vector3.UnitZ;
        else axis = Vector3.Normalize(axis);
        float r2 = MathF.Max(0.25f, radius) * MathF.Max(0.25f, radius);
        bool any = false;
        for (int i = 0; i < positions.Count; i++)
        {
            var d = positions[i] - origin;
            float t = Vector3.Dot(d, axis);
            var radial = d - axis * t;
            if (radial.LengthSquared() > r2) continue;
            if (!any) { minT = maxT = t; any = true; }
            else
            {
                if (t < minT) minT = t;
                if (t > maxT) maxT = t;
            }
        }
        return any;
    }

    static Vector3 Newell(List<Vector3> poly)
    {
        var n = Vector3.Zero;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            n.X += (a.Y - b.Y) * (a.Z + b.Z);
            n.Y += (a.Z - b.Z) * (a.X + b.X);
            n.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        return n;
    }

    static float ChainLength(List<Vector3> poly)
    {
        float s = 0f;
        for (int i = 0; i < poly.Count - 1; i++)
            s += Vector3.Distance(poly[i], poly[i + 1]);
        if (poly.Count > 2) s += Vector3.Distance(poly[^1], poly[0]);
        return s;
    }

    static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-12f) return a;
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f);
        return a + ab * t;
    }
}

/// <summary>Line segments for edge chains, hole circles, and the drill-depth line on holes that are on.</summary>
public static class DrillMarkers
{
    public static List<(Vector3 Pos, Vector3 Color)> Build(
        IReadOnlyList<DrillSite> sites,
        IReadOnlyList<IReadOnlyList<Vector3>> edges,
        int activeId,
        float breakthroughMm)
    {
        var segs = new List<(Vector3, Vector3)>();
        var edgeCol = new Vector3(0.45f, 0.7f, 0.85f);
        IReadOnlyList<Vector3>? activeRim = null;
        foreach (var site in sites)
        {
            if (site.Id != activeId) continue;
            activeRim = site.Rim;
            break;
        }
        var highlight = new Vector3(1f, 0.9f, 0.2f);
        foreach (var chain in edges)
        {
            bool selected = activeRim is not null && ReferenceEquals(chain, activeRim);
            var color = selected ? highlight : edgeCol;
            for (int i = 0; i < chain.Count - 1; i++)
            {
                segs.Add((chain[i], color));
                segs.Add((chain[i + 1], color));
            }
        }
        foreach (var site in sites)
            AppendSite(segs, site, site.Id == activeId, edgeCol, breakthroughMm);
        return segs;
    }

    static void AppendSite(
        List<(Vector3, Vector3)> segs, DrillSite site, bool active, Vector3 edgeCol, float breakthroughMm)
    {
        var axis = site.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(site.Axis) : Vector3.UnitZ;
        Basis(axis, out var u, out var v);
        bool hole = site.Kind == DrillSiteKind.Hole;
        // A hole that is not on the drill stays the same colour as the other edges.
        Vector3 color = hole && !site.Included
            ? edgeCol
            : site.Kind switch
            {
                DrillSiteKind.Hole when active => new Vector3(1f, 0.9f, 0.2f),
                DrillSiteKind.Hole => new Vector3(1f, 0.45f, 0.05f),
                _ when active => new Vector3(1f, 0.95f, 0.4f),
                _ => new Vector3(0.3f, 0.9f, 0.45f),
            };
        float radius = hole ? MathF.Max(1f, site.DiameterMm * 0.5f) : 4f;
        if (site.Rim is { Count: >= 2 } rim && (hole || active))
        {
            for (int i = 0; i < rim.Count - 1; i++)
            {
                segs.Add((rim[i], color));
                segs.Add((rim[i + 1], color));
            }
        }
        else if (hole)
        {
            const int n = 24;
            for (int i = 0; i < n; i++)
            {
                float a0 = MathF.Tau * i / n;
                float a1 = MathF.Tau * (i + 1) / n;
                var p0 = site.Position + (u * MathF.Cos(a0) + v * MathF.Sin(a0)) * radius;
                var p1 = site.Position + (u * MathF.Cos(a1) + v * MathF.Sin(a1)) * radius;
                segs.Add((p0, color));
                segs.Add((p1, color));
            }
        }
        else
        {
            float tick = MathF.Min(radius * 0.45f, 4f);
            segs.Add((site.Position - u * tick, color));
            segs.Add((site.Position + u * tick, color));
            segs.Add((site.Position - v * tick, color));
            segs.Add((site.Position + v * tick, color));
        }
        // The center line is the plunge the tool will cut, and only on a hole that is on.
        if (!site.Included) return;
        float depth = site.DepthMm(breakthroughMm);
        if (depth <= 0.05f) return;
        segs.Add((site.Position, color));
        segs.Add((site.Position - axis * depth, color));
    }

    public static void Basis(Vector3 axis, out Vector3 u, out Vector3 v)
    {
        var hint = MathF.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        u = Vector3.Normalize(Vector3.Cross(hint, axis));
        v = Vector3.Cross(axis, u);
    }
}
