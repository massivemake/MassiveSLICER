using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Lightning;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// X-bracing depth may taper over height: <see cref="SliceSettings.XBracingDepthMm"/>
/// is the depth at the TOP of the part and <see cref="SliceSettings.XBracingDepthBottomMm"/>
/// (when &gt; 0) sets a different depth at the bottom, interpolated linearly with the
/// layer's world Z between the part's zMin and zMax. 0 keeps the classic constant depth.
/// </summary>
public sealed class XBracingDepthTaperTest
{
    private const float Bead = 6f;
    private const int Layers = 200;
    private const float Lh = 3f;
    private const float ZMin = 0f;
    private static readonly float ZMax = (Layers - 1) * Lh;

    private static SliceSettings Settings(float depthTop, float depthBottom) => new()
    {
        SlicingMode = SlicingMode.Surface,
        LayerHeight = Lh, FirstLayerHeight = Lh, BeadWidth = Bead,
        InfillPattern = InfillPattern.None,
        ZigZagSeam = true,
        XBracingEnabled = true,
        XBracingDepthMm = depthTop,
        XBracingDepthBottomMm = depthBottom,
        XBracingSpanMm = 120f,
        XBracingAngleDeg = 30f,
        LightningOverhangDeg = 30f,
    };

    /// <summary>Flat unrolled wall: a straight open path, reversed on odd layers (zig-zag).</summary>
    private static List<Vector2> WallPath(bool reversed)
    {
        var p = new List<Vector2>();
        for (int i = 0; i <= 120; i++)
            p.Add(new Vector2(-300f + 600f * i / 120f, 0f));
        if (reversed) p.Reverse();
        return p;
    }

    private static float[] RunDepths(float depthTop, float depthBottom)
    {
        var settings = Settings(depthTop, depthBottom);
        var state = new XBracingPlanner.OpenPathDetourState { PartZMin = ZMin, PartZMax = ZMax };
        var maxDepthPerLayer = new float[Layers];
        for (int li = 0; li < Layers; li++)
        {
            float z = ZMin + li * Lh;
            var contours = new List<List<Vector2>> { WallPath(reversed: li % 2 == 1) };
            var closed = new List<bool> { false };
            XBracingPlanner.ApplyOpenPathDetours(
                contours, closed, z, Lh, settings, state, isBedLayer: li == 0);
            float m = 0f;
            foreach (var h in state.PrevList) m = MathF.Max(m, h.Depth);
            maxDepthPerLayer[li] = m;
        }
        return maxDepthPerLayer;
    }

    [Fact]
    public void BottomDepthTapersIndependentlyOfTop()
    {
        var taper    = RunDepths(depthTop: 60f, depthBottom: 10f);
        var constant = RunDepths(depthTop: 60f, depthBottom: 0f); // 0 = constant 60

        // Mid-low layer (frac ≈ 0.3): growth has long saturated. The taper ceiling
        // there is ~10 + 50·0.3 = 25 mm, while the constant run reaches ~60 mm.
        int lo = 60;
        Assert.True(taper[lo] < constant[lo] - 20f,
            $"taper depth at layer {lo} ({taper[lo]:0.#}) should be far below constant ({constant[lo]:0.#})");

        // High layer (frac ≈ 0.95): taper approaches the top depth.
        int hi = 190;
        Assert.True(taper[hi] > taper[lo] + 15f,
            $"taper depth must rise with height: layer {hi}={taper[hi]:0.#} vs {lo}={taper[lo]:0.#}");
        Assert.True(taper[hi] > 45f,
            $"taper depth near the top ({taper[hi]:0.#}) should approach the 60 mm top depth");

        // Constant control: no meaningful height dependence once saturated.
        Assert.True(MathF.Abs(constant[hi] - constant[lo]) < 8f,
            $"constant depth should be height-independent: {lo}={constant[lo]:0.#} {hi}={constant[hi]:0.#}");
    }

    [Fact]
    public void ZeroBottomKeepsConstantDepth()
    {
        // Default (bottom = 0) must behave exactly like the classic single-depth path.
        var withRange = RunDepths(depthTop: 45f, depthBottom: 0f);
        var state = new XBracingPlanner.OpenPathDetourState(); // no PartZMin/Max set
        var settings = Settings(45f, 0f);
        float topMax = 0f;
        for (int li = 0; li < Layers; li++)
        {
            float z = ZMin + li * Lh;
            var contours = new List<List<Vector2>> { WallPath(li % 2 == 1) };
            var closed = new List<bool> { false };
            XBracingPlanner.ApplyOpenPathDetours(contours, closed, z, Lh, settings, state, isBedLayer: li == 0);
            if (li == Layers - 1)
                foreach (var h in state.PrevList) topMax = MathF.Max(topMax, h.Depth);
        }
        // Same saturated depth whether or not the Z range is supplied (bottom = 0).
        Assert.True(MathF.Abs(withRange[Layers - 1] - topMax) < 3f,
            $"bottom=0 must be constant regardless of Z range: {withRange[Layers - 1]:0.#} vs {topMax:0.#}");
    }
}
