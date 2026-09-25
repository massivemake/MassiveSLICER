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
    public void Does_nothing_when_no_rule_varies_layer_thickness()
    {
        var tp = new Toolpath();
        tp.Layers.Add(MakeLayer(0, 10f, height: 1.5f));

        LayerHeightFlowPostProcessor.Apply(tp, Adaptive(false));

        Assert.Equal(1f, HeightScale(tp, 0), 5);
    }

    /// <summary>
    /// Support-driven thinning moves slice planes on its own, with adaptive layer height OFF.
    /// This pass used to be gated on the adaptive flag alone, so it never ran for that case and
    /// every thinned layer was handed a full nominal layer's worth of material.
    ///
    /// Measured live on the Anaconda head before the fix: 205 of 561 layers thinned to 2 mm,
    /// all of them still at flow x1 — 1.5x over-extrusion on 37 % of the part.
    /// </summary>
    [Fact]
    public void Support_driven_thinning_gets_flow_corrected_with_adaptive_off()
    {
        var tp = new Toolpath();
        tp.Layers.Add(MakeLayer(0, 10f, height: 3.0f));
        tp.Layers.Add(MakeLayer(1, 12f, height: 2.0f));   // thinned for overlap, not for finish

        LayerHeightFlowPostProcessor.Apply(tp, SupportDrivenOnly());

        Assert.Equal(1.000f, HeightScale(tp, 0), 3);
        Assert.Equal(0.667f, HeightScale(tp, 1), 3);      // 2 / 3, not 1.0
    }

    /// <summary>
    /// The number the exporter actually writes for that case. Without this the fix could be
    /// correct in the toolpath and still not reach the machine.
    /// </summary>
    [Fact]
    public void Exported_rpm_drops_for_a_support_thinned_layer_with_adaptive_off()
    {
        var tp = new Toolpath();
        tp.Layers.Add(MakeLayer(0, 10f, height: 3.0f));
        tp.Layers.Add(MakeLayer(1, 12f, height: 2.0f));

        var krl = new KrlExportSettings
        {
            ProgramName   = "T",
            BeadWidthMm   = 6f,
            LayerHeightMm = 3f,
            PrintSpeedMps = 0.06f,
            FlowRate      = 0.5862f,
        };

        // Before: the thinned layer demands the same RPM as a full one — the over-extrusion.
        Assert.Equal(37.986f, ToolpathRpm.MovePercent(tp.Layers[1].Moves[0], krl), 2);

        LayerHeightFlowPostProcessor.Apply(tp, SupportDrivenOnly());

        Assert.Equal(37.986f, ToolpathRpm.MovePercent(tp.Layers[0].Moves[0], krl), 2);
        Assert.Equal(25.324f, ToolpathRpm.MovePercent(tp.Layers[1].Moves[0], krl), 2);
    }

    /// <summary>Both rules on is the same correction — thickness is thickness, whoever chose it.</summary>
    [Fact]
    public void Both_rules_on_corrects_flow_the_same_way()
    {
        var tp = new Toolpath();
        tp.Layers.Add(MakeLayer(0, 10f, height: 2.0f));

        LayerHeightFlowPostProcessor.Apply(tp, new SliceSettings
        {
            AdaptiveLayerHeight      = true,
            SupportDrivenLayerHeight = true,
            LayerHeight              = Nominal,
        });

        Assert.Equal(0.667f, HeightScale(tp, 0), 3);
    }

    /// <summary>
    /// Guards the reason the gate is one named property rather than a list of flags: anything
    /// that varies thickness must report it here, or the flow pass silently skips that case.
    /// </summary>
    [Fact]
    public void Varies_layer_thickness_covers_every_rule_that_moves_a_slice_plane()
    {
        Assert.False(new SliceSettings().VariesLayerThickness);
        Assert.True(new SliceSettings { AdaptiveLayerHeight      = true }.VariesLayerThickness);
        Assert.True(new SliceSettings { SupportDrivenLayerHeight = true }.VariesLayerThickness);
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

    /// <summary>The standalone case: thickness varies, but the adaptive flag is off.</summary>
    private static SliceSettings SupportDrivenOnly() => new()
    {
        AdaptiveLayerHeight      = false,
        SupportDrivenLayerHeight = true,
        LayerHeight              = Nominal,
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
