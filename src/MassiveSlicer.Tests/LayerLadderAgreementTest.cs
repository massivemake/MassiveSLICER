using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

/// <summary>
/// The layer PREVIEW and the actual SLICE must place slice planes in the same places.
///
/// They did not. The preview built its own ladder: it called
/// <see cref="AdaptiveLayerHeights.ComputeZPositions"/> without <c>minFaceAreaMm2</c>, and applied
/// neither support-driven thinning nor the slew cap. So the bands drawn over the mesh were a
/// different calculation from the toolpath the user was about to get — and the min-face-area gate,
/// the setting most likely to be under investigation, was the one the picture ignored.
///
/// The fix is structural: <see cref="PlanarSlicer.BuildLayerLadder"/> is the single place that
/// decides where planes go, and both callers use it. These tests exist to keep it that way — if
/// someone adds a fourth thickness rule inside <c>Slice</c> instead of inside the builder, the
/// first test here fails.
/// </summary>
public class LayerLadderAgreementTest
{
    /// <summary>A tapering box: wide at the bottom, narrowing with height, so the thickness
    /// rules have something real to react to.</summary>
    private static List<Vector3[]> Wedge(float height = 60f)
    {
        var v = new List<Vector3>();
        const int steps = 24;
        for (int i = 0; i < steps; i++)
        {
            float z0 = height * i / steps, z1 = height * (i + 1) / steps;
            float r0 = 40f - 25f * i / steps, r1 = 40f - 25f * (i + 1) / steps;
            foreach (var (sx, sy) in new[] { (1, 0), (0, 1), (-1, 0), (0, -1) })
            {
                var a = new Vector3(sx * r0, sy * r0, z0);
                var b = new Vector3(sy * r0, -sx * r0, z0);
                var c = new Vector3(sx * r1, sy * r1, z1);
                var d = new Vector3(sy * r1, -sx * r1, z1);
                v.AddRange([a, b, c]);
                v.AddRange([b, d, c]);
            }
        }
        v.AddRange([new(-40, -40, 0), new(40, -40, 0), new(40, 40, 0)]);
        v.AddRange([new(-40, -40, 0), new(40, 40, 0), new(-40, 40, 0)]);
        return [v.ToArray()];
    }

    /// <summary>
    /// The wedge plus a scatter of tiny NEAR-HORIZONTAL facets, each well under one bead footprint
    /// (bead 8 x min layer 2 = 16 mm2). A shallow face demands a thin layer from the stairstep
    /// criterion, so ungated these pin layers thin; gated they are ignored. Without slivers the
    /// gate has nothing to suppress and a test of it passes for the wrong reason.
    /// </summary>
    private static List<Vector3[]> WedgeWithSlivers()
    {
        var v = new List<Vector3>(Wedge()[0]);
        for (int k = 0; k < 8; k++)
        {
            float z = 12f + k * 4.3f;              // spread up the part, off the layer grid
            float x = 6f + k;
            // ~1.5 mm2, tilted a fraction of a degree off horizontal
            v.AddRange([
                new Vector3(x,        0f,   z),
                new Vector3(x + 1.7f, 0f,   z + 0.02f),
                new Vector3(x,        1.7f, z),
            ]);
        }
        return [v.ToArray()];
    }

    private static SliceSettings Settings(
        bool adaptive = false,
        bool supportDriven = false,
        float slewMm = 0f,
        float minFaceArea = 0f,
        float quality = 0.4f,
        float minLayerHeight = 2f) => new()
    {
        LayerHeight              = 4f,
        FirstLayerHeight         = 4f,
        MinLayerHeight           = minLayerHeight,
        BeadWidth                = 8f,
        AdaptiveQuality          = quality,
        AdaptiveLayerHeight      = adaptive,
        SupportDrivenLayerHeight = supportDriven,
        MaxLayerHeightChangeMm   = slewMm,
        AdaptiveMinFaceAreaMm2   = minFaceArea,
    };

    /// <summary>
    /// ⭐ The one that matters. Whatever combination of rules is on, the ladder the preview draws
    /// must be the ladder the slicer used — compared against the Z of every layer the slice
    /// actually produced, not against a re-run of the builder.
    /// </summary>
    [Theory]
    [InlineData(false, false, 0f)]      // uniform
    [InlineData(true,  false, 0f)]      // finish only
    [InlineData(false, true,  0f)]      // adhesion only
    [InlineData(true,  true,  0f)]      // both
    [InlineData(true,  true,  0.2f)]    // both + slew
    [InlineData(false, true,  0.2f)]    // adhesion + slew
    public void The_preview_ladder_matches_the_layers_the_slice_produced(
        bool adaptive, bool supportDriven, float slewMm)
    {
        var meshes = Wedge();
        var settings = Settings(adaptive, supportDriven, slewMm);

        float zMin = float.MaxValue, zMax = float.MinValue;
        foreach (var t in meshes)
            foreach (var v in t) { zMin = MathF.Min(zMin, v.Z); zMax = MathF.Max(zMax, v.Z); }

        // What the preview draws.
        var preview = PlanarSlicer.BuildLayerLadder(meshes, zMin, zMax, settings, recordReasons: false);

        // What the slicer actually produced.
        var toolpath = PlanarSlicer.Slice(meshes, settings);
        Assert.True(toolpath.Layers.Count > 3,
            $"fixture produced only {toolpath.Layers.Count} layers — not enough to compare");

        // Every sliced layer's Z must appear in the preview ladder.
        foreach (var layer in toolpath.Layers)
            Assert.True(preview.Any(z => MathF.Abs(z - layer.Z) < 1e-3f),
                $"sliced layer at Z {layer.Z:0.####} is not in the preview ladder — the preview is "
              + "computing a different answer from the slice");
    }

    /// <summary>
    /// The gate must reach the builder. Passing it changes the ladder on a mesh with sub-bead
    /// facets, so a builder that dropped the argument (which is what the preview used to do) shows
    /// up as an identical ladder either way.
    /// </summary>
    [Fact]
    public void The_min_face_area_gate_actually_reaches_the_ladder()
    {
        var meshes = WedgeWithSlivers();
        float zMin = 0f, zMax = 60f;

        var withGate = PlanarSlicer.BuildLayerLadder(meshes, zMin, zMax,
            Settings(adaptive: true, minFaceArea: 0f), false);
        var noGate = PlanarSlicer.BuildLayerLadder(meshes, zMin, zMax,
            Settings(adaptive: true, minFaceArea: -1f), false);

        // -1 turns the gate off entirely; 0 derives a bead footprint. On a tapered mesh with many
        // small side facets those must not agree, or the setting is being ignored.
        Assert.True(withGate.Length != noGate.Length
                    || withGate.Zip(noGate).Any(p => MathF.Abs(p.First - p.Second) > 1e-3f),
            "gated and ungated ladders are identical — ResolvedMinFaceAreaMm2 is not reaching "
          + "ComputeZPositions");
    }

    /// <summary>
    /// Each rule must be visible in the ladder, so "both on" is never silently one rule.
    /// This is the question a user cannot currently answer from the UI.
    /// </summary>
    [Fact]
    public void Support_driven_changes_the_ladder_that_the_finish_rule_alone_produces()
    {
        var meshes = Wedge();
        float zMin = 0f, zMax = 60f;

        var finishOnly = PlanarSlicer.BuildLayerLadder(meshes, zMin, zMax,
            Settings(adaptive: true, supportDriven: false), false);
        var both = PlanarSlicer.BuildLayerLadder(meshes, zMin, zMax,
            Settings(adaptive: true, supportDriven: true), false);

        Assert.True(both.Length != finishOnly.Length
                    || both.Zip(finishOnly).Any(p => MathF.Abs(p.First - p.Second) > 1e-3f),
            "adding support-driven changed nothing on a tapered wedge — it is not running");
    }

    /// <summary>
    /// The preview must not disturb the diagnostics. It runs on every settings keystroke, and it
    /// used to overwrite the static <c>adaptive-height-debug</c> reads, so the report could
    /// describe a ladder that was not the toolpath on screen.
    /// </summary>
    [Fact]
    public void Preview_does_not_overwrite_the_diagnostics_a_real_slice_published()
    {
        var meshes = Wedge();
        var settings = Settings(adaptive: true, supportDriven: true);

        // A real slice publishes both statics.
        PlanarSlicer.Slice(meshes, settings);
        int reasonsFromSlice   = AdaptiveLayerHeights.LastReasons.Count;
        int decisionsFromSlice = SupportDrivenLayerHeights.LastDecisions.Count;
        Assert.True(reasonsFromSlice   > 0, "slice published no adaptive reasons — test is vacuous");
        Assert.True(decisionsFromSlice > 0, "slice published no support decisions — test is vacuous");

        // A preview pass at DIFFERENT settings must leave them alone.
        PlanarSlicer.BuildLayerLadder(meshes, 0f, 60f,
            Settings(adaptive: true, supportDriven: true, quality: 0.95f, minLayerHeight: 3.5f),
            recordReasons: false);

        Assert.Equal(reasonsFromSlice,   AdaptiveLayerHeights.LastReasons.Count);
        Assert.Equal(decisionsFromSlice, SupportDrivenLayerHeights.LastDecisions.Count);
    }

    /// <summary>
    /// Slicing with support-driven OFF must clear the decisions, or the report describes a run
    /// that is no longer on screen. Observed live: support-height-debug claimed "14 thinned for
    /// overlap" for a slice with the feature switched off.
    /// </summary>
    [Fact]
    public void Slicing_with_support_driven_off_clears_its_stale_decisions()
    {
        var meshes = Wedge();

        PlanarSlicer.Slice(meshes, Settings(supportDriven: true));
        Assert.True(SupportDrivenLayerHeights.LastDecisions.Count > 0, "test is vacuous");

        PlanarSlicer.Slice(meshes, Settings(supportDriven: false));
        Assert.Empty(SupportDrivenLayerHeights.LastDecisions);
    }
}
