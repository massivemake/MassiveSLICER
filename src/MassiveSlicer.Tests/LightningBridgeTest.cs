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
                        // Straightening may move a node one extra step; still ≤ bead/2.
                        Assert.True(best <= 2f * MaxStep + 0.5f,
                            $"layer {i + 1} node ({node.X:0.#},{node.Y:0.#}) is {best:0.##} mm from layer {i}'s fingers");
                    }
        }
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

        float MaxRadiusBelowFlare(Toolpath tp) => tp.Layers
            .Where(l => l.Z > 80f && l.Z < 117f)
            .SelectMany(l => l.Moves.Where(m => m.Kind == MoveKind.Extrude))
            .SelectMany(m => new[] { m.From, m.To })
            .Max(pnt => new Vector2(pnt.X, pnt.Y).Length());

        // Off: nothing beyond the wall (r=57 inset + half bead of slit rounding).
        Assert.True(MaxRadiusBelowFlare(off) < 60f,
            $"external material without the checkbox: r={MaxRadiusBelowFlare(off):0.#}");
        // On: fins reach outward well past the wall beneath the flare.
        Assert.True(MaxRadiusBelowFlare(on) > 66f,
            $"fins did not grow outward: r={MaxRadiusBelowFlare(on):0.#}");

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
    public void AnchorClassCheckboxesControlWhereFingersRoot()
    {
        // Shrinking square (demand every layer) with a fixed centered hole: with
        // interior-only anchoring every tree roots on the hole boundary; with
        // exterior-only, on the outer square.
        List<List<List<Vector2>>> Polys()
        {
            var layers = new List<List<List<Vector2>>>();
            for (int i = 0; i < 8; i++)
            {
                float h = 90f - i * 6f;
                layers.Add(
                [
                    [new(-h, -h), new(h, -h), new(h, h), new(-h, h)],                     // outer CCW
                    [new(-20f, -20f), new(-20f, 20f), new(20f, 20f), new(20f, -20f)],     // hole CW
                ]);
            }
            return layers;
        }
        var heights = Enumerable.Repeat(LayerH, 8).ToList();

        var interiorOnly = LightningPlanner.Build(Polys(), heights, new SliceSettings
        {
            LayerHeight = LayerH, FirstLayerHeight = LayerH, BeadWidth = Bead,
            InfillPattern = InfillPattern.LightningBridge, LightningOverhangDeg = 30f,
            LightningAnchorInterior = true, LightningAnchorExterior = false,
        });
        var exteriorOnly = LightningPlanner.Build(Polys(), heights, new SliceSettings
        {
            LayerHeight = LayerH, FirstLayerHeight = LayerH, BeadWidth = Bead,
            InfillPattern = InfillPattern.LightningBridge, LightningOverhangDeg = 30f,
            LightningAnchorInterior = false, LightningAnchorExterior = true,
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
                float m = MathF.Max(MathF.Abs(t.Anchor.X), MathF.Abs(t.Anchor.Y));
                Assert.True(m > 25f,
                    $"exterior-only anchor ({t.Anchor.X:0.#},{t.Anchor.Y:0.#}) on the hole");
            }
        Assert.True(sawInterior, "no trees planned with interior anchoring");
        Assert.True(sawExterior, "no trees planned with exterior anchoring");
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

    private static float DistToSegment2D(Vector3 p, Vector3 a, Vector3 b)
    {
        float abx = b.X - a.X, aby = b.Y - a.Y;
        float len2 = abx * abx + aby * aby;
        float t = len2 < 1e-12f ? 0f
            : Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2, 0f, 1f);
        float cx = a.X + t * abx, cy = a.Y + t * aby;
        return MathF.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
    }
}
