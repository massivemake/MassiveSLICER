using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

public class SupportBridgeEstimateTest
{
    private const float LayerH = 3f;
    private const float Bead = 6f;
    private const float Deg = 30f;

    private static Toolpath TwoLayerOffset(float gapMm)
    {
        // Layer 0: bead along X at Y=0. Layer 1: same but offset in +Y by gapMm.
        var tp = new Toolpath();
        var l0 = new ToolpathLayer(0, 0f) { Height = LayerH };
        l0.Moves.Add(new ToolpathMove(new Vector3(0, 0, 0), new Vector3(40, 0, 0), MoveKind.Extrude));
        var l1 = new ToolpathLayer(1, LayerH) { Height = LayerH };
        l1.Moves.Add(new ToolpathMove(
            new Vector3(0, gapMm, LayerH), new Vector3(40, gapMm, LayerH), MoveKind.Extrude));
        tp.Layers.Add(l0);
        tp.Layers.Add(l1);
        return tp;
    }

    private static float MaxStep(float deg = Deg) =>
        MathF.Min(LayerH * MathF.Tan(deg * MathF.PI / 180f), 0.5f * Bead);

    [Fact]
    public void SmallGap_AlreadySupportedWithinHalfBead()
    {
        // gap 2 mm < 0.5*bead (3 mm) → already supported (1 layer look-down, thr = half bead).
        var tp = TwoLayerOffset(2f);
        var r = SupportBridgeEstimate.Compute(tp, [(1, 0, 1)], LayerH, Bead, Deg);
        Assert.True(r.SampleCount > 0);
        Assert.True(r.AlreadySupported, r.Summary);
        Assert.True(r.LayersRequired <= 1);
    }

    [Fact]
    public void LargeGap_NeedsMultipleLayersAt30Deg()
    {
        // MaxStep @ 30° capped to 0.5*bead = 3 mm.
        // gap 9 mm → need ceil(9/3) = 3 layers of growth — but we only have 1 layer below,
        // so it should report reaches bed / layers = layerIndex+1 = 2.
        float gap = 9f;
        var tp = TwoLayerOffset(gap);
        var r = SupportBridgeEstimate.Compute(tp, [(1, 0, 1)], LayerH, Bead, Deg);
        Assert.True(r.MaxGapMm >= gap * 0.9f, $"gap={r.MaxGapMm}");
        // With only one layer below and gap > maxStep, need bed foundation.
        Assert.True(r.LayersRequired >= 2, r.Summary);
        Assert.True(r.ReachesBed, r.Summary);
    }

    [Fact]
    public void GapWithinOneMaxStep_NeedsOneLayer()
    {
        float step = MaxStep();
        // gap slightly under MaxStep but above half-bead so not "already" via half-bead only.
        // half bead = 3, MaxStep = 3 at 30° with cap — use uncapped mentally:
        // Actually cap makes maxStep = 3 = half bead. Use smaller bead to separate.
        float bead = 10f;
        float maxStep = MathF.Min(LayerH * MathF.Tan(Deg * MathF.PI / 180f), 0.5f * bead); // min(1.73, 5)=1.73
        float gap = maxStep * 0.9f; // within 1 step, above half-bead? half bead=5, gap~1.56 — already supported by half bead thr when k=1 uses max(reach, halfBead)=5.

        // Build stack where half-bead is small: bead=4, half=2, maxStep=min(1.73,2)=1.73
        bead = 4f;
        maxStep = MathF.Min(LayerH * MathF.Tan(Deg * MathF.PI / 180f), 0.5f * bead);
        gap = maxStep * 0.85f; // ~1.47, half bead = 2 → thr = max(1.47, 2) = 2 → still already supported!

        // Need gap between maxStep and halfBead impossible when maxStep <= halfBead.
        // So with Formbound cap, k=1 thr is always at least half bead = maxStep when equal.
        // "Needs 1 layer" with alreadySupported false: need multi-layer stack where solid is 2 layers down.
        var tp = new Toolpath();
        var l0 = new ToolpathLayer(0, 0f) { Height = LayerH };
        l0.Moves.Add(new ToolpathMove(new Vector3(0, 0, 0), new Vector3(40, 0, 0), MoveKind.Extrude));
        // layer 1 empty of nearby solid
        var l1 = new ToolpathLayer(1, LayerH) { Height = LayerH };
        l1.Moves.Add(new ToolpathMove(new Vector3(200, 200, LayerH), new Vector3(210, 200, LayerH), MoveKind.Extrude));
        // layer 2 tip above layer 0 solid with gap = 2*maxStep*0.9
        float tipGap = maxStep * 1.8f;
        var l2 = new ToolpathLayer(2, LayerH * 2) { Height = LayerH };
        l2.Moves.Add(new ToolpathMove(
            new Vector3(0, tipGap, LayerH * 2), new Vector3(40, tipGap, LayerH * 2), MoveKind.Extrude));
        tp.Layers.Add(l0);
        tp.Layers.Add(l1);
        tp.Layers.Add(l2);

        var r = SupportBridgeEstimate.Compute(tp, [(2, 0, 1)], LayerH, bead, Deg);
        Assert.Equal(2, r.LayersRequired);
        Assert.False(r.ReachesBed);
        Assert.False(r.AlreadySupported);
    }

    [Fact]
    public void EmptySelection_ReturnsNoSelection()
    {
        var tp = TwoLayerOffset(5f);
        var r = SupportBridgeEstimate.Compute(tp, [], LayerH, Bead, Deg);
        Assert.Equal(0, r.LayersRequired);
        Assert.Contains("No selection", r.Summary);
    }

    [Fact]
    public void Tree_ToBedFoundation_IgnoresNearbySolidPlane()
    {
        // Formbound would land on layer 0 after 2 steps (gap within cone).
        // Tree must still report full bed foundation = layerIndex + 1.
        float bead = 4f;
        float maxStep = MathF.Min(LayerH * MathF.Tan(Deg * MathF.PI / 180f), 0.5f * bead);
        float tipGap = maxStep * 1.8f; // lands at k=2 for Formbound

        var tp = new Toolpath();
        var l0 = new ToolpathLayer(0, 0f) { Height = LayerH };
        l0.Moves.Add(new ToolpathMove(new Vector3(0, 0, 0), new Vector3(40, 0, 0), MoveKind.Extrude));
        var l1 = new ToolpathLayer(1, LayerH) { Height = LayerH };
        l1.Moves.Add(new ToolpathMove(new Vector3(200, 200, LayerH), new Vector3(210, 200, LayerH), MoveKind.Extrude));
        var l2 = new ToolpathLayer(2, LayerH * 2) { Height = LayerH };
        l2.Moves.Add(new ToolpathMove(
            new Vector3(0, tipGap, LayerH * 2), new Vector3(40, tipGap, LayerH * 2), MoveKind.Extrude));
        tp.Layers.Add(l0);
        tp.Layers.Add(l1);
        tp.Layers.Add(l2);

        var formbound = SupportBridgeEstimate.Compute(tp, [(2, 0, 1)], LayerH, bead, Deg,
            toBedFoundation: false);
        Assert.Equal(2, formbound.LayersRequired);
        Assert.False(formbound.ReachesBed);

        var tree = SupportBridgeEstimate.Compute(tp, [(2, 0, 1)], LayerH, bead, Deg,
            toBedFoundation: true);
        Assert.Equal(3, tree.LayersRequired); // layerIndex+1
        Assert.True(tree.ReachesBed);
        Assert.Contains("To bed", tree.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tree_ElevatedMultiplanar_UsesTipZOverLayerIndex()
    {
        // Multiplanar stack whose first plane is already at Z=30 (not bed).
        // Selection on layer index 2 at Z=36 → byIndex=3, byZ=ceil(36/3)=12 → 12 layers.
        float bead = 6f;
        var tp = new Toolpath();
        for (int i = 0; i < 3; i++)
        {
            float z = 30f + i * LayerH;
            var layer = new ToolpathLayer(i, z) { Height = LayerH };
            // Solid near origin on each plane — Formbound would land immediately.
            layer.Moves.Add(new ToolpathMove(
                new Vector3(0, 0, z), new Vector3(40, 0, z), MoveKind.Extrude));
            tp.Layers.Add(layer);
        }
        // Tip selection offset so Formbound would still find solid within a few steps.
        float tipZ = 30f + 2 * LayerH; // 36
        tp.Layers[2].Moves.Clear();
        tp.Layers[2].Moves.Add(new ToolpathMove(
            new Vector3(0, 2f, tipZ), new Vector3(40, 2f, tipZ), MoveKind.Extrude));

        var tree = SupportBridgeEstimate.Compute(tp, [(2, 0, 1)], LayerH, bead, Deg,
            toBedFoundation: true);
        Assert.Equal(12, tree.LayersRequired); // ceil(36/3)
        Assert.True(tree.ReachesBed);
        Assert.Contains("To bed", tree.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.True(tree.HeightMm >= tipZ - 0.1f, $"height={tree.HeightMm}");
    }

    [Fact]
    public void Formbound_ToBed_SummarySaysToBedEvenWhenNotTopLayer()
    {
        // Selection mid-stack with no solid below within cone → bed foundation,
        // even when toolpath has more layers above the selection.
        var tp = new Toolpath();
        for (int i = 0; i < 5; i++)
        {
            float z = i * LayerH;
            var layer = new ToolpathLayer(i, z) { Height = LayerH };
            // Far-away solid only — never within cone of tip at origin.
            layer.Moves.Add(new ToolpathMove(
                new Vector3(500, 500, z), new Vector3(540, 500, z), MoveKind.Extrude));
            tp.Layers.Add(layer);
        }
        // Tip at layer 2, origin XY — nothing nearby below.
        tp.Layers[2].Moves.Clear();
        tp.Layers[2].Moves.Add(new ToolpathMove(
            new Vector3(0, 0, LayerH * 2), new Vector3(40, 0, LayerH * 2), MoveKind.Extrude));

        var r = SupportBridgeEstimate.Compute(tp, [(2, 0, 1)], LayerH, Bead, Deg);
        Assert.Equal(3, r.LayersRequired);
        Assert.True(r.ReachesBed);
        Assert.Contains("To bed", r.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
