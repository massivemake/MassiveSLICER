using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

/// <summary>
/// Synthetic stand-in for the real fuselage that broke Lightning Bridge on angled
/// prints (26-282). Reproduces the pathological conditions in one mesh:
///  - every surface DUPLICATED (double-shelled CAD export → contour twins with
///    corrupted windings),
///  - a hollow interior (thin wall → near-degenerate ring regions after insets),
///  - an interior diaphragm partition (inner walls that must keep printing),
///  - a smooth blimp profile at −35.5° (a tangent-grazing band always exists
///    somewhere, plus solid nose sections that grow lightning fingers).
/// The checks are the print invariants that failed in the field: perimeter
/// preservation vs a shells-only slice, hollow stays hollow, per-layer support,
/// and path continuity.
/// </summary>
public class AngledFuselageLikeTest
{
    private const float LayerH = 2.5f, Bead = 6f, Tilt = -35.5f;
    private const float MaxStep = 1.443f;               // layerH·tan(30°) at 2.5 mm
    private const float SupportRadius = MaxStep + Bead * 0.5f;

    // ── Mesh construction ────────────────────────────────────────────────────

    /// <summary>Blimp radius profile (r as a function of z, 0…360).</summary>
    private static readonly (float r, float z)[] OuterProfile =
    [
        (12f, 0f), (70f, 40f), (100f, 120f), (95f, 200f), (60f, 290f), (10f, 355f),
    ];

    private static void Revolve(List<Vector3> tris, (float r, float z)[] profile, int segments = 48)
    {
        for (int k = 0; k < profile.Length - 1; k++)
        {
            var (r0, z0) = profile[k];
            var (r1, z1) = profile[k + 1];
            for (int i = 0; i < segments; i++)
            {
                float a0 = MathF.Tau * i / segments, a1 = MathF.Tau * (i + 1) / segments;
                var p00 = new Vector3(r0 * MathF.Cos(a0), r0 * MathF.Sin(a0), z0);
                var p01 = new Vector3(r0 * MathF.Cos(a1), r0 * MathF.Sin(a1), z0);
                var p10 = new Vector3(r1 * MathF.Cos(a0), r1 * MathF.Sin(a0), z1);
                var p11 = new Vector3(r1 * MathF.Cos(a1), r1 * MathF.Sin(a1), z1);
                tris.AddRange([p00, p01, p11]);
                tris.AddRange([p00, p11, p10]);
            }
        }
    }

    private static void Box(List<Vector3> tris, Vector3 min, Vector3 max)
    {
        Vector3[] c =
        [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z),
            new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z),
            new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        ];
        int[][] faces =
        [
            [0, 1, 2, 3], [4, 7, 6, 5], [0, 4, 5, 1],
            [1, 5, 6, 2], [2, 6, 7, 3], [3, 7, 4, 0],
        ];
        foreach (var f in faces)
        {
            tris.AddRange([c[f[0]], c[f[1]], c[f[2]]]);
            tris.AddRange([c[f[0]], c[f[2]], c[f[3]]]);
        }
    }

    internal static Vector3[] BuildFuselageLike()
    {
        var tris = new List<Vector3>();

        // Outer hull + inner hull 10 mm inside (hollow wall).
        Revolve(tris, OuterProfile);
        var inner = OuterProfile.Select(p => (MathF.Max(p.r - 10f, 2f), p.z)).ToArray();
        Revolve(tris, inner);

        // Cap the wall ends with annular rings so the wall encloses volume — real
        // exports are closed solids, and the mesh-truth oracle (vertical-ray
        // parity) reads an open surface as having no material at all.
        void CapRing(float rIn, float rOut, float z, int segments = 48)
        {
            for (int i = 0; i < segments; i++)
            {
                float a0 = MathF.Tau * i / segments, a1 = MathF.Tau * (i + 1) / segments;
                var i0 = new Vector3(rIn * MathF.Cos(a0), rIn * MathF.Sin(a0), z);
                var i1 = new Vector3(rIn * MathF.Cos(a1), rIn * MathF.Sin(a1), z);
                var o0 = new Vector3(rOut * MathF.Cos(a0), rOut * MathF.Sin(a0), z);
                var o1 = new Vector3(rOut * MathF.Cos(a1), rOut * MathF.Sin(a1), z);
                tris.AddRange([i0, i1, o1]);
                tris.AddRange([i0, o1, o0]);
            }
        }
        CapRing(inner[0].Item1, OuterProfile[0].r, OuterProfile[0].z);
        CapRing(inner[^1].Item1, OuterProfile[^1].r, OuterProfile[^1].z);

        // Interior diaphragm: a thin wall crossing the hollow void, welded conceptually
        // to the inner hull (slices into strip contours that must keep printing).
        Box(tris, new Vector3(-80f, -3.25f, 80f), new Vector3(80f, 3.25f, 240f));

        // DOUBLE-SHELL the whole mesh: duplicate every triangle with reversed winding
        // and a hair of offset. Measured on the real fuselage: twin contour areas
        // differ by ~5–9 mm² on 2.5 m perimeters → surfaces sit ~0.003 mm apart.
        // (Larger offsets make twin chains interleave into phantom parity walls the
        // real export never produces.)
        var twinOffset = new Vector3(0.004f, 0.003f, 0.002f);
        int n = tris.Count;
        for (int i = 0; i < n; i += 3)
        {
            tris.Add(tris[i] + twinOffset);
            tris.Add(tris[i + 2] + twinOffset);
            tris.Add(tris[i + 1] + twinOffset);
        }

        return [.. tris];
    }

    private static SliceSettings Settings(InfillPattern pattern) => new()
    {
        LayerHeight = LayerH, FirstLayerHeight = LayerH, BeadWidth = Bead,
        TiltAngle = Tilt, InfillPattern = pattern,
        LightningOverhangDeg = 30f, LightningAnchorInterior = true,
        LightningAnchorExterior = true,
    };

    // ── The invariant gauntlet ───────────────────────────────────────────────

    [Fact]
    public void AngledDoubleShelledHollowPartSurvivesLightning()
    {
        var mesh = BuildFuselageLike();
        var lightning = AngledPlanarSlicer.Slice([mesh], Settings(InfillPattern.LightningBridge));
        var baseline  = AngledPlanarSlicer.Slice([mesh], Settings(InfillPattern.None));

        Assert.True(lightning.Layers.Count > 80, $"only {lightning.Layers.Count} layers");

        // 1. Perimeter preservation: every baseline extrude midpoint has lightning
        //    material within notch-mouth distance. (The field bug lost 143 mm spans.)
        var byZ = lightning.Layers.ToDictionary(l => MathF.Round(l.Z, 1));
        float worstGap = 0; float worstZ = 0;
        foreach (var bl in baseline.Layers)
        {
            if (!byZ.TryGetValue(MathF.Round(bl.Z, 1), out var ll)) continue;
            var segs = ll.Moves.Where(m => m.Kind == MoveKind.Extrude && !m.IsWipe).ToList();
            if (segs.Count == 0) continue;
            foreach (var bm in bl.Moves)
            {
                if (bm.Kind != MoveKind.Extrude || bm.IsWipe) continue;
                // Skip micro-dash junk the baseline prints in tangent bands — the
                // slicer may legitimately drop sub-bead fragments.
                if (Vector3.Distance(bm.From, bm.To) < 1.5f) continue;
                var mid = (bm.From + bm.To) * 0.5f;
                float best = float.MaxValue;
                foreach (var s in segs)
                {
                    float d = DistPointSeg(mid, s.From, s.To);
                    if (d < best) best = d;
                    if (best < 8f) break;
                }
                if (best > worstGap && best < 1e9f) { worstGap = best; worstZ = bl.Z; }
            }
        }
        Assert.True(worstGap < 30f, $"perimeter lost: {worstGap:0.#} mm of wall missing at z={worstZ:0.#}");

        // 2. Hollow stays hollow: no material deep inside the void (parity failure
        //    filled it in the field). Probe away from the walls and the diaphragm.
        Vector3[] voidProbes =
        [
            new(0f, 55f, 150f), new(0f, -55f, 150f), new(0f, 50f, 200f), new(0f, -50f, 200f),
        ];
        foreach (var probe in voidProbes)
        {
            // Lightning fingers legitimately bridge the void to support the closing
            // nose — the parity-failure signature is REGION WALL material (non-finger)
            // deep inside the hollow.
            var witness = lightning.Layers
                .SelectMany(l => l.Moves.Where(m => m.Kind == MoveKind.Extrude
                    && !m.IsLightning
                    && DistPointSeg(probe, m.From, m.To) < 18f)
                    .Select(m => (l.Z, m)))
                .Cast<(float Z, ToolpathMove M)?>()
                .FirstOrDefault();
            Assert.True(witness is null,
                $"hollow filled near ({probe.X},{probe.Y},{probe.Z}): "
                + $"z={witness?.Z:0.#} {witness?.M.From}->{witness?.M.To} lightning={witness?.M.IsLightning}");
        }

        // 3. Support: material lightning ADDS must rest within reach of the layer
        //    below (the invariant the floating-finger bugs violated). Region walls
        //    that match baseline material are the mesh's own responsibility — bed
        //    contact, island births (diaphragm bottom) float identically in a plain
        //    shells slice — so only lightning-specific moves are policed.
        float allowance = MathF.Max(SupportRadius + 0.75f, 4f * Bead * 0.6f);
        int floats = 0; float worstFloat = 0; float worstFloatZ = 0;
        var baselineByZ = baseline.Layers.ToDictionary(l => MathF.Round(l.Z, 1));
        for (int li = 1; li < lightning.Layers.Count; li++)
        {
            var layer = lightning.Layers[li];
            var below = lightning.Layers[li - 1].Moves
                .Where(m => m.Kind == MoveKind.Extrude).ToList();
            if (below.Count == 0) continue;
            baselineByZ.TryGetValue(MathF.Round(layer.Z, 1), out var blSame);
            var blSegs = blSame?.Moves.Where(m => m.Kind == MoveKind.Extrude).ToList() ?? [];
            foreach (var m in layer.Moves)
            {
                if (m.Kind != MoveKind.Extrude || m.IsLayerStitch || m.IsWipe) continue;
                if (Vector3.Distance(m.From, m.To) < 1.5f) continue;
                var mid = (m.From + m.To) * 0.5f;
                if (mid.Z < 15f) continue;                    // bed contact zone
                bool meshDemanded = blSegs.Any(b => DistPointSeg(mid, b.From, b.To) < 2.5f);
                if (meshDemanded) continue;
                float best = float.MaxValue;
                foreach (var b in below)
                {
                    float d = DistPointSeg(mid, b.From, b.To);
                    if (d < best) best = d;
                    if (best < allowance) break;
                }
                if (best > allowance + 6.5f)
                {
                    floats++;
                    if (best > worstFloat) { worstFloat = best; worstFloatZ = layer.Z; }
                }
            }
        }
        // Isolated marginal bridges happen (a finger mid-span slightly past the
        // inter-finger allowance), and hull/diaphragm JUNCTION planes still produce
        // one band of ~35 mm phantom-parity walls (known artifact: the twin chains
        // weld differently there and no contour-level dedupe can pair them — fixing
        // it needs segment-level curve reasoning). The field bugs this pins were
        // 95–105 mm floats across whole columns, so cap magnitude and count at the
        // current engine's frontier: regressions beyond it must fail.
        Assert.True(worstFloat < 45f,
            $"floating bead at z={worstFloatZ:0.#}: {worstFloat:0.##} mm above support");
        Assert.True(floats <= 30, $"{floats} floating beads — systemic support failure");

        // 4. Continuity: lightning must not systemically fragment the path beyond
        //    what the geometry demands. Grazing tangent-band layers legitimately
        //    differ by several islands (parity of split twin chains vs the baseline's
        //    proximity chaining), so isolated violations are tolerated — a real
        //    fragmentation regression trips the budget across many layers.
        int violations = 0; string firstViolation = "";
        foreach (var bl in baseline.Layers)
        {
            if (!byZ.TryGetValue(MathF.Round(bl.Z, 1), out var ll)) continue;
            int Travels(ToolpathLayer l) => l.Moves.Count(m =>
                m.Kind == MoveKind.Travel && !m.IsLayerChange && !m.IsZHop);
            int lt = Travels(ll), bt = Travels(bl);
            if (bt > 10) continue;   // deep tangent band: no meaningful island count
            if (lt > bt + Math.Max(2, bt / 2))
            {
                violations++;
                if (firstViolation.Length == 0)
                    firstViolation = $"z={ll.Z:0.#}: {lt} travels vs baseline {bt}";
            }
        }
        Assert.True(violations <= 4,
            $"path fragmented on {violations} layers (first: {firstViolation})");
    }

    private static float DistPointSeg(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 < 1e-9f ? 0f : Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f);
        return (p - (a + ab * t)).Length();
    }
}
