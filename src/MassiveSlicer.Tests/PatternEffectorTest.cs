using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Live effector semantics on the pattern system. Erase must mean "the sine wave is
/// not applied in that area": zero displacement through the inner influence region
/// (even though the effector point hovers off the wall), full pattern outside the
/// radius, and a smooth blend between — never a merely weakened wave at the centre.
/// </summary>
public class PatternEffectorTest
{
    private const float R = 500f;   // circle radius (mm)

    /// <summary>Circular wall, 40 layers × 360 segments, centred on the origin.</summary>
    private static Toolpath BuildCylinder()
    {
        var tp = new Toolpath();
        for (int li = 0; li < 40; li++)
        {
            var layer = new ToolpathLayer(li, li * 3f);
            Vector3 Pt(int i)
            {
                float a = i / 360f * 2f * MathF.PI;
                return new Vector3(R * MathF.Cos(a), R * MathF.Sin(a), li * 3f);
            }
            for (int i = 0; i < 360; i++)
                layer.Moves.Add(new ToolpathMove(Pt(i), Pt(i + 1), MoveKind.Extrude));
            tp.Layers.Add(layer);
        }
        return tp;
    }

    private static SliceSettings Settings(EffectorMode mode, Vector3 effector) => new()
    {
        PatternType        = PatternType.Sine,
        PatternAmplitude   = 12f,
        PatternFrequency   = 12f,
        EffectorPoints     = [effector],
        EffectorRadiusMm   = 300f,
        EffectorStrengthMm = 20f,
        EffectorMode       = mode,
    };

    private static float MaxRadialDeviation(Toolpath tp, Func<Vector3, bool> where)
    {
        float max = 0f;
        foreach (var layer in tp.Layers)
            foreach (var m in layer.Moves)
            {
                if (!where(m.To)) continue;
                max = MathF.Max(max, MathF.Abs(new Vector2(m.To.X, m.To.Y).Length() - R));
            }
        return max;
    }

    [Fact]
    public void EraseZeroesThePatternInsideTheCoreAndKeepsItOutside()
    {
        // Effector hovers 100 mm off the wall (like a real handle in space): the wall's
        // nearest region is well inside the 60%-of-radius full-erase core.
        var effector = new Vector3(R - 100f, 0f, 60f);
        var tp = PatternEffect.Apply(BuildCylinder(), Settings(EffectorMode.Erase, effector));

        float insideCore = MaxRadialDeviation(tp, p =>
            Vector3.Distance(p, effector) < 300f * 0.5f);
        float outside = MaxRadialDeviation(tp, p =>
            Vector3.Distance(p, effector) > 300f * 1.2f);

        Assert.True(insideCore < 0.5f, $"pattern should be erased near the effector, saw {insideCore:F2} mm");
        Assert.True(outside > 8f, $"pattern should be untouched outside the radius, saw {outside:F2} mm");
    }

    [Fact]
    public void AmplifyBoostsThePatternNearTheEffector()
    {
        var effector = new Vector3(R - 100f, 0f, 60f);
        var tp = PatternEffect.Apply(BuildCylinder(), Settings(EffectorMode.Amplify, effector));

        float near = MaxRadialDeviation(tp, p => Vector3.Distance(p, effector) < 150f);
        float far  = MaxRadialDeviation(tp, p => Vector3.Distance(p, effector) > 400f);

        Assert.True(near > far + 3f, $"near {near:F2} mm should exceed far {far:F2} mm");
    }

    [Fact]
    public void EraseBlendsSmoothlyAcrossTheOuterBand()
    {
        var effector = new Vector3(R - 100f, 0f, 60f);
        var tp = PatternEffect.Apply(BuildCylinder(), Settings(EffectorMode.Erase, effector));

        // Mid-band (d ≈ 250 of 300): suppression ≈ 44%, so the deviation must sit
        // strictly between the erased core and the full 12 mm pattern.
        float band = MaxRadialDeviation(tp, p =>
            Vector3.Distance(p, effector) is > 245f and < 255f);
        Assert.InRange(band, 2f, 11f);
    }
}
