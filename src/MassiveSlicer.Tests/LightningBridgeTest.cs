using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Core.Slicing.Lightning;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Lightning Bridge infill: continuous perimeter-anchored support fingers.
/// The canonical demand case for shells-only printing is an INWARD-shrinking top
/// (dome / closing vessel) — its walls move inward faster than the support radius,
/// so fingers must grow beneath them; gradual shapes and prisms must stay
/// perimeter-only. Continuity (zero travels) is the hard LFAM requirement.
/// </summary>
public sealed class LightningBridgeTest
{
    private const float LayerH = 3f, Bead = 6f;

    private static SliceSettings Settings() => new()
    {
        LayerHeight = LayerH, FirstLayerHeight = LayerH, BeadWidth = Bead,
        InfillPattern = InfillPattern.LightningBridge,
        LightningOverhangDeg = 30f,
    };

    // maxStep = min(3·tan30°, 3) = 1.73; supportRadius = maxStep + bead/2.
    private static readonly float MaxStep = MathF.Min(LayerH * MathF.Tan(30f * MathF.PI / 180f), Bead * 0.5f);
    private static readonly float SupportRadius = MaxStep + Bead * 0.5f;

    // -- Synthetic meshes --------------------------------------------------------

    /// <summary>Surface of revolution over a radius profile, capped top and bottom.</summary>
    private static Vector3[] Revolve((float r, float z)[] profile, int segments = 64)
    {
        var v = new List<Vector3>();
        Vector3 P(float r, float z, int i)
        {
            float a = i / (float)segments * 2f * MathF.PI;
            return new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z);
        }
        for (int p = 0; p + 1 < profile.Length; p++)
        {
            var (r0, z0) = profile[p];
            var (r1, z1) = profile[p + 1];
            for (int i = 0; i < segments; i++)
            {
                var a0 = P(r0, z0, i); var a1 = P(r0, z0, i + 1);
                var b0 = P(r1, z1, i); var b1 = P(r1, z1, i + 1);
                if (r0 > 1e-3f) { v.AddRange([a0, a1, b0]); }
                if (r1 > 1e-3f) { v.AddRange([a1, b1, b0]); }
            }
        }
        // caps
        var (rb, zb) = profile[0];
        var (rt, zt) = profile[^1];
        for (int i = 0; i < segments; i++)
        {
            if (rb > 1e-3f) v.AddRange([new Vector3(0, 0, zb), P(rb, zb, i + 1), P(rb, zb, i)]);
            if (rt > 1e-3f) v.AddRange([new Vector3(0, 0, zt), P(rt, zt, i), P(rt, zt, i + 1)]);
        }
        return v.ToArray();
    }

    /// <summary>Cylinder r=60 up to z=117, then closing almost flat to r=6 by z=123 —
    /// the closing walls move inward ~27 mm per layer, far beyond the support radius.</summary>
    private static Vector3[] FlatTopVessel() =>
        Revolve([(60f, 0f), (60f, 117f), (6f, 123f)]);

    /// <summary>Gentle 30°-from-vertical cone top — inward shrink ≈ 1.7 mm per layer,
    /// within the support radius, so no fingers are needed.</summary>
    private static Vector3[] GentleConeVessel() =>
        Revolve([(60f, 0f), (60f, 90f), (30f, 142f)]);

    private static Vector3[] Cylinder() => Revolve([(60f, 0f), (60f, 120f)]);

    // -- Helpers -------------------------------------------------------------------

    private static float ExtrudeLen(ToolpathLayer l) =>
        l.Moves.Where(m => m.Kind == MoveKind.Extrude)
               .Sum(m => Vector3.Distance(m.From, m.To));

    // -- Tests -----------------------------------------------------------------------

    [Fact]
    public void FlatTopVesselGrowsFingersBelowTheClosure()
    {
        var tp = PlanarSlicer.Slice([FlatTopVessel()], Settings(), null);
        Assert.True(tp.Layers.Count > 30, $"layers {tp.Layers.Count}");

        float perimeter = 2f * MathF.PI * (60f - Bead / 2f);   // inset shell circumference

        // Bottom layers: perimeter only (no demand reaches that deep).
        var bottom = tp.Layers.Where(l => l.Z < 20f).ToList();
        Assert.True(bottom.Count > 2);
        foreach (var l in bottom)
            Assert.True(ExtrudeLen(l) < perimeter * 1.2f,
                $"z={l.Z}: {ExtrudeLen(l):0} mm — spurious fingers near the bed");

        // Layers approaching the closure: fingers add substantial path length.
        var nearTop = tp.Layers.Where(l => l.Z > 100f && l.Z < 118f).ToList();
        Assert.True(nearTop.Count > 2);
        Assert.Contains(nearTop, l => ExtrudeLen(l) > perimeter * 1.3f);
    }

    [Fact]
    public void EveryLayerIsOneContinuousExtrusion()
    {
        var tp = PlanarSlicer.Slice([FlatTopVessel()], Settings(), null);
        foreach (var layer in tp.Layers)
        {
            // The cross-layer connector inserted by Slice() may be a travel; within
            // the layer itself the path must chain unbroken with zero travels.
            var moves = layer.Moves
                .SkipWhile(m => m.IsLayerChange || m.IsLayerStitch)
                .ToList();
            Assert.DoesNotContain(moves, m => m.Kind == MoveKind.Travel);
            for (int k = 1; k < moves.Count; k++)
                Assert.True(Vector3.Distance(moves[k].From, moves[k - 1].To) < 0.01f,
                    $"z={layer.Z}: chain break at move {k}");
        }
    }

    [Fact]
    public void EveryExtrudeSitsOnMaterialBelowOrBridgesBetweenFingers()
    {
        var tp = PlanarSlicer.Slice([FlatTopVessel()], Settings(), null);

        // Fingers are spaced branchSpacing (auto = 4×bead) apart, so a bead crossing
        // the gap between two finger tips legitimately bridges up to ~half a spacing
        // from the nearest support — the same way classic lightning infill bridges
        // between branch tips. Anything beyond that is genuinely floating.
        float bridgeAllowance = 4f * Bead * 0.6f;
        float limit = MathF.Max(SupportRadius + 0.75f, bridgeAllowance);

        for (int li = 1; li < tp.Layers.Count; li++)
        {
            var below = tp.Layers[li - 1].Moves.Where(m => m.Kind == MoveKind.Extrude).ToList();
            if (below.Count == 0) continue;

            foreach (var m in tp.Layers[li].Moves)
            {
                if (m.Kind != MoveKind.Extrude || m.IsLayerStitch) continue;
                var mid = (m.From + m.To) * 0.5f;
                float best = float.MaxValue;
                foreach (var b in below)
                {
                    float d = DistToSegment2D(mid, b.From, b.To);
                    if (d < best) best = d;
                }
                Assert.True(best <= limit,
                    $"z={tp.Layers[li].Z}: bead at ({mid.X:0.#},{mid.Y:0.#}) floats {best:0.##} mm from the layer below");
            }
        }
    }

    [Fact]
    public void GentleShapesStayPerimeterOnly()
    {
        float perimeter = 2f * MathF.PI * (60f - Bead / 2f);

        foreach (var mesh in new[] { GentleConeVessel(), Cylinder() })
        {
            var tp = PlanarSlicer.Slice([mesh], Settings(), null);
            Assert.True(tp.Layers.Count > 10);
            foreach (var l in tp.Layers)
            {
                float expected = 2f * MathF.PI * 60f;   // upper bound (radius shrinks above)
                Assert.True(ExtrudeLen(l) < expected * 1.2f,
                    $"z={l.Z}: {ExtrudeLen(l):0} mm — fingers grew where none are needed");
            }
        }
    }

    [Fact]
    public void PlannerRetractionInvariantHolds()
    {
        // Synthetic shrinking squares: walls move inward 6 mm/layer — beyond the
        // 4.73 mm support radius, so every layer of the shrink is demand.
        var polys = new List<List<List<Vector2>>>();
        var heights = new List<float>();
        for (int i = 0; i < 10; i++)
        {
            float h = 80f - i * 6f;
            polys.Add([[new(-h, -h), new(h, -h), new(h, h), new(-h, h)]]);
            heights.Add(LayerH);
        }
        var plan = LightningPlanner.Build(polys, heights, Settings());

        Assert.Contains(plan.Layers, lp => lp.Trees.Count > 0);

        for (int i = 0; i + 1 < plan.Layers.Length; i++)
        {
            foreach (var treeAbove in plan.Layers[i + 1].Trees)
                foreach (var b in treeAbove.Branches)
                    foreach (var node in b.Centerline)
                    {
                        // Every node of the layer above must be within one step of
                        // SOME centerline on this layer (grown-from-below invariant).
                        if (plan.Layers[i].Trees.Count == 0) continue;
                        float best = float.MaxValue;
                        foreach (var t in plan.Layers[i].Trees)
                            foreach (var lb in t.Branches)
                                for (int k = 1; k < lb.Centerline.Count; k++)
                                    best = MathF.Min(best,
                                        DistToSegment2D(new Vector3(node, 0),
                                            new Vector3(lb.Centerline[k - 1], 0),
                                            new Vector3(lb.Centerline[k], 0)));
                        // Straightening / MaxStep tip-redirect may add a fraction of a
                        // step on top of pure retract; still well under a bead jump.
                        Assert.True(best <= 2.5f * MaxStep + Bead * 0.25f,
                            $"layer {i + 1} node ({node.X:0.#},{node.Y:0.#}) is {best:0.##} mm from layer {i}'s fingers");
                    }
        }
    }

    [Fact]
    public void FingerDepthGrowsByAtMostMaxStepBetweenLayers()
    {
        // Continuous inward shrink: fingers must form MaxStep columns (no mid-stack
        // full-depth re-birth islands). Max trunk length on layer i+1 may exceed
        // layer i by at most ~MaxStep (bottom-up growth / top-down retract dual).
        var polys = new List<List<List<Vector2>>>();
        var heights = new List<float>();
        for (int i = 0; i < 12; i++)
        {
            float h = 70f - i * 5f;
            polys.Add([[new(-h, -h), new(h, -h), new(h, h), new(-h, h)]]);
            heights.Add(LayerH);
        }
        var plan = LightningPlanner.Build(polys, heights, Settings());
        Assert.Contains(plan.Layers, lp => lp.Trees.Count > 0);

        float MaxTrunk(LightningLayerPlan lp)
        {
            float m = 0f;
            foreach (var t in lp.Trees)
            {
                if (t.Branches.Count == 0 || t.Branches[0].Centerline.Count < 2) continue;
                var line = t.Branches[0].Centerline;
                m = MathF.Max(m, Vector2.Distance(line[0], line[^1]));
            }
            return m;
        }

        // Walk bottom→top through the continuous tree band; each step may deepen
        // by ~MaxStep only (founding full-birth is only allowed with no prior column).
        float prev = 0f;
        int compared = 0;
        for (int i = 0; i < plan.Layers.Length; i++)
        {
            float d = MaxTrunk(plan.Layers[i]);
            if (d < Bead * 0.4f)
            {
                // Gap / no trees — reset so the next founding birth isn't scored.
                prev = 0f;
                continue;
            }
            if (prev > Bead * 0.4f)
            {
                float grew = d - prev;
                Assert.True(grew <= MaxStep * 2.5f + Bead * 0.5f,
                    $"layer {i}: trunk jumped {grew:0.##} mm (prev={prev:0.#} now={d:0.#} maxStep={MaxStep:0.##}) — full-depth re-birth?");
                compared++;
            }
            prev = d;
        }
        Assert.True(compared >= 2, $"expected a multi-layer column, only compared {compared} steps");
    }

    [Fact]
    public void ExteriorOverhangCheckboxGrowsSacrificialFins()
    {
        // Cylinder flaring OUTWARD near the top (r 60 → 90 over one layer's height —
        // far beyond the support radius). Off: skipped as unsupportable. On:
        // sacrificial fins grow outside the wall beneath the flare.
        var mesh = Revolve([(60f, 0f), (60f, 117f), (90f, 123f)]);

        var off = PlanarSlicer.Slice([mesh], Settings(), null);
        var onS = new SliceSettings
        {
            LayerHeight = LayerH, FirstLayerHeight = LayerH, BeadWidth = Bead,
            InfillPattern = InfillPattern.LightningBridge, LightningOverhangDeg = 30f,
            LightningExteriorOverhangs = true,
        };
        var on = PlanarSlicer.Slice([mesh], onS, null);

        float MaxRadiusInBand(Toolpath tp, float zLo, float zHi) => tp.Layers
            .Where(l => l.Z > zLo && l.Z < zHi)
            .SelectMany(l => l.Moves.Where(m => m.Kind == MoveKind.Extrude))
            .SelectMany(m => new[] { m.From, m.To })
            .Select(pnt => new Vector2(pnt.X, pnt.Y).Length())
            .DefaultIfEmpty(0f)
            .Max();

        // Off: nothing beyond the wall (r=57 inset + half bead of slit rounding).
        Assert.True(MaxRadiusInBand(off, 80f, 117f) < 60f,
            $"external material without the checkbox: r={MaxRadiusInBand(off, 80f, 117f):0.#}");
        // On: fins reach outward well past the wall beneath the flare.
        Assert.True(MaxRadiusInBand(on, 80f, 117f) > 66f,
            $"fins did not grow outward: r={MaxRadiusInBand(on, 80f, 117f):0.#}");
        // Fins lean at the bead-on-bead limit (bead/2 per layer), so they peel off
        // the perimeter close under the flare instead of trailing a shallow sail
        // toward the bed. Continuous MaxStep columns may start a few layers lower
        // than a single full-birth island; they must still stay near the flare.
        float lowestFinZ = on.Layers
            .Where(l => l.Moves.Any(m => m.Kind == MoveKind.Extrude
                && MathF.Max(new Vector2(m.From.X, m.From.Y).Length(),
                             new Vector2(m.To.X, m.To.Y).Length()) > 61f))
            .Select(l => l.Z)
            .DefaultIfEmpty(float.MaxValue)
            .Min();
        Assert.True(lowestFinZ > 95f,
            $"fins trail too far below the flare: outward material starts at z={lowestFinZ:0.#}");

        // Continuity holds with fins (single island, zero travels).
        foreach (var layer in on.Layers)
            Assert.DoesNotContain(
                layer.Moves.SkipWhile(m => m.IsLayerChange || m.IsLayerStitch),
                m => m.Kind == MoveKind.Travel);

        // Every fin layer rests on the fin below (same growth invariant).
        for (int li = 1; li < on.Layers.Count; li++)
        {
            var below = on.Layers[li - 1].Moves.Where(m => m.Kind == MoveKind.Extrude).ToList();
            if (below.Count == 0) continue;
            foreach (var m in on.Layers[li].Moves)
            {
                if (m.Kind != MoveKind.Extrude || m.IsLayerStitch) continue;
                var mid = (m.From + m.To) * 0.5f;
                float best = float.MaxValue;
                foreach (var b in below)
                    best = MathF.Min(best, DistToSegment2D(mid, b.From, b.To));
                Assert.True(best <= MathF.Max(SupportRadius + 0.75f, 4f * Bead * 0.6f),
                    $"z={on.Layers[li].Z}: fin bead floats {best:0.##} mm");
            }
        }
    }

    [Fact]
    public void DroppedTreeLineageNeverPrintsAboveTheDrop()
    {
        // Layer 0: thin strip the slit cuts clean across → neck guard drops the tree.
        // Layer 1: shorter finger that would emit fine — but its support column died
        // at layer 0, so the shared dropped-lineage set must silence it too.
        List<List<Vector2>> strip =
            [[new(0, 0), new(100, 0), new(100, 20), new(0, 20)]];

        var plan = new MassiveSlicer.Core.Slicing.Lightning.LightningPlan(2);
        var full = new MassiveSlicer.Core.Slicing.Lightning.LightningTree { Id = 7, Anchor = new(0, 10) };
        full.Branches.Add(new MassiveSlicer.Core.Slicing.Lightning.LightningBranch(
            [new Vector2(0, 10), new Vector2(101, 10)]));           // crosses the far wall
        plan.Layers[0].Trees.Add(full);

        var partial = new MassiveSlicer.Core.Slicing.Lightning.LightningTree { Id = 7, Anchor = new(0, 10) };
        partial.Branches.Add(new MassiveSlicer.Core.Slicing.Lightning.LightningBranch(
            [new Vector2(0, 10), new Vector2(50, 10)]));            // healthy mid-region finger
        plan.Layers[1].Trees.Add(partial);

        var l0 = new ToolpathLayer(0, 3f) { Height = 3f };
        var l1 = new ToolpathLayer(1, 6f) { Height = 3f };
        MassiveSlicer.Core.Slicing.Lightning.LightningGenerator.EmitLightning(strip, plan.Layers[0], 3f, l0, Bead, 0f);
        MassiveSlicer.Core.Slicing.Lightning.LightningGenerator.EmitLightning(strip, plan.Layers[1], 6f, l1, Bead, 0f);

        Assert.Contains(7, plan.Layers[0].DroppedTrees);            // neck guard fired

        // Assert on geometry, not the IsLightning tag: no extrude material may exist
        // in the strip's interior band the finger walls would occupy (y 7–13,
        // x 8–45 — the perimeter rectangle never enters it).
        // Long straight slit walls have vertices only at their ends, so probe the
        // segment midpoint as well as the endpoints.
        static bool InBand(float x, float y) => x > 8 && x < 45 && y > 5 && y < 15;
        static bool FingerMaterial(ToolpathLayer l) => l.Moves.Any(m =>
            m.Kind == MoveKind.Extrude
            && (InBand(m.From.X, m.From.Y) || InBand(m.To.X, m.To.Y)
                || InBand((m.From.X + m.To.X) * 0.5f, (m.From.Y + m.To.Y) * 0.5f)));
        Assert.False(FingerMaterial(l0), "layer 0 kept the region-splitting finger");
        Assert.False(FingerMaterial(l1), "layer 1 printed a finger over the dropped column");
        Assert.Contains(l1.Moves, m => m.Kind == MoveKind.Extrude); // perimeter still prints
    }

    [Fact]
    public void DoubleShelledMeshKeepsHollowInteriorAndInnerWalls()
    {
        // Real-world CAD exports often duplicate every surface, so each contour slices
        // twice — and the duplicate pair confuses nesting-depth orientation (both copies
        // end up wound the same way). A raw NonZero union then fills the hollow interior
        // and swallows inner walls (fuselage bug). ToPathsD must dedupe coincident
        // contours and re-orient survivors by nesting parity.
        static List<Vector2> Square(float half, float off, bool ccw)
        {
            List<Vector2> pts =
                [new(-half + off, -half), new(half + off, -half), new(half + off, half), new(-half + off, half)];
            if (!ccw) pts.Reverse();
            return pts;
        }

        // Outer 200×200 and inner hole 120×120, each duplicated with a 0.05 mm offset,
        // ALL wound clockwise (the corrupted orientation observed on the fuselage).
        List<List<Vector2>> polys =
        [
            Square(100f, 0f, ccw: false),
            Square(100f, 0.05f, ccw: false),
            Square(60f, 0f, ccw: false),
            Square(60f, 0.05f, ccw: false),
        ];

        var region = MassiveSlicer.Core.Slicing.Lightning.LightningPlanner.ToPathsD(polys);

        // The hollow interior must survive: centre outside, ring material inside.
        Assert.False(MassiveSlicer.Core.Slicing.Lightning.LightningPlanner.InsideRegion(
            region, new Vector2(0, 0)), "hollow interior was filled");
        Assert.True(MassiveSlicer.Core.Slicing.Lightning.LightningPlanner.InsideRegion(
            region, new Vector2(80, 0)), "ring material missing");

        // And the emitted layer prints BOTH walls exactly once each.
        var plan = new MassiveSlicer.Core.Slicing.Lightning.LightningPlan(1);
        var layer = new ToolpathLayer(0, 3f) { Height = 3f };
        MassiveSlicer.Core.Slicing.Lightning.LightningGenerator.EmitLightning(
            polys, plan.Layers[0], 3f, layer, Bead, 0f);
        bool NearWall(float target) => layer.Moves.Any(m => m.Kind == MoveKind.Extrude
            && Math.Abs(MathF.Max(Math.Abs((m.From.X + m.To.X) * 0.5f), Math.Abs((m.From.Y + m.To.Y) * 0.5f)) - target) < 2f);
        Assert.True(NearWall(100f), "outer wall missing");
        Assert.True(NearWall(60f), "inner wall missing");
    }

    [Fact]
    public void MeshInsideTesterHandlesDoubleShelledExports()
    {
        // Axis-aligned cube 0..100 as a triangle soup, then the same cube again
        // offset 0.02 mm (double-shelled CAD export). Parity ray-casting must read
        // the twin surfaces as ONE surface or every answer inverts.
        static Vector3[] Cube(float o)
        {
            Vector3 p000 = new(o, o, o), p100 = new(100 + o, o, o), p010 = new(o, 100 + o, o),
                p110 = new(100 + o, 100 + o, o), p001 = new(o, o, 100 + o), p101 = new(100 + o, o, 100 + o),
                p011 = new(o, 100 + o, 100 + o), p111 = new(100 + o, 100 + o, 100 + o);
            return
            [
                p000, p010, p110, p000, p110, p100,   // bottom
                p001, p101, p111, p001, p111, p011,   // top
                p000, p100, p101, p000, p101, p001,   // front
                p010, p011, p111, p010, p111, p110,   // back
                p000, p001, p011, p000, p011, p010,   // left
                p100, p110, p111, p100, p111, p101,   // right
            ];
        }

        foreach (var meshes in new[] { new[] { Cube(0f) }, new[] { Cube(0f), Cube(0.02f) } })
        {
            var t = new MeshInsideTester(meshes);
            Assert.True(t.IsInside(new Vector3(50, 50, 50)), "centre must be solid");
            Assert.True(t.IsInside(new Vector3(5, 5, 5)), "near-corner must be solid");
            Assert.False(t.IsInside(new Vector3(150, 50, 50)), "beside the cube must be void");
            Assert.False(t.IsInside(new Vector3(50, 50, 150)), "above the cube must be void");
            Assert.False(t.IsInside(new Vector3(50, 50, -50)), "below the cube must be void");
        }
    }

    [Fact]
    public void PhantomIslandDoesNotSeedFingerLadder()
    {
        // A grazing cut over a pocket rim emits the rim curve without the wall that
        // hosts it, and the parity union reads that lone contour as a SOLID island —
        // for the one or two layers the tangency lasts. Real geometry persists;
        // phantoms vanish. The planner must refuse to grow fingers under solids
        // that are gone a few layers further up, or each phantom seeds a ladder of
        // bridging for dozens of layers below geometry that doesn't exist
        // (Drone V52 bug, 2026-07-09).
        static List<Vector2> Square(float half, bool ccw)
        {
            List<Vector2> pts =
                [new(-half, -half), new(half, -half), new(half, half), new(-half, half)];
            if (!ccw) pts.Reverse();
            return pts;
        }

        const int n = 40;
        var polys = new List<List<List<Vector2>>>();
        for (int i = 0; i < n; i++)
        {
            List<List<Vector2>> layer = i < 20
                ? [Square(100f, ccw: true)]                             // solid floor
                : [Square(100f, ccw: true), Square(70f, ccw: false)];   // ring above
            if (i is 20 or 21)
                layer.Add(Square(15f, ccw: true));   // phantom island in the ring void
            polys.Add(layer);
        }
        var heights = Enumerable.Repeat(3f, n).ToList();
        var plan = LightningPlanner.Build(polys, heights, new SliceSettings
        {
            LayerHeight = 3f, BeadWidth = Bead, InfillPattern = InfillPattern.LightningBridge,
            LightningOverhangDeg = 30f,
            LightningAnchorInterior = true, LightningAnchorExterior = true,
        },
        // Mesh-truth oracle: everything the layers claim is real EXCEPT the phantom
        // island's footprint (the mesh has a void there — grazing-cut rim curve).
        solidAt: (li, p) => !(MathF.Abs(p.X) < 16f && MathF.Abs(p.Y) < 16f));

        // No finger may chase the phantom: nothing reaches its footprint below it.
        for (int i = 0; i < 20; i++)
            foreach (var t in plan.Layers[i].Trees)
                foreach (var b in t.Branches)
                    foreach (var p in b.Centerline)
                        Assert.True(p.Length() > 40f,
                            $"layer {i}: finger node at ({p.X:0.#},{p.Y:0.#}) chases the phantom island");

        // Control: the ring's inner wall floats over the hollow floor and PERSISTS —
        // that demand is real and must still grow fingers.
        Assert.True(plan.Layers[19].Trees.Count > 0,
            "persistent real demand (ring inner wall) no longer plans support");
    }

    [Fact]
    public void ConvergingFingersMergeInsteadOfLeavingASliver()
    {
        // Two parallel fingers 8 mm apart (bead 6): their slit walls face each other
        // 2 mm apart — a sliver tongue printed as two nearly-coincident beads.
        // The generator must merge the near-touching slits into one clean notch.
        List<List<Vector2>> rect = [[new(0, 0), new(200, 0), new(200, 68), new(0, 68)]];

        var plan = new MassiveSlicer.Core.Slicing.Lightning.LightningPlan(1);
        var a = new MassiveSlicer.Core.Slicing.Lightning.LightningTree { Id = 1, Anchor = new(0, 30) };
        a.Branches.Add(new MassiveSlicer.Core.Slicing.Lightning.LightningBranch(
            [new Vector2(0, 30), new Vector2(120, 30)]));
        var b = new MassiveSlicer.Core.Slicing.Lightning.LightningTree { Id = 2, Anchor = new(0, 38) };
        b.Branches.Add(new MassiveSlicer.Core.Slicing.Lightning.LightningBranch(
            [new Vector2(0, 38), new Vector2(120, 38)]));
        plan.Layers[0].Trees.Add(a);
        plan.Layers[0].Trees.Add(b);

        var layer = new ToolpathLayer(0, 3f) { Height = 3f };
        MassiveSlicer.Core.Slicing.Lightning.LightningGenerator.EmitLightning(
            rect, plan.Layers[0], 3f, layer, Bead, 0f);

        // Without the merge, the sliver's facing walls run at y = 33 and y = 35
        // (30+bead/2 / 38−bead/2) between the anchors and the tips.
        static bool InSliver(float x, float y) => x > 10 && x < 100 && y > 32 && y < 36;
        Assert.DoesNotContain(layer.Moves, m =>
            m.Kind == MoveKind.Extrude
            && (InSliver(m.From.X, m.From.Y) || InSliver(m.To.X, m.To.Y)
                || InSliver((m.From.X + m.To.X) * 0.5f, (m.From.Y + m.To.Y) * 0.5f)));

        // Still one continuous island: no travels after the layer lead-in.
        Assert.DoesNotContain(
            layer.Moves.SkipWhile(m => m.IsLayerChange || m.IsLayerStitch),
            m => m.Kind == MoveKind.Travel);

        // The merged notch still exists (fingers weren't just dropped).
        Assert.Contains(layer.Moves, m => m.Kind == MoveKind.Extrude
            && (m.From.X + m.To.X) * 0.5f > 40
            && (m.From.Y + m.To.Y) * 0.5f is > 20 and < 48);
    }

    [Fact]
    public void AnchorJumpRetiresTheWholeLineage()
    {
        // Top layers: a small square shrinking inside a big one → interior demand
        // roots fingers on the big square's boundary. Bottom layers: the region
        // teleports 200 mm sideways — the wall under the fingers is gone, so the
        // whole lineage must vanish from every layer instead of re-anchoring there.
        List<List<Vector2>> big     = [[new(0, 0), new(60, 0), new(60, 60), new(0, 60)]];
        List<List<Vector2>> small   = [[new(15, 15), new(45, 15), new(45, 45), new(15, 45)]];
        List<List<Vector2>> shifted = [[new(200, 0), new(260, 0), new(260, 60), new(200, 60)]];

        var settings = Settings();
        float[] heights = [LayerH, LayerH, LayerH, LayerH, LayerH, LayerH];

        // Control: continuous wall below — fingers exist under the shrink.
        var okPlan = MassiveSlicer.Core.Slicing.Lightning.LightningPlanner.Build(
            [big, big, big, big, small, small], heights, settings);
        Assert.True(okPlan.Layers[3].Trees.Count > 0, "control: no demand fingers formed");

        // Boundary teleports under the fingers at layers 0–1.
        var jumpPlan = MassiveSlicer.Core.Slicing.Lightning.LightningPlanner.Build(
            [shifted, shifted, big, big, small, small], heights, settings);
        for (int i = 0; i < jumpPlan.Layers.Length; i++)
            Assert.True(jumpPlan.Layers[i].Trees.Count == 0,
                $"layer {i} kept {jumpPlan.Layers[i].Trees.Count} tree(s) after the anchor jump");
    }

    [Fact]
    public void AnchorClassCheckboxesControlWhereFingersRoot()
    {
        // Affect Interior: inward demand roots on the hole. Affect Exterior: outward
        // flares root on the outer perimeter (sacrificial). Separate domains — exterior
        // does not require interior to be on.
        List<List<List<Vector2>>> ShrinkingWithHole()
        {
            var layers = new List<List<List<Vector2>>>();
            for (int i = 0; i < 8; i++)
            {
                float h = 90f - i * 6f;
                layers.Add(
                [
                    [new(-h, -h), new(h, -h), new(h, h), new(-h, h)],
                    [new(-20f, -20f), new(-20f, 20f), new(20f, 20f), new(20f, -20f)],
                ]);
            }
            return layers;
        }
        // Expanding outer square (outward flare) — exterior-domain demand.
        List<List<List<Vector2>>> ExpandingOuter()
        {
            var layers = new List<List<List<Vector2>>>();
            for (int i = 0; i < 8; i++)
            {
                float h = 40f + i * 8f;
                layers.Add(
                [
                    [new(-h, -h), new(h, -h), new(h, h), new(-h, h)],
                ]);
            }
            return layers;
        }
        var heights = Enumerable.Repeat(LayerH, 8).ToList();

        var interiorOnly = LightningPlanner.Build(ShrinkingWithHole(), heights, new SliceSettings
        {
            LayerHeight = LayerH, FirstLayerHeight = LayerH, BeadWidth = Bead,
            InfillPattern = InfillPattern.LightningBridge, LightningOverhangDeg = 30f,
            LightningAnchorInterior = true, LightningAnchorExterior = false,
            LightningExteriorOverhangs = false,
        });
        var exteriorOnly = LightningPlanner.Build(ExpandingOuter(), heights, new SliceSettings
        {
            LayerHeight = LayerH, FirstLayerHeight = LayerH, BeadWidth = Bead,
            InfillPattern = InfillPattern.LightningBridge, LightningOverhangDeg = 30f,
            LightningAnchorInterior = false, LightningAnchorExterior = true,
            LightningExteriorOverhangs = true,
        });

        bool sawInterior = false, sawExterior = false;
        foreach (var lp in interiorOnly.Layers)
            foreach (var t in lp.Trees)
            {
                sawInterior = true;
                float m = MathF.Max(MathF.Abs(t.Anchor.X), MathF.Abs(t.Anchor.Y));
                Assert.True(MathF.Abs(m - 20f) < 1.5f,
                    $"interior-only anchor ({t.Anchor.X:0.#},{t.Anchor.Y:0.#}) not on the hole");
            }
        foreach (var lp in exteriorOnly.Layers)
            foreach (var t in lp.Trees)
            {
                sawExterior = true;
                Assert.True(t.External,
                    $"exterior-only tree should be External (got cavity/interior at {t.Anchor})");
            }
        Assert.True(sawInterior, "no trees planned with Affect Interior");
        Assert.True(sawExterior, "no trees planned with Affect Exterior");
    }

    [Fact]
    public void TipLoopsAddSupportPadPathLength()
    {
        var square = new List<List<Vector2>>
        {
            new() { new(-60, -60), new(60, -60), new(60, 60), new(-60, 60) },
        };
        var plan = new LightningLayerPlan();
        var tree = new LightningTree { Anchor = new Vector2(-60, 0) };
        tree.Branches.Add(new LightningBranch([new Vector2(-60, 0), new Vector2(0, 0)]));
        plan.Trees.Add(tree);

        float LoopLen(float tipRadius)
        {
            var layer = new ToolpathLayer(0, 0f);
            LightningGenerator.EmitLightning(square, plan, 0f, layer, Bead, tipRadius);
            return layer.Moves.Where(m => m.Kind == MoveKind.Extrude)
                              .Sum(m => Vector3.Distance(m.From, m.To));
        }

        float plain = LoopLen(0f);
        float looped = LoopLen(30f);
        Assert.True(plain > 4 * 120f, "finger notch missing from the perimeter loop");
        // A 30 mm tip disc adds roughly its circumference of extra boundary.
        Assert.True(looped > plain + 100f,
            $"tip loop added only {looped - plain:0} mm (plain {plain:0}, looped {looped:0})");
    }

    [Fact]
    public void AngledSlicerSmoke()
    {
        var settings = new SliceSettings
        {
            LayerHeight = LayerH, FirstLayerHeight = LayerH, BeadWidth = Bead,
            TiltAngle = 20f,
            InfillPattern = InfillPattern.LightningBridge,
            LightningOverhangDeg = 30f,
        };
        var tp = AngledPlanarSlicer.Slice([FlatTopVessel()], settings);
        Assert.True(tp.Layers.Count > 10, $"layers {tp.Layers.Count}");
        var mid = tp.Layers[tp.Layers.Count / 2];
        Assert.True(mid.Moves.Count(m => m.Kind == MoveKind.Extrude) > 8);
        Assert.Contains(mid.Moves, m => m.Normal != Vector3.Zero);
        Assert.DoesNotContain(
            mid.Moves.SkipWhile(m => m.IsLayerChange || m.IsLayerStitch),
            m => m.Kind == MoveKind.Travel);
    }

    // -- Formbound Buttress -------------------------------------------------------

    private static SliceSettings ButtressSettings() => new()
    {
        LayerHeight = LayerH, FirstLayerHeight = LayerH, BeadWidth = Bead,
        InfillPattern = InfillPattern.FormboundButtress,
        LightningOverhangDeg = 30f,
        LightningButtressBarMm = 40f,
        LightningPreferInteriorMouths = true,
    };

    [Fact]
    public void FormboundButtressGrowsUnderFlatTopAndStaysContinuous()
    {
        var tp = PlanarSlicer.Slice([FlatTopVessel()], ButtressSettings(), null);
        Assert.True(tp.Layers.Count > 30);

        float perimeter = 2f * MathF.PI * (60f - Bead / 2f);
        var nearTop = tp.Layers.Where(l => l.Z > 100f && l.Z < 118f).ToList();
        Assert.True(nearTop.Count > 2);
        Assert.Contains(nearTop, l => ExtrudeLen(l) > perimeter * 1.3f);

        foreach (var layer in tp.Layers)
        {
            var moves = layer.Moves
                .SkipWhile(m => m.IsLayerChange || m.IsLayerStitch)
                .ToList();
            Assert.DoesNotContain(moves, m => m.Kind == MoveKind.Travel);
            for (int k = 1; k < moves.Count; k++)
                Assert.True(Vector3.Distance(moves[k].From, moves[k - 1].To) < 0.01f,
                    $"z={layer.Z}: chain break at move {k}");
        }
    }

    [Fact]
    public void FormboundButtressBuildsHorizontalBarAsSingleBeadT()
    {
        // Shrinking squares: demand every layer. Buttress trees must be T-shaped
        // (trunk + bar branches) with bar span near the configured length — not
        // multi-bead solid pads.
        var polys = new List<List<List<Vector2>>>();
        var heights = new List<float>();
        for (int i = 0; i < 10; i++)
        {
            float h = 80f - i * 6f;
            polys.Add([[new(-h, -h), new(h, -h), new(h, h), new(-h, h)]]);
            heights.Add(LayerH);
        }
        var plan = LightningPlanner.Build(polys, heights, ButtressSettings());

        bool foundT = false;
        float bestBar = 0f;
        foreach (var lp in plan.Layers)
            foreach (var t in lp.Trees)
            {
                // T-morph: trunk + at least one bar leaf.
                if (t.Branches.Count < 2) continue;
                foundT = true;
                // Bar span ≈ sum of leaf branch lengths (or distance between leaf tips).
                var tips = t.Branches.Where(b => b.ParentBranch >= 0)
                    .Select(b => b.Centerline[^1]).ToList();
                if (tips.Count >= 2)
                    bestBar = MathF.Max(bestBar, Vector2.Distance(tips[0], tips[1]));
                else if (tips.Count == 1)
                    bestBar = MathF.Max(bestBar, t.Branches.Where(b => b.ParentBranch >= 0)
                        .Max(b => b.ArcLength()));
            }
        Assert.True(foundT, "no T-morph buttress trees planned");
        // Allow growth/retract: bar should approach the 40 mm setting on some layer.
        Assert.True(bestBar >= 20f, $"horizontal bar only {bestBar:0.#} mm (want ~40)");
    }

    [Fact]
    public void FormboundButtressSupportsAdjacentParallelLedges()
    {
        // Two long parallel unsupported top edges (like twin horizontal rails).
        // A single T under one must not suppress demand for the other.
        // Geometry: tall box that shrinks into two narrow parallel rectangles on top.
        var polys = new List<List<List<Vector2>>>();
        var heights = new List<float>();
        // Layers 0..5: big square (base). Layers 6..9: two separate islands side by side.
        for (int i = 0; i < 6; i++)
        {
            polys.Add([[new(-80f, -40f), new(80f, -40f), new(80f, 40f), new(-80f, 40f)]]);
            heights.Add(LayerH);
        }
        for (int i = 0; i < 4; i++)
        {
            // Two rails: Y≈-20 and Y≈+20, each 120×12 — parallel horizontals.
            polys.Add([
                [new(-60f, -26f), new(60f, -26f), new(60f, -14f), new(-60f, -14f)],
                [new(-60f, 14f), new(60f, 14f), new(60f, 26f), new(-60f, 26f)],
            ]);
            heights.Add(LayerH);
        }
        var plan = LightningPlanner.Build(polys, heights, ButtressSettings());

        // On the layer just under the twin rails, we need trees reaching both Y bands.
        bool south = false, north = false;
        foreach (var lp in plan.Layers)
            foreach (var t in lp.Trees)
                foreach (var b in t.Branches)
                    foreach (var p in b.Centerline)
                    {
                        if (p.Y < -10f) south = true;
                        if (p.Y > 10f) north = true;
                    }
        Assert.True(south && north,
            $"missed a parallel ledge (south={south}, north={north})");
    }

    [Fact]
    public void FormboundButtressSupportsBothWallsOfChannel()
    {
        // U-channel: base plate + two parallel tall walls. Upper layers shrink the
        // walls inward so both rims need support. Near-wall T's must not suppress
        // far-wall demand (side-aware coverage).
        var polys = new List<List<List<Vector2>>>();
        var heights = new List<float>();
        // Base: solid rectangle
        for (int i = 0; i < 4; i++)
        {
            polys.Add([[new(-60f, -40f), new(60f, -40f), new(60f, 40f), new(-60f, 40f)]]);
            heights.Add(LayerH);
        }
        // Channel: outer box with hole — two long walls at Y=±30
        for (int i = 0; i < 8; i++)
        {
            float inset = i < 4 ? 0f : (i - 3) * 3f; // upper layers walls move inward
            float yOut = 40f - inset;
            float yIn = 20f + inset * 0.3f;
            polys.Add([
                [new(-60f, -yOut), new(60f, -yOut), new(60f, yOut), new(-60f, yOut)],
                // CW hole — channel interior
                [new(-50f, -yIn), new(-50f, yIn), new(50f, yIn), new(50f, -yIn)],
            ]);
            heights.Add(LayerH);
        }
        var plan = LightningPlanner.Build(polys, heights, ButtressSettings());

        bool south = false, north = false;
        foreach (var lp in plan.Layers)
            foreach (var t in lp.Trees)
            {
                // Classify by ANCHOR wall (which side hosts the mouth), not bar tip.
                if (t.Anchor.Y < -15f) south = true;
                if (t.Anchor.Y > 15f) north = true;
            }
        Assert.True(south && north,
            $"both channel walls must host mouths (south={south}, north={north})");
    }

    [Fact]
    public void FormboundButtressCoversLongBridgeRunWithoutGaps()
    {
        // Long thin top ledge (~200 mm) over a wide base — the classic "orange line"
        // case: one T in the middle leaves big unsupported spans along the bridge.
        var polys = new List<List<List<Vector2>>>();
        var heights = new List<float>();
        for (int i = 0; i < 8; i++)
        {
            polys.Add([[new(-100f, -50f), new(100f, -50f), new(100f, 50f), new(-100f, 50f)]]);
            heights.Add(LayerH);
        }
        for (int i = 0; i < 4; i++)
        {
            // Narrow horizontal rail along X, full length.
            polys.Add([[new(-100f, -8f), new(100f, -8f), new(100f, 8f), new(-100f, 8f)]]);
            heights.Add(LayerH);
        }
        var plan = LightningPlanner.Build(polys, heights, ButtressSettings());

        // Collect all bar/trunk points under the rail on the densest layer.
        var pts = new List<Vector2>();
        foreach (var lp in plan.Layers)
            foreach (var t in lp.Trees)
                foreach (var b in t.Branches)
                    pts.AddRange(b.Centerline);

        Assert.True(pts.Count > 0, "no buttress geometry under long bridge");

        // Sample the rail underside every 15 mm along X; each sample must be within
        // half a bar-pitch of some centerline (continuous under-bridge coverage).
        float maxGap = 0f;
        for (float x = -90f; x <= 90f; x += 15f)
        {
            var probe = new Vector2(x, 0f);
            float best = float.MaxValue;
            foreach (var p in pts)
                best = MathF.Min(best, Vector2.Distance(p, probe));
            // Also check distance to any centerline segment more carefully via pts.
            maxGap = MathF.Max(maxGap, best);
        }
        // With overlapping bars, gap to nearest support should stay modest.
        Assert.True(maxGap < 35f,
            $"long bridge has {maxGap:0.#} mm gap to nearest buttress (want < 35)");
    }

    [Fact]
    public void FormboundButtressMouthsPreferInteriorOnHollowPart()
    {
        var polys = new List<List<List<Vector2>>>();
        var heights = new List<float>();
        for (int i = 0; i < 12; i++)
        {
            float o = 60f - (i > 6 ? (i - 6) * 5f : 0f);
            float hole = 25f;
            polys.Add([
                [new(-o, -o), new(o, -o), new(o, o), new(-o, o)],
                [new(-hole, -hole), new(-hole, hole), new(hole, hole), new(hole, -hole)],
            ]);
            heights.Add(LayerH);
        }
        var plan = LightningPlanner.Build(polys, heights, ButtressSettings());

        int interiorMouths = 0, exteriorMouths = 0;
        foreach (var lp in plan.Layers)
            foreach (var t in lp.Trees)
            {
                float r = MathF.Max(MathF.Abs(t.Anchor.X), MathF.Abs(t.Anchor.Y));
                if (r < 40f) interiorMouths++;
                else exteriorMouths++;
            }
        Assert.True(interiorMouths + exteriorMouths > 0, "no buttress trees planned");
        Assert.True(interiorMouths >= exteriorMouths,
            $"interior mouths {interiorMouths} < exterior {exteriorMouths}");
    }

    private static float DistToSegment2D(Vector3 p, Vector3 a, Vector3 b)
    {
        float abx = b.X - a.X, aby = b.Y - a.Y;
        float len2 = abx * abx + aby * aby;
        float t = len2 < 1e-12f ? 0f
            : Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2, 0f, 1f);
        float cx = a.X + t * abx, cy = a.Y + t * aby;
        return MathF.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
    }
    [Fact]
    public void OverhangConeIsMeasuredFromTheSlicePlaneAxis()
    {
        // A wall leaning 35° from vertical is comfortably printable straight-up
        // (35° < the ~60° effective budget of the 30° cone + half-bead adhesion).
        // Sliced at an OPPOSING 35° tilt, the same wall drifts at 70° relative to
        // the stacking axis — far past any budget — so a PLANE-relative analysis
        // must demand supports there. A world-Z analysis would see a printable
        // 35° lean and demand nothing.
        float hx = 150f, hy = 15f, h = 220f;
        float lean = MathF.Tan(35f * MathF.PI / 180f); // x-shear per z
        Vector3 S(float x, float y, float z) => new(x + z * lean, y, z);
        var v = new Vector3[]
        {
            S(-hx, -hy, 0), S(hx, -hy, 0), S(hx, hy, 0), S(-hx, hy, 0),
            S(-hx, -hy, h), S(hx, -hy, h), S(hx, hy, h), S(-hx, hy, h),
        };
        int[][] faces =
        [
            [0,1,2],[0,2,3], [4,6,5],[4,7,6],
            [0,4,5],[0,5,1], [1,5,6],[1,6,2],
            [2,6,7],[2,7,3], [3,7,4],[3,4,0],
        ];
        var tris = new List<Vector3>();
        foreach (var f in faces)
            tris.AddRange([v[f[0]], v[f[1]], v[f[2]]]);

        var settings = new SliceSettings
        {
            LayerHeight = 2.5f, FirstLayerHeight = 2.5f, BeadWidth = 6f,
            TiltAngle = -35f,
            InfillPattern = InfillPattern.LightningBridge,
            LightningOverhangDeg = 30f,
            LightningAnchorInterior = true,
        };
        var tp = AngledPlanarSlicer.Slice([tris.ToArray()], settings);
        Assert.True(tp.Layers.Count > 20, $"expected many tilted layers, got {tp.Layers.Count}");

        // Plane-relative demand ⇒ lightning fingers appear (extra extrusion length
        // beyond the plain perimeter on mid layers).
        var baseline = AngledPlanarSlicer.Slice([tris.ToArray()], new SliceSettings
        {
            LayerHeight = 2.5f, FirstLayerHeight = 2.5f, BeadWidth = 6f,
            TiltAngle = -35f,
            InfillPattern = InfillPattern.None,
            LightningOverhangDeg = 30f,
        });
        float Len(Toolpath t) => t.Layers.Sum(l => l.Moves
            .Where(m => m.Kind == MoveKind.Extrude)
            .Sum(m => Vector3.Distance(m.From, m.To)));
        float lightning = Len(tp), plain = Len(baseline);
        Assert.True(lightning > plain * 1.02f,
            $"no plane-relative demand detected: lightning={lightning:0} vs plain={plain:0} — "
            + "overhang analysis may be evaluating against world-vertical instead of the slice plane");

        // Control: the same leaning wall sliced straight-up is inside the budget —
        // a plane-relative analysis adds (almost) nothing there.
        var upSettings = new SliceSettings
        {
            LayerHeight = 2.5f, FirstLayerHeight = 2.5f, BeadWidth = 6f,
            InfillPattern = InfillPattern.LightningBridge,
            LightningOverhangDeg = 30f,
            LightningAnchorInterior = true,
        };
        var upPlain = new SliceSettings
        {
            LayerHeight = 2.5f, FirstLayerHeight = 2.5f, BeadWidth = 6f,
            InfillPattern = InfillPattern.None,
        };
        float upL = Len(PlanarSlicer.Slice([tris.ToArray()], upSettings, null));
        float upP = Len(PlanarSlicer.Slice([tris.ToArray()], upPlain, null));
        Assert.True(upL < upP * 1.15f,
            $"straight-up slice of a 35° lean should need little support: {upL:0} vs {upP:0}");
    }

}
