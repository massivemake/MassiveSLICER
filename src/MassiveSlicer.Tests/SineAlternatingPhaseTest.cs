using System.Linq;
using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Sine cycles-per-layer: every layer carries the same whole number of cycles, spread
/// evenly along its own path from the same anchor, and flipped half a cycle from the
/// layer below. Same count + same anchor means cycle i occupies the same fraction of
/// the path on every layer, so the flip parks every peak over the valley beneath it —
/// the alignment is structural, not something that has to be measured and corrected.
/// </summary>
public class SineAlternatingPhaseTest
{
    private const float H = 4f;
    private const int Cycles = 12;

    /// <summary>Tapered cone: the perimeter shrinks every layer, so a fixed mm
    /// wavelength could never divide into it evenly.</summary>
    private static Toolpath Cone(int layers = 8, int samples = 1440)
    {
        var tp = new Toolpath();
        for (int li = 0; li < layers; li++)
        {
            float r = 200f - li * 9f, z = (li + 1) * H;
            var layer = new ToolpathLayer(li, z) { Height = H };
            Vector3 P(int i)
            {
                float a = i / (float)samples * 2f * MathF.PI;
                return new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z);
            }
            for (int i = 0; i < samples; i++)
                layer.Moves.Add(new ToolpathMove(P(i), P(i + 1), MoveKind.Extrude));
            tp.Layers.Add(layer);
        }
        return tp;
    }

    private static SliceSettings Settings(int cycles, float frequency = 999f) => new()
    {
        PatternType = PatternType.Sine,
        PatternAmplitude = 5f,
        PatternFrequency = frequency,               // must be ignored when cycles is set
        PatternMapping = PatternMappingMode.Wavelength,  // ditto
        PatternWavelengthMm = 10f,
        PatternSineCyclesPerLayer = cycles,
    };

    /// <summary>Radial deviation from the nominal circle, sampled by path fraction.</summary>
    private static float[] Profile(ToolpathLayer layer, float r, int buckets = 720)
    {
        var dev = new float[buckets];
        int n = layer.Moves.Count;
        for (int b = 0; b < buckets; b++)
        {
            var m = layer.Moves[(int)((long)b * n / buckets)];
            dev[b] = new Vector2(m.From.X, m.From.Y).Length() - r;
        }
        return dev;
    }

    [Fact]
    public void EveryPeakSitsOverAValleyOnTheLayerBelow()
    {
        var tp = PatternEffect.Apply(Cone(), Settings(Cycles));

        float[]? below = null;
        for (int li = 0; li < tp.Layers.Count; li++)
        {
            var dev = Profile(tp.Layers[li], 200f - li * 9f);
            if (below is not null)
            {
                // Opposite phase everywhere: the two profiles must be near mirror images.
                float dot = 0f, mag = 0f;
                for (int b = 0; b < dev.Length; b++) { dot += dev[b] * below[b]; mag += below[b] * below[b]; }
                float ratio = dot / mag;     // +1 = in phase (bad), -1 = perfectly opposed
                Assert.True(ratio < -0.9f,
                    $"layer {li} is not opposed to layer {li - 1}: correlation {ratio:F2} " +
                    "(-1 = peak over valley, +1 = peak stacked on peak)");
            }
            below = dev;
        }
    }

    [Fact]
    public void EachLayerCarriesExactlyTheRequestedWholeCycles()
    {
        var tp = PatternEffect.Apply(Cone(), Settings(Cycles));
        for (int li = 0; li < tp.Layers.Count; li++)
        {
            var dev = Profile(tp.Layers[li], 200f - li * 9f);
            Assert.Equal(Cycles, RisingCrossings(dev));

            // The last cycle finishes exactly at the seam — the loop closes on itself.
            var mv = tp.Layers[li].Moves;
            Assert.True(Vector3.Distance(mv[0].From, mv[^1].To) < 0.05f,
                $"layer {li} left a {Vector3.Distance(mv[0].From, mv[^1].To):F2} mm step at the seam");
        }
    }

    /// <summary>Counts whole cycles, wrap included — the seam crossing is a real one.</summary>
    private static int RisingCrossings(float[] dev)
    {
        int n = dev.Length, count = 0;
        for (int b = 0; b < n; b++)
            if (dev[(b + n - 1) % n] < 0f && dev[b] >= 0f) count++;
        return count;
    }

    /// <summary>
    /// 0 hands Sine back to the Distribution setting. Wavelength mapping holds the cycle
    /// SIZE fixed, so on a tapering part the cycle COUNT has to fall away layer by layer —
    /// which is exactly the drift the new mode exists to remove.
    /// </summary>
    [Fact]
    public void ZeroHandsSineBackToTheDistributionSetting()
    {
        var off = PatternEffect.Apply(Cone(), Settings(0));
        var on  = PatternEffect.Apply(Cone(), Settings(Cycles));

        var offCounts = new List<int>();
        var onCounts  = new List<int>();
        for (int li = 0; li < off.Layers.Count; li++)
        {
            float r = 200f - li * 9f;
            offCounts.Add(RisingCrossings(Profile(off.Layers[li], r)));
            onCounts .Add(RisingCrossings(Profile(on .Layers[li], r)));
        }

        Assert.True(offCounts.Distinct().Count() > 1,
            $"expected wavelength mapping to drift the cycle count, got {string.Join(",", offCounts)}");
        Assert.Single(onCounts.Distinct());
        Assert.Equal(Cycles, onCounts[0]);
    }
}
