using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;

namespace MassiveSlicer.Tests;

/// <summary>
/// Adaptive layer height varies Z spacing; extruder RPM is derived from the nominal
/// LayerHeight. These pin the correction that makes flow follow the real thickness.
///
/// Numbers come from a real part (Dragon column, PPGF on the HF head): bead 6 mm,
/// nominal layer 3 mm, 60 mm/s, flow 0.5862 rev/cm3 -> 37.98576 % nominal RPM. Its
/// thinnest layer measured 1.753 mm, which was being given a full 3 mm of material.
/// </summary>
public class LayerHeightFlowPostProcessorTest
{
    private const float Nominal = 3f;

    [Fact]
    public void Thin_layer_gets_flow_cut_in_proportion_to_its_real_thickness()
    {
        var tp = new Toolpath();
        tp.Layers.Add(MakeLayer(0, 10f, height: 3.000f));
        tp.Layers.Add(MakeLayer(1, 12f, height: 1.753f));   // the worst real layer
        tp.Layers.Add(MakeLayer(2, 15f, height: 2.364f));

        LayerHeightFlowPostProcessor.Apply(tp, Adaptive(true));

        Assert.Equal(1.000f, HeightScale(tp, 0), 3);
        Assert.Equal(0.584f, HeightScale(tp, 1), 3);   // 1.753 / 3
        Assert.Equal(0.788f, HeightScale(tp, 2), 3);   // 2.364 / 3
    }

    /// <summary>
    /// The one that matters: the number the KRL exporter actually writes. If this passes
    /// while the toolpath is wrong, the fix is cosmetic.
    /// </summary>
    [Fact]
    public void Exported_rpm_for_a_thin_layer_drops_by_the_same_proportion()
    {
        var tp = new Toolpath();
        tp.Layers.Add(MakeLayer(0, 10f, height: 3.000f));
        tp.Layers.Add(MakeLayer(1, 12f, height: 1.753f));

        var krl = new KrlExportSettings
        {
            ProgramName   = "T",
            BeadWidthMm   = 6f,
            LayerHeightMm = 3f,
            PrintSpeedMps = 0.06f,
            FlowRate      = 0.5862f,
        };

        // Before: both layers demand the full nominal RPM — that is the bug.
        Assert.Equal(37.986f, ToolpathRpm.MovePercent(tp.Layers[0].Moves[0], krl), 2);
        Assert.Equal(37.986f, ToolpathRpm.MovePercent(tp.Layers[1].Moves[0], krl), 2);

        LayerHeightFlowPostProcessor.Apply(tp, Adaptive(true));

        Assert.Equal(37.986f, ToolpathRpm.MovePercent(tp.Layers[0].Moves[0], krl), 2);
        Assert.Equal(22.196f, ToolpathRpm.MovePercent(tp.Layers[1].Moves[0], krl), 2);
    }

    [Fact]
    public void Does_nothing_when_adaptive_layer_height_is_off()
    {
        var tp = new Toolpath();
        tp.Layers.Add(MakeLayer(0, 10f, height: 1.5f));

        LayerHeightFlowPostProcessor.Apply(tp, Adaptive(false));

        Assert.Equal(1f, HeightScale(tp, 0), 5);
    }

    [Fact]
    public void Applying_twice_does_not_compound()
    {
        var tp = new Toolpath();
        tp.Layers.Add(MakeLayer(0, 10f, height: 1.5f));

        LayerHeightFlowPostProcessor.Apply(tp, Adaptive(true));
        LayerHeightFlowPostProcessor.Apply(tp, Adaptive(true));

        Assert.Equal(0.5f, HeightScale(tp, 0), 5);
    }

    [Fact]
    public void A_wild_z_gap_from_a_skipped_plane_is_clamped_not_obeyed()
    {
        var tp = new Toolpath();
        tp.Layers.Add(MakeLayer(0, 10f, height: 300f));    // absurd — several layers of gap
        tp.Layers.Add(MakeLayer(1, 12f, height: 0.001f));

        LayerHeightFlowPostProcessor.Apply(tp, Adaptive(true));

        Assert.Equal(LayerHeightFlowPostProcessor.MaxScale, HeightScale(tp, 0), 5);
        Assert.Equal(LayerHeightFlowPostProcessor.MinScale, HeightScale(tp, 1), 5);
    }

    [Fact]
    public void Layers_with_no_recorded_thickness_are_left_alone()
    {
        var tp = new Toolpath();
        tp.Layers.Add(MakeLayer(0, 10f, height: 0f));

        LayerHeightFlowPostProcessor.Apply(tp, Adaptive(true));

        Assert.Equal(1f, HeightScale(tp, 0), 5);
    }

    // -- helpers ---------------------------------------------------------------

    private static SliceSettings Adaptive(bool on) => new()
    {
        AdaptiveLayerHeight = on,
        LayerHeight         = Nominal,
    };

    private static ToolpathLayer MakeLayer(int index, float z, float height)
    {
        var layer = new ToolpathLayer(index, z) { Height = height, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, z), new Vector3(50, 0, z), MoveKind.Extrude));
        return layer;
    }

    private static float HeightScale(Toolpath tp, int layerIndex)
        => tp.Layers[layerIndex].Moves[0].HeightScale;
}
