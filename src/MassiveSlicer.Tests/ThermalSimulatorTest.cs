using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;

namespace MassiveSlicer.Tests;

public class ThermalSimulatorTest
{
    private static SliceSettings Settings() => new()
    {
        BeadWidth = 6f, LayerHeight = 3f, PrintSpeedMps = 0.1f,
        ThermalDepositTempC = 250f, ThermalGlassTransitionC = 105f,
        ThermalAmbientTempC = 30f, ThermalDensityGmCc = 1.05f,
    };

    /// <summary>Square-perimeter layer of the given side length at height z.</summary>
    private static ToolpathLayer SquareLayer(int index, float z, float side)
    {
        var layer = new ToolpathLayer(index, z) { Height = 3f };
        Vector3[] c =
        [
            new(0, 0, z), new(side, 0, z), new(side, side, z), new(0, side, z), new(0, 0, z),
        ];
        for (int i = 1; i < c.Length; i++)
            layer.Moves.Add(new ToolpathMove(c[i - 1], c[i], MoveKind.Extrude));
        return layer;
    }

    private static Toolpath Stack(params float[] sides)
    {
        var tp = new Toolpath();
        for (int i = 0; i < sides.Length; i++)
            tp.Layers.Add(SquareLayer(i, 3f * (i + 1), sides[i]));
        return tp;
    }

    [Fact]
    public void SafeWindowIsOrderedAndPlausible()
    {
        var r = ThermalSimulator.Simulate(Stack(500f), Settings());
        Assert.True(r.TimeConstantS > 60f && r.TimeConstantS < 3600f,
            $"τ={r.TimeConstantS}");
        Assert.True(r.MinLayerTimeS > 0f);
        Assert.True(r.MinLayerTimeS < r.MaxLayerTimeS,
            $"window inverted: {r.MinLayerTimeS}..{r.MaxLayerTimeS}");
        Assert.InRange(r.TargetLayerTimeS, r.MinLayerTimeS, r.MaxLayerTimeS);
        // Interface at the target time sits inside the sag/bond window.
        Assert.InRange(r.PredictedInterfaceTempC, r.BondTempC, r.SagTempC);
    }

    [Fact]
    public void SpeedsScaleWithLayerLength()
    {
        // Two layer sizes 1:2 → recommended high/low speeds also 1:2 (same layer time).
        var r = ThermalSimulator.Simulate(Stack(400f, 800f), Settings());
        Assert.True(r.RecommendedMinMmS > 0f);
        Assert.Equal(2f, r.RecommendedMaxMmS / r.RecommendedMinMmS, 2);

        // Uniform stack → equal low and high.
        var u = ThermalSimulator.Simulate(Stack(500f, 500f, 500f), Settings());
        Assert.Equal(u.RecommendedMinMmS, u.RecommendedMaxMmS, 3);
    }

    [Fact]
    public void TinyLayerWarnsAboutSagLimit()
    {
        // 40mm perimeter takes ~40s even at the 1 mm/s floor — far under t_min (~minutes).
        var r = ThermalSimulator.Simulate(Stack(10f), Settings());
        Assert.Contains(r.Warnings, w => w.Contains("sag"));
    }

    [Fact]
    public void DegenerateDepositTempSetsNoSpeeds()
    {
        var s = new SliceSettings
        {
            BeadWidth = 6f, LayerHeight = 3f, PrintSpeedMps = 0.1f,
            ThermalDepositTempC = 120f,   // below the 150 °C sag limit
            ThermalGlassTransitionC = 105f, ThermalAmbientTempC = 30f,
        };
        var r = ThermalSimulator.Simulate(Stack(500f), s);
        Assert.Equal(0f, r.RecommendedMaxMmS);
        Assert.NotEmpty(r.Warnings);
    }

    [Fact]
    public void StampedTempsTrackLayerTime()
    {
        // Bigger previous layer = longer cooling = colder interface.
        var tp = Stack(400f, 400f, 1600f, 1600f);
        ThermalSimulator.StampLayerTemps(tp, Settings());

        foreach (var layer in tp.Layers)
            Assert.False(float.IsNaN(layer.ThermalTempC), $"layer {layer.Index} unstamped");

        // Layer 1 lands on a small (quick, hot) layer; layer 3 on a big (slow, cold) one.
        Assert.True(tp.Layers[1].ThermalTempC > tp.Layers[3].ThermalTempC,
            $"{tp.Layers[1].ThermalTempC:0.#} vs {tp.Layers[3].ThermalTempC:0.#}");

        // All temps between ambient and deposit.
        var s = Settings();
        foreach (var layer in tp.Layers)
            Assert.InRange(layer.ThermalTempC, s.ThermalAmbientTempC, s.ThermalDepositTempC);
    }

    [Fact]
    public void StampedTempsIgnoreAdaptiveSpeedScale_ShortLayersStayHot()
    {
        // Regression: with adaptive speed/flow on, short layers are printed SLOW (low
        // PrintSpeedScale) and long layers FAST. If the thermal stamp used those adaptive
        // speeds, a short layer's long print time would make the layer above it read COOL —
        // inverting the map so short layers show blue. The thermal view must reflect layer
        // geometry (base speed): short layers hot, long layers cool, regardless of adaptive.
        var tp = Stack(400f, 400f, 1600f, 1600f);
        // Simulate adaptive comp: short (400) layers slowed to 0.25x, long (1600) sped to 1.0x.
        foreach (var layer in tp.Layers)
        {
            float scale = layer.Index < 2 ? 0.25f : 1.0f; // short slow, long fast
            for (int mi = 0; mi < layer.Moves.Count; mi++)
                layer.Moves[mi] = layer.Moves[mi] with { PrintSpeedScale = scale };
        }

        ThermalSimulator.StampLayerTemps(tp, Settings());

        // Even though the short layers were slowed, the layer sitting on a short layer must
        // still read HOTTER than the layer sitting on a long layer (short region = red).
        Assert.True(tp.Layers[1].ThermalTempC > tp.Layers[3].ThermalTempC,
            $"short-layer interface should be hotter: L1={tp.Layers[1].ThermalTempC:0.#} vs L3={tp.Layers[3].ThermalTempC:0.#}");
    }

    [Fact]
    public void CloneCarriesThermalAndLightningData()
    {
        var tp = Stack(400f);
        tp.Layers[0].ThermalTempC = 123f;
        tp.Layers[0].Contours.Add(new ContourSpan(0, 4, true, -1));
        tp.Layers[0].Moves[0] = tp.Layers[0].Moves[0] with { IsLightning = true };

        var copy = MassiveSlicer.Core.Slicing.ToolpathClone.Copy(tp);
        Assert.Equal(123f, copy.Layers[0].ThermalTempC);
        Assert.Single(copy.Layers[0].Contours);
        Assert.True(copy.Layers[0].Moves[0].IsLightning);
    }
}
