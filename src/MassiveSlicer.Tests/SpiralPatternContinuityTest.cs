using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Core.Slicing.Effects;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// A patterned wall has to stay one closed, continuous loop, because spiral mode only
/// ramps a loop it can see closing — a torn or unclosed loop is left flat, and a flat
/// layer between two ramped ones collides with the one below and gaps against the one
/// above. Two ways the pattern used to break the loop:
///   • Wavelength mapping laid cycles at a fixed mm pitch from the seam, so unless the
///     perimeter was a whole multiple of the wavelength the wave stepped at the seam.
///   • The displacement rode the per-segment normal, which flips through a corner, so
///     the two sides of every corner pushed apart.
/// </summary>
public class SpiralPatternContinuityTest
{
    private const float H = 3f;

    private static Toolpath Build(Func<int, float, Vector3> pt, int samples, int layers = 40)
    {
        var tp = new Toolpath();
        for (int li = 0; li < layers; li++)
        {
            var layer = new ToolpathLayer(li, li * H) { Height = H };
            for (int i = 0; i < samples; i++)
                layer.Moves.Add(new ToolpathMove(
                    pt(li, i / (float)samples), pt(li, (i + 1) / (float)samples), MoveKind.Extrude));
            tp.Layers.Add(layer);
        }
        return tp;
    }

    /// <summary>Tapered cone — perimeter changes every layer, so no wavelength divides it.</summary>
    private static Toolpath Cone() => Build((li, u) =>
    {
        float r = 200f - li * 1.7f, a = u * 2f * MathF.PI;
        return new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), li * H);
    }, 360);

    /// <summary>Tapered square — four hard right-angle corners for the normal to turn through.</summary>
    private static Toolpath Box() => Build((li, u) =>
    {
        float s = 200f - li * 1.7f;
        float t = u * 4f; int side = Math.Min(3, (int)t); float f = t - side;
        var c = new[] { new Vector2(-s, -s), new Vector2(s, -s), new Vector2(s, s), new Vector2(-s, s) };
        var p = Vector2.Lerp(c[side], c[(side + 1) % 4], f);
        return new Vector3(p.X, p.Y, li * H);
    }, 400);

    private static SliceSettings Settings(PatternMappingMode mode, float frequency) => new()
    {
        Spiralize           = true,
        PatternType         = PatternType.Sine,
        PatternAmplitude    = 2f,
        PatternFrequency    = frequency,
        PatternMapping      = mode,
        PatternWavelengthMm = 10f,
        LayerHeight         = H,
    };

    public static TheoryData<string, PatternMappingMode, float> Cases => new()
    {
        { "cone", PatternMappingMode.ArcLength,  20f },
        { "cone", PatternMappingMode.Wavelength, 20f },
        { "box",  PatternMappingMode.ArcLength,  20f },
        { "box",  PatternMappingMode.ArcLength,  21f },   // corners off the zero crossing
        { "box",  PatternMappingMode.Wavelength, 20f },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void PatternedWallStaysClosedAndEveryLayerSpirals(
        string shape, PatternMappingMode mode, float frequency)
    {
        var settings  = Settings(mode, frequency);
        var patterned = PatternEffect.Apply(shape == "cone" ? Cone() : Box(), settings);
        var spiralled = SpiralizeEffect.Apply(patterned, settings);

        foreach (var layer in patterned.Layers)
        {
            var m = layer.Moves;

            // No tear where two moves meet: they share a vertex, so they must share a point.
            for (int k = 1; k < m.Count; k++)
                Assert.True(Vector3.Distance(m[k].From, m[k - 1].To) < 0.05f,
                    $"{shape}/{mode}/f{frequency} layer {layer.Index} tore " +
                    $"{Vector3.Distance(m[k].From, m[k - 1].To):F2} mm between moves {k - 1} and {k}");

            // And the loop closes back on its own start.
            Assert.True(Vector3.Distance(m[0].From, m[^1].To) < 0.05f,
                $"{shape}/{mode}/f{frequency} layer {layer.Index} left a " +
                $"{Vector3.Distance(m[0].From, m[^1].To):F2} mm step at the seam");
        }

        // Every layer therefore ramps a full layer height — none is left flat.
        foreach (var layer in spiralled.Layers)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var m in layer.Moves) { lo = MathF.Min(lo, m.To.Z); hi = MathF.Max(hi, m.To.Z); }
            Assert.True(hi - lo > H * 0.9f,
                $"{shape}/{mode}/f{frequency} layer {layer.Index} was left flat (rise {hi - lo:F2} mm)");
        }
    }

    /// <summary>
    /// Each loop has to rise by the gap to the layer ABOVE it. Layer.Height is the gap
    /// BELOW (this Z minus the previous Z), so under adaptive or support-driven heights
    /// the two differ and ramping by the wrong one opens a gap beneath every thinned
    /// layer and drives the ramp through the one above.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RampLandsExactlyWhereTheNextLayerStarts(bool withPattern)
    {
        // Mixed thicknesses, the way adaptive / support-driven slicing produces them.
        var heights = new[] { 3f, 3f, 1.5f, 1.5f, 3f, 3f, 0.8f, 3f, 3f, 1.5f, 3f, 3f };
        var tp = new Toolpath();
        float prevZ = 0f;
        for (int li = 0; li < heights.Length; li++)
        {
            float z = prevZ + heights[li];
            var layer = new ToolpathLayer(li, z) { Height = z - prevZ };
            for (int i = 0; i < 180; i++)
            {
                float a0 = i / 180f * 2f * MathF.PI, a1 = (i + 1) / 180f * 2f * MathF.PI;
                layer.Moves.Add(new ToolpathMove(
                    new Vector3(200f * MathF.Cos(a0), 200f * MathF.Sin(a0), z),
                    new Vector3(200f * MathF.Cos(a1), 200f * MathF.Sin(a1), z), MoveKind.Extrude));
            }
            tp.Layers.Add(layer);
            prevZ = z;
        }

        var settings = withPattern
            ? Settings(PatternMappingMode.ArcLength, 20f)
            : new SliceSettings { Spiralize = true };
        var sp = SpiralizeEffect.Apply(withPattern ? PatternEffect.Apply(tp, settings) : tp, settings);

        for (int li = 0; li < sp.Layers.Count - 1; li++)
        {
            float rampEnd   = sp.Layers[li].Moves[^1].To.Z;
            float nextStart = sp.Layers[li + 1].Moves[0].From.Z;
            Assert.True(MathF.Abs(nextStart - rampEnd) < 0.01f,
                $"pattern={withPattern} layer {li}: ramp ended at {rampEnd:F2} but the next " +
                $"layer starts at {nextStart:F2} — {nextStart - rampEnd:+0.00;-0.00} mm out");
        }
    }

    /// <summary>
    /// The whole spiral has to be one unbroken path. The layer-change stitch is built from
    /// the previous layer's end as it stood BEFORE the ramp, so after ramping it starts a
    /// full layer height too low — the tool drops back down and re-climbs at every layer.
    /// </summary>
    [Fact]
    public void LayerChangeConnectorStartsWhereThePreviousLayerActuallyEnded()
    {
        const float lh = 4f;
        var tp = new Toolpath();
        Vector3 Pt(float r, float ang, float z) =>
            new(r * MathF.Cos(ang), r * MathF.Sin(ang), z);

        var prevEnd = Pt(200f, 0f, 0f);
        for (int li = 0; li < 12; li++)
        {
            float z = (li + 1) * lh;
            var layer = new ToolpathLayer(li, z) { Height = lh };
            for (int i = 0; i < 240; i++)
                layer.Moves.Add(new ToolpathMove(
                    Pt(200f, i / 240f * 2f * MathF.PI, z),
                    Pt(200f, (i + 1) / 240f * 2f * MathF.PI, z), MoveKind.Extrude));
            // Exactly what the slicer does: join from where the previous layer ended.
            ToolpathLayerConnect.Insert(layer, prevEnd, 10f);
            prevEnd = layer.Moves[^1].To;
            tp.Layers.Add(layer);
        }

        var settings = Settings(PatternMappingMode.ArcLength, 20f);
        var sp = SpiralizeEffect.Apply(PatternEffect.Apply(tp, settings), settings);

        Vector3? end = null;
        foreach (var layer in sp.Layers)
            foreach (var m in layer.Moves)
            {
                if (end is Vector3 e)
                    Assert.True(Vector3.Distance(e, m.From) < 0.01f,
                        $"layer {layer.Index}: path jumps {Vector3.Distance(e, m.From):F2} mm " +
                        $"(z {e.Z:F2} -> {m.From.Z:F2}) before this move");
                end = m.To;
            }
    }

    /// <summary>
    /// Real contours carry the odd zero-length move. It has no tangent of its own, but it
    /// still has to be displaced with its neighbours — left behind while the wall moves out
    /// from under it, it opens a gap the width of the pattern, which splits the loop and
    /// costs that layer its spiral entirely.
    /// </summary>
    [Fact]
    public void DegenerateMoveTravelsWithTheWallAndKeepsTheLoopIntact()
    {
        const float lh = 4f;
        var tp = new Toolpath();
        for (int li = 0; li < 6; li++)
        {
            float z = (li + 1) * lh;
            var layer = new ToolpathLayer(li, z) { Height = lh };
            Vector3 Pt(int i) => new(200f * MathF.Cos(i / 240f * 2f * MathF.PI),
                                     200f * MathF.Sin(i / 240f * 2f * MathF.PI), z);
            for (int i = 0; i < 240; i++)
            {
                layer.Moves.Add(new ToolpathMove(Pt(i), Pt(i + 1), MoveKind.Extrude));
                if (i == 120)   // a duplicated vertex, exactly as the slicer emits it
                    layer.Moves.Add(new ToolpathMove(Pt(121), Pt(121), MoveKind.Extrude));
            }
            tp.Layers.Add(layer);
        }

        var settings = Settings(PatternMappingMode.Wavelength, 20f);
        var patterned = PatternEffect.Apply(tp, settings);

        foreach (var layer in patterned.Layers)
            for (int k = 1; k < layer.Moves.Count; k++)
                Assert.True(
                    Vector3.Distance(layer.Moves[k].From, layer.Moves[k - 1].To) < 0.05f,
                    $"layer {layer.Index}: the wall split by " +
                    $"{Vector3.Distance(layer.Moves[k].From, layer.Moves[k - 1].To):F2} mm at move {k}");

        foreach (var layer in SpiralizeEffect.Apply(patterned, settings).Layers)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var m in layer.Moves) { lo = MathF.Min(lo, m.To.Z); hi = MathF.Max(hi, m.To.Z); }
            Assert.True(hi - lo > lh * 0.9f,
                $"layer {layer.Index} was left flat (rise {hi - lo:F2} mm)");
        }
    }

    /// <summary>The pattern must not drop the layer metadata the rest of the pipeline reads.</summary>
    [Fact]
    public void PatternKeepsLayerHeight()
    {
        var settings = Settings(PatternMappingMode.ArcLength, 20f);
        foreach (var layer in PatternEffect.Apply(Cone(), settings).Layers)
            Assert.Equal(H, layer.Height, 3);
    }
}
