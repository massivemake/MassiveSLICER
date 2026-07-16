using MassiveSlicer.Core.Slicing.Lightning;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class XBracingDepthEaseTest
{
    [Fact]
    public void Linear_IsIdentity()
    {
        for (float t = 0; t <= 1.001f; t += 0.1f)
        {
            float e = XBracingPlanner.DepthTaperEase(t, "Linear", "Linear");
            Assert.InRange(e, t - 0.02f, t + 0.02f);
        }
    }

    [Fact]
    public void EaseInBottom_SlowerNearBase()
    {
        // Ease-In at bottom → start slope 0 → progress lags early (stays near bottom depth longer)
        float midLinear = 0.5f;
        float midEased = XBracingPlanner.DepthTaperEase(0.5f, "Ease-In", "Linear");
        Assert.True(midEased < midLinear - 0.05f,
            $"Ease-In bottom should lag at mid height, got {midEased}");
    }

    [Fact]
    public void EaseOutTop_SlowerNearTop()
    {
        // Ease-Out at top → end slope 0 → progress leads mid, settles late
        float e = XBracingPlanner.DepthTaperEase(0.5f, "Linear", "Ease-Out");
        Assert.True(e > 0.5f - 0.02f, $"Ease-Out top mid should be ≥ linear, got {e}");
        float nearTop = XBracingPlanner.DepthTaperEase(0.9f, "Linear", "Ease-Out");
        // Soft settle: remaining gap to 1 is smaller rate than linear
        Assert.InRange(nearTop, 0.75f, 0.999f);
    }

    [Fact]
    public void Smooth_EndpointsFlat()
    {
        // Smooth = zero slopes both ends → classic smoothstep-ish (0 at 0, 1 at 1)
        Assert.Equal(0f, XBracingPlanner.DepthTaperEase(0f, "Smooth", "Smooth"), 3);
        Assert.Equal(1f, XBracingPlanner.DepthTaperEase(1f, "Smooth", "Smooth"), 3);
        float mid = XBracingPlanner.DepthTaperEase(0.5f, "Smooth", "Smooth");
        Assert.InRange(mid, 0.45f, 0.55f);
    }

    [Fact]
    public void EndpointsAlwaysZeroAndOne()
    {
        foreach (var bot in new[] { "Linear", "Ease-In", "Ease-Out", "Smooth" })
        foreach (var top in new[] { "Linear", "Ease-In", "Ease-Out", "Smooth" })
        {
            Assert.Equal(0f, XBracingPlanner.DepthTaperEase(0f, bot, top), 3);
            Assert.Equal(1f, XBracingPlanner.DepthTaperEase(1f, bot, top), 3);
        }
    }
}
