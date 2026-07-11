using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Core.Slicing.Lightning;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Zig-zag seam mode: closed wall loops become a single open skin that reverses
/// each layer (no dual-wall back panel).
/// </summary>
public sealed class ZigZagSingleSkinTest
{
    private const float Bead = 6f;

    private static Vector3[] ThinWallBox(float len = 200f, float thick = 20f, float h = 60f)
    {
        float hx = len * 0.5f, hy = thick * 0.5f;
        var v = new Vector3[]
        {
            new(-hx, -hy, 0), new(hx, -hy, 0), new(hx, hy, 0), new(-hx, hy, 0),
            new(-hx, -hy, h), new(hx, -hy, h), new(hx, hy, h), new(-hx, hy, h),
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
        return [.. tris];
    }

    [Fact]
    public void ZigZagEmitsOpenPathsThatReverseEachLayer()
    {
        var settings = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
            InfillPattern = InfillPattern.None,
            ZigZagSeam = true,
        };
        var tp = PlanarSlicer.Slice([ThinWallBox()], settings, null);
        Assert.True(tp.Layers.Count >= 4, $"expected several layers, got {tp.Layers.Count}");

        // Collect first extrude direction of even vs odd layers (ignore layer-change travels).
        Vector2 Dir(ToolpathLayer lyr)
        {
            foreach (var m in lyr.Moves)
            {
                if (m.Kind != MoveKind.Extrude || m.IsLayerChange || m.IsLayerStitch) continue;
                var d = new Vector2(m.To.X - m.From.X, m.To.Y - m.From.Y);
                if (d.LengthSquared() > 1e-4f) return Vector2.Normalize(d);
            }
            return Vector2.Zero;
        }

        var d0 = Dir(tp.Layers[0]);
        var d1 = Dir(tp.Layers[1]);
        Assert.True(d0.LengthSquared() > 0.5f && d1.LengthSquared() > 0.5f,
            "missing extrude direction on first layers");
        // Consecutive layers should reverse (dot product negative).
        float dot = Vector2.Dot(d0, d1);
        Assert.True(dot < -0.3f,
            $"expected reverse zig-zag between layers, dir0={d0} dir1={d1} dot={dot:0.###}");
    }

    [Fact]
    public void ZigZagWithXBracingInsertsHairpinDetours()
    {
        var settings = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
            InfillPattern = InfillPattern.None,
            ZigZagSeam = true,
            XBracingEnabled = true,
            XBracingDepthMm = 25f,
            XBracingSpanMm = 80f,
            XBracingAngleDeg = 30f,
            XBracingExtendEdges = true,
            LightningOverhangDeg = 45f,
        };
        var tp = PlanarSlicer.Slice([ThinWallBox(len: 300f, thick: 30f, h: 90f)], settings, null);
        Assert.True(tp.Layers.Count >= 4);

        // Hairpins increase path length vs plain single-skin (~300 mm face).
        // Lower layers are short stubs; mid/upper layers grow — average still > plain face.
        float avg = 0f;
        int n = 0;
        foreach (var lyr in tp.Layers)
        {
            float len = 0f;
            foreach (var m in lyr.Moves)
            {
                if (m.Kind != MoveKind.Extrude || m.IsLayerChange || m.IsLayerStitch) continue;
                len += Vector3.Distance(m.From, m.To);
            }
            if (len < 1f) continue;
            avg += len;
            n++;
        }
        Assert.True(n > 0);
        avg /= n;
        Assert.True(avg > 310f,
            $"expected X hairpins to lengthen path, avg={avg:0.#} mm");
        Assert.True(avg < 900f,
            $"path too long ({avg:0.#} mm) — may have rebuilt a dual-wall panel");
    }

    [Fact]
    public void FirstOpenPathLayerBirthUsesFullDepthEvenWhenZIsElevated()
    {
        // Part bottom may sit far above world Z=0 (print bed offset). Birth depth
        // must still be full wantDepth on the first open-path / bed layer.
        var settings = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
            XBracingEnabled = true,
            XBracingDepthMm = 50f,
            XBracingSpanMm = 80f,
            XBracingAngleDeg = 30f,
            XBracingExtendEdges = true,
            LightningOverhangDeg = 30f,
        };
        var path = new List<Vector2>();
        for (int i = 0; i <= 20; i++)
            path.Add(new Vector2(i * 15f, 0f));
        var state = new XBracingPlanner.OpenPathDetourState();
        var contours = new List<List<Vector2>> { new(path) };
        var closed = new List<bool> { false };

        // Elevated first layer (e.g. mesh bottom at ~900 mm) — slicer marks bed via isBedLayer.
        float zElevated = 903f;
        XBracingPlanner.ApplyOpenPathDetours(
            contours, closed, zElevated, 3f, settings, state, isBedLayer: true);

        Assert.True(state.FirstOpenPathZ is float fz && MathF.Abs(fz - zElevated) < 0.1f,
            "FirstOpenPathZ should record the elevated first open layer");
        Assert.True(state.PrevList.Count > 0, "expected hairpins on first elevated layer");
        float maxDepth = state.PrevList.Max(h => h.Depth);
        Assert.True(maxDepth >= 49f,
            $"bed-layer birth should be full depth (~50), got max={maxDepth:0.##}");

        // Second elevated layer without isBedLayer: new births stay short; stacked grow from prev.
        var contours2 = new List<List<Vector2>> { new(path) };
        XBracingPlanner.ApplyOpenPathDetours(
            contours2, closed, zElevated + 3f, 3f, settings, state, isBedLayer: false);
        // Any brand-new key at mid-height should not force full wantDepth solely from absolute Z.
        Assert.True(state.FirstOpenPathZ is float fz2 && MathF.Abs(fz2 - zElevated) < 0.1f);
    }

    [Fact]
    public void CylinderHairpinsStayAimedAtAxisAcrossLayers()
    {
        // As the X mouth walks along the wall, tips must keep pointing at the cylinder
        // (no drift from prev-tip overhang clamps).
        var settings = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
            XBracingEnabled = true,
            XBracingDepthMm = 40f,
            XBracingSpanMm = 80f,
            XBracingAngleDeg = 30f,
            XBracingExtendEdges = true,
            XBracingProjectionType = "Cylinder",
            XBracingCylinderX = 0f,
            XBracingCylinderY = 0f,
            XBracingCylinderFlipDirection = false,
            LightningOverhangDeg = 30f,
        };
        // Wall along X at Y=100 (circle of mouths around origin would need a curve;
        // a straight wall still has fromAxis = mouth - 0, so aim is -Normalize(mouth)).
        var path = new List<Vector2>();
        for (int i = 0; i <= 20; i++)
            path.Add(new Vector2(i * 15f, 100f));
        var state = new XBracingPlanner.OpenPathDetourState();
        var axis = new Vector2(0f, 0f);

        for (int li = 0; li < 12; li++)
        {
            float z = 900f + (li + 1) * 3f;
            var contours = new List<List<Vector2>> { new(path) };
            var closed = new List<bool> { false };
            XBracingPlanner.ApplyOpenPathDetours(
                contours, closed, z, 3f, settings, state, isBedLayer: li == 0);

            foreach (var h in state.PrevList)
            {
                var aim = h.Tip - h.Mouth;
                float al = aim.Length();
                if (al < 1f) continue;
                aim /= al;
                // Toward origin from mouth.
                var want = axis - h.Mouth;
                float wl = want.Length();
                if (wl < 1e-3f) continue;
                want /= wl;
                float dot = Vector2.Dot(aim, want);
                Assert.True(dot > 0.98f,
                    $"layer {li} hairpin drifted off cylinder aim: mouth={h.Mouth} tip={h.Tip} dot={dot:0.###}");
            }
        }
    }

    [Fact]
    public void CylinderProjectionAimsTowardAxisByDefault()
    {
        var pull = new SliceSettings
        {
            XBracingEnabled = true,
            XBracingProjectionType = "Cylinder",
            XBracingCylinderX = 0f,
            XBracingCylinderY = 0f,
            XBracingCylinderFlipDirection = false,
        };
        // Mouth at +X → pull toward origin is −X.
        var toward = XBracingPlanner.BraceDirAt(pull, new Vector2(100f, 0f));
        Assert.True(toward.X < -0.9f && MathF.Abs(toward.Y) < 0.1f,
            $"expected pull toward axis (−X), got {toward}");

        var radiate = new SliceSettings
        {
            XBracingEnabled = true,
            XBracingProjectionType = "Cylinder",
            XBracingCylinderX = 0f,
            XBracingCylinderY = 0f,
            XBracingCylinderFlipDirection = true,
        };
        var outDir = XBracingPlanner.BraceDirAt(radiate, new Vector2(100f, 0f));
        Assert.True(outDir.X > 0.9f && MathF.Abs(outDir.Y) < 0.1f,
            $"expected radiate outward (+X), got {outDir}");

        // Direction must rotate with mouth (not freeze a world vector).
        var atY = XBracingPlanner.BraceDirAt(pull, new Vector2(0f, 100f));
        Assert.True(atY.Y < -0.9f && MathF.Abs(atY.X) < 0.1f,
            $"expected pull toward axis (−Y) at mouth on +Y, got {atY}");
    }

    [Fact]
    public void HairpinSupportFractionRequiresSixtyPercent()
    {
        // Parallel hairpins far apart → low support.
        var mouth = new Vector2(0, 0);
        var tip = new Vector2(0, 30);
        var prevMouth = new Vector2(20, 0);
        var prevTip = new Vector2(20, 30);
        float far = XBracingPlanner.SupportFraction(mouth, tip, prevMouth, prevTip, supportR: 6f);
        Assert.True(far < 0.3f, $"far parallel hairpins should be unsupported, got {far:0.##}");

        // Stacked (same locus, grown tip) → high support.
        var prevTipShort = new Vector2(0, 15);
        float stacked = XBracingPlanner.SupportFraction(mouth, tip, mouth, prevTipShort, supportR: 6f);
        Assert.True(stacked >= 0.55f, $"stacked growth should be mostly supported, got {stacked:0.##}");
    }

    [Fact]
    public void ZigZagXHairpinsStackOnPreviousLayer()
    {
        // Simulate multi-layer open-path detours and assert each hairpin is ≥60%
        // supported by its previous-layer parent (no floating spikes).
        var settings = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
            XBracingEnabled = true,
            XBracingDepthMm = 40f,
            XBracingSpanMm = 80f,
            XBracingAngleDeg = 30f,
            XBracingExtendEdges = true,
            LightningOverhangDeg = 30f,
        };

        // Straight open wall face along +X, left-normal (+Y) = into wall.
        var path = new List<Vector2>();
        for (int i = 0; i <= 20; i++)
            path.Add(new Vector2(i * 15f, 0f)); // 300 mm face
        var state = new XBracingPlanner.OpenPathDetourState();

        // Match planner support radius (bead * 1.15, maxStep * 1.5).
        float lh = 3f;
        float maxStep = MathF.Min(lh * MathF.Tan(30f * MathF.PI / 180f), 0.5f * Bead);
        maxStep = MathF.Max(maxStep, lh * 0.5f);
        float supportR = MathF.Max(Bead * 1.15f, maxStep * 1.5f);

        List<XBracingPlanner.Hairpin>? prevPins = null;
        int stackedChecks = 0;
        float minSupport = 1f;
        float maxDepthSeen = 0f;

        for (int li = 0; li < 25; li++)
        {
            float z = (li + 1) * lh;
            var contours = new List<List<Vector2>> { new(path) };
            var closedCopy = new List<bool> { false };
            XBracingPlanner.ApplyOpenPathDetours(contours, closedCopy, z, lh, settings, state);

            // After Apply, AdvanceLayer has run: Prev/PrevList hold this layer's pins.
            var pins = state.PrevList.ToList();
            foreach (var h in pins)
                maxDepthSeen = MathF.Max(maxDepthSeen, h.Depth);

            if (prevPins is { Count: > 0 })
            {
                foreach (var h in pins)
                {
                    // Birth stubs don't need a parent; grown pins must catch previous.
                    // Planner minDepth is bead*0.35 — ribs entering through a side
                    // edge mid-print are born at that depth (wall-supported bump).
                    if (h.Depth <= MathF.Max(maxStep * 1.15f, Bead * 0.4f)) continue;

                    float best = 0f;
                    foreach (var p in prevPins)
                    {
                        float f = XBracingPlanner.SupportFraction(
                            h.Mouth, h.Tip, p.Mouth, p.Tip, supportR);
                        if (f > best) best = f;
                    }
                    stackedChecks++;
                    minSupport = MathF.Min(minSupport, best);
                    Assert.True(best >= XBracingPlanner.MinSupportFraction - 0.02f,
                        $"layer {li} hairpin depth={h.Depth:0.#} support={best:0.##} " +
                        $"mouth={h.Mouth} tip={h.Tip} (need ≥{XBracingPlanner.MinSupportFraction:0%})");
                }
            }
            prevPins = pins;
        }

        Assert.True(stackedChecks >= 5,
            $"expected several stacked hairpin checks, got {stackedChecks}");
        Assert.True(maxDepthSeen > maxStep * 3f,
            $"hairpins should grow over layers, maxDepth={maxDepthSeen:0.#}");
        Assert.True(minSupport >= XBracingPlanner.MinSupportFraction - 0.02f,
            $"worst support {minSupport:0.##} below 60%");
    }

    [Fact]
    public void XHairpinMouthsWalkDiagonalNotVertical()
    {
        // Mouths must track the angle-driven X (ideal dS/layer ≈ lh·tanθ), not lag
        // into nearly vertical ridges when overhang clamps fire.
        var settings = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
            XBracingEnabled = true,
            XBracingDepthMm = 40f,
            XBracingSpanMm = 80f,
            XBracingAngleDeg = 30f,
            XBracingExtendEdges = true,
            LightningOverhangDeg = 30f,
        };

        var path = new List<Vector2>();
        for (int i = 0; i <= 20; i++)
            path.Add(new Vector2(i * 15f, 0f));
        var state = new XBracingPlanner.OpenPathDetourState();

        float lh = 3f;
        float span = 80f;
        float angleDeg = 30f;
        float cellH = span / MathF.Tan(angleDeg * MathF.PI / 180f);
        float idealDs = span * lh / cellH; // ≈ lh * tan(30°) ≈ 1.73 mm

        // Track one key's mouth S across layers (key for cell 0, diag 0).
        const int trackKey = 0; // ci=0, c=0, diag=0
        float? firstS = null;
        float lastS = 0f;
        float lastDepth = 0f;
        int tracked = 0;
        float sumAbsDs = 0f;
        int depthDrops = 0;
        float maxDepth = 0f;

        for (int li = 0; li < 30; li++)
        {
            float z = li * lh;
            var contours = new List<List<Vector2>> { new(path) };
            var closedCopy = new List<bool> { false };
            XBracingPlanner.ApplyOpenPathDetours(
                contours, closedCopy, z, lh, settings, state, isBedLayer: li == 0);

            if (!state.Prev.TryGetValue(trackKey, out var pin) || pin.Depth < 1e-3f)
                continue;

            maxDepth = MathF.Max(maxDepth, pin.Depth);

            if (firstS is null)
            {
                firstS = pin.S;
                lastS = pin.S;
                lastDepth = pin.Depth;
                continue;
            }

            sumAbsDs += MathF.Abs(pin.S - lastS);
            // Monotonic depth: free edge must not thrash (jagged sawtooth).
            if (pin.Depth < lastDepth - 0.5f)
                depthDrops++;
            lastS = pin.S;
            lastDepth = pin.Depth;
            tracked++;
        }

        Assert.True(tracked >= 10, $"expected tracked mouth steps, got {tracked}");
        float avgDs = sumAbsDs / tracked;
        // Average step should be a good fraction of ideal (allow some lag / meet merge).
        Assert.True(avgDs >= idealDs * 0.45f,
            $"mouth walk too small (vertical X?): avgDs={avgDs:0.##} ideal={idealDs:0.##}");
        // Full travel over many layers should move a substantial share of half-span.
        float totalTravel = MathF.Abs(lastS - firstS!.Value);
        Assert.True(totalTravel >= span * 0.25f,
            $"total mouth travel {totalTravel:0.#} too small for X lean (span={span})");
        Assert.True(depthDrops <= 1,
            $"depth thrashing ({depthDrops} drops) causes jagged free edges");
        Assert.True(maxDepth >= 35f,
            $"expected near-full depth on bed-born pin, max={maxDepth:0.#}");
    }

    [Fact]
    public void ZigZagDoesNotEmitClosedLoopBackPanel()
    {
        // A closed dual-wall panel would extrude roughly 2× the long face length per layer.
        // Single skin should be closer to one long face (~200 mm), not ~440 mm perimeter.
        var settings = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
            InfillPattern = InfillPattern.None,
            ZigZagSeam = true,
        };
        var tp = PlanarSlicer.Slice([ThinWallBox(len: 200f, thick: 20f)], settings, null);
        float avgExtrude = 0f;
        int n = 0;
        foreach (var lyr in tp.Layers)
        {
            float len = 0f;
            foreach (var m in lyr.Moves)
            {
                if (m.Kind != MoveKind.Extrude || m.IsLayerChange || m.IsLayerStitch) continue;
                len += Vector3.Distance(m.From, m.To);
            }
            if (len < 1f) continue;
            avgExtrude += len;
            n++;
        }
        Assert.True(n > 0);
        avgExtrude /= n;
        // Long face ~200 mm; full perimeter ~440 mm. Single skin should be well under 320.
        Assert.True(avgExtrude < 320f,
            $"avg extrude length {avgExtrude:0.#} mm looks like a closed back-panel, not single skin");
        Assert.True(avgExtrude > 100f,
            $"avg extrude length {avgExtrude:0.#} mm is too short for the long face");
    }
}
