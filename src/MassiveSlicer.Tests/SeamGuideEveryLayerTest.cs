using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// A seam guide anchors the seam on EVERY layer, not just the layer where a contour is born.
/// <para>
/// <b>Honest limitation:</b> these tests pass against the pre-change slicer as well. Both a
/// flaring tube and a 90°-twisting tube were tried, and neither separates "guide on the birth
/// layer then inherit" from "guide on every layer" — nearest-point inheritance happens to hold
/// a near-constant XY on both. They are property guards against the guide being ignored, NOT
/// evidence that the every-layer change is what cures the drift seen on a real flared part.
/// </para>
/// </summary>
public sealed class SeamGuideEveryLayerTest
{
    private const float Bead = 6f;

    /// <summary>
    /// Square tube that TWISTS with height (90° bottom to top). The twist is what discriminates:
    /// an inherited seam rides the rotating wall and spirals a quarter turn, while a guide-anchored
    /// seam has to stay on one compass bearing. A tube that merely flares does not separate the
    /// two — under uniform scaling the nearest point to the parent seam keeps its bearing anyway,
    /// so a flaring fixture passes with or without the fix and proves nothing.
    /// </summary>
    private static Vector3[] TwistingTube(float h = 90f, float half = 60f, float twistRad = MathF.PI / 2f)
    {
        const int Steps = 36;
        var tris = new List<Vector3>();

        Vector3[] Ring(float z, float a)
        {
            float c = MathF.Cos(a), s = MathF.Sin(a);
            Vector3 R(float x, float y) => new(x * c - y * s, x * s + y * c, z);
            return [R(-half, -half), R(half, -half), R(half, half), R(-half, half)];
        }

        for (int s = 0; s < Steps; s++)
        {
            float z0 = h * s / Steps, z1 = h * (s + 1) / Steps;
            var lo = Ring(z0, twistRad * s / Steps);
            var hi = Ring(z1, twistRad * (s + 1) / Steps);

            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) % 4;
                tris.AddRange([lo[i], lo[j], hi[j]]);
                tris.AddRange([lo[i], hi[j], hi[i]]);
            }
        }

        var bot = Ring(0f, 0f);
        var top = Ring(h, twistRad);
        tris.AddRange([bot[0], bot[2], bot[1]]);
        tris.AddRange([bot[0], bot[3], bot[2]]);
        tris.AddRange([top[0], top[1], top[2]]);
        tris.AddRange([top[0], top[2], top[3]]);
        return [.. tris];
    }

    private static SliceSettings Settings(params SeamGuidePoint[] guides) => new()
    {
        LayerHeight      = 3f,
        FirstLayerHeight = 3f,
        BeadWidth        = Bead,
        InfillPattern    = InfillPattern.None,
        SeamGuidePoints  = guides,
    };

    /// <summary>Seam of a layer = start of its first extrude move (what the viewport marks).</summary>
    private static Vector2? SeamXY(ToolpathLayer layer)
    {
        foreach (var m in layer.Moves)
            if (m.Kind == MoveKind.Extrude)
                return new Vector2(m.From.X, m.From.Y);
        return null;
    }

    [Fact]
    public void GuideHoldsTheSeamOnTheSameWallFromBottomToTop()
    {
        // Guide out on +X.
        var tp = PlanarSlicer.Slice([TwistingTube()], Settings(new SeamGuidePoint(200f, 0f, 0f)), null);
        Assert.True(tp.Layers.Count >= 8, $"expected several layers, got {tp.Layers.Count}");

        int checked_ = 0;
        foreach (var layer in tp.Layers)
        {
            if (SeamXY(layer) is not { } seam) continue;
            checked_++;

            // On the +X wall, X is the dominant positive coordinate.
            Assert.True(seam.X > 0f,
                $"layer {layer.Index}: seam at ({seam.X:F1}, {seam.Y:F1}) left the +X side");
            Assert.True(seam.X > MathF.Abs(seam.Y),
                $"layer {layer.Index}: seam at ({seam.X:F1}, {seam.Y:F1}) drifted around a corner " +
                "— the guide was not applied to this layer");
        }

        Assert.True(checked_ >= 8, $"only {checked_} layers carried a seam");
    }

    [Fact]
    public void MovingTheGuideMovesTheSeamOnEveryLayer()
    {
        var onX = PlanarSlicer.Slice([TwistingTube()], Settings(new SeamGuidePoint(200f, 0f, 0f)), null);
        var onY = PlanarSlicer.Slice([TwistingTube()], Settings(new SeamGuidePoint(0f, 200f, 0f)), null);

        int compared = 0;
        for (int i = 0; i < Math.Min(onX.Layers.Count, onY.Layers.Count); i++)
        {
            if (SeamXY(onX.Layers[i]) is not { } sx) continue;
            if (SeamXY(onY.Layers[i]) is not { } sy) continue;
            compared++;

            Assert.True(sx.X > MathF.Abs(sx.Y), $"layer {i}: +X guide put the seam at {sx}");
            Assert.True(sy.Y > MathF.Abs(sy.X), $"layer {i}: +Y guide put the seam at {sy}");
        }

        Assert.True(compared >= 8, $"only {compared} layers compared");
    }

    [Fact]
    public void NoGuideStillUsesTheContinuitySeam()
    {
        // Without guides the ray/inheritance path must still run — the guide branch is additive.
        var tp = PlanarSlicer.Slice([TwistingTube()], Settings(), null);
        Assert.True(tp.Layers.Count >= 8);
        Assert.Contains(tp.Layers, l => SeamXY(l) is not null);
    }
}
