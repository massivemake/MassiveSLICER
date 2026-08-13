using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

/// <summary>
/// Layer thickness is the minimum demand of any single triangle crossing that Z, unweighted, so
/// one sliver outvotes the whole cross-section. Measured on a real part: 277 of 281 constrained
/// layers were decided by a triangle under a tenth of the average area it beat, the deciding
/// triangles running 0.95–8 mm² against 2,500–5,000 faces crossing.
///
/// The gate's argument is physical, not statistical: a surface feature smaller than a single bead
/// cannot be reproduced by the machine, so it has no business setting thickness for everything.
///
/// The fixture is a vertical wall (two 5,000 mm² triangles, which impose no constraint) plus ONE
/// near-horizontal sliver of 0.51 mm² spanning Z 50.0–50.2 — the only thing in the scene that can
/// demand a thin layer.
/// </summary>
public class AdaptiveMinFaceAreaTest
{
    private const float MinH = 1f, MaxH = 3f, Quality = 0f;
    private const float Gate = 10f;      // well above the 0.51 mm² sliver, well below the walls

    [Fact]
    public void Ungated_a_single_sliver_pins_a_layer_to_the_floor()
    {
        var z = AdaptiveLayerHeights.ComputeZPositions(
            WallWithOneSliver(), 0f, 100f, MaxH, MinH, MaxH, Quality, minFaceAreaMm2: 0f);

        Assert.Contains(Heights(z), h => MathF.Abs(h - MinH) < 1e-3f);
        Assert.Contains(AdaptiveLayerHeights.LastReasons, r => r.SnappedToFaceBottom);
    }

    /// <summary>The point of the whole thing: gate the sliver and the wall takes full layers.</summary>
    [Fact]
    public void Gated_the_sliver_stops_deciding_and_every_layer_is_full_thickness()
    {
        var z = AdaptiveLayerHeights.ComputeZPositions(
            WallWithOneSliver(), 0f, 100f, MaxH, MinH, MaxH, Quality, minFaceAreaMm2: Gate);

        Assert.All(Heights(z), h => Assert.Equal(MaxH, h, 3));
        Assert.All(AdaptiveLayerHeights.LastReasons, r => Assert.False(r.SnappedToFaceBottom));
        Assert.Contains(AdaptiveLayerHeights.LastReasons, r => r.FacesGated > 0);
        Assert.Contains(AdaptiveLayerHeights.LastReasons, r => r.GateChangedTheOutcome);
    }

    /// <summary>
    /// The gate lives in BOTH facet passes and they catch different slivers: the first pass sees
    /// faces whose Z span straddles the layer position, the second sees faces starting inside the
    /// tentative height. This fixture puts a sliver across the FIRST layer's Z (2.5–3.5 against a
    /// layer at 3.0) so the first-pass gate is actually exercised.
    ///
    /// Added after a control test: breaking the first-pass gate alone changed no test result,
    /// because every other fixture here is caught by the second pass.
    /// </summary>
    [Fact]
    public void The_gate_applies_to_a_sliver_straddling_the_layer_position_too()
    {
        var meshes = WallOnly();
        meshes.Add([
            new Vector3(10, 10, 2.5f), new Vector3(11, 10, 2.5f), new Vector3(10, 11, 3.5f),
        ]);

        // First layer lands at 0 + firstLayerHeight = 3.0, inside the sliver's 2.5-3.5 span.
        var ungated = AdaptiveLayerHeights.ComputeZPositions(
            meshes, 0f, 40f, MaxH, MinH, MaxH, Quality, minFaceAreaMm2: 0f);
        float firstUngated = ungated[1] - ungated[0];

        var gated = AdaptiveLayerHeights.ComputeZPositions(
            meshes, 0f, 40f, MaxH, MinH, MaxH, Quality, minFaceAreaMm2: Gate);
        float firstGated = gated[1] - gated[0];

        Assert.True(firstUngated < MaxH - 1e-3f,
            $"the straddling sliver must constrain when ungated; got {firstUngated} mm");
        Assert.Equal(MaxH, firstGated, 3);
        Assert.True(AdaptiveLayerHeights.LastReasons[0].FacesGated > 0,
            "the first layer should record the straddling sliver as gated");
    }

    /// <summary>
    /// A genuinely large shallow face is real geometry, not tessellation noise, and must still
    /// force a thin layer. Without this the gate would just be "ignore shallow surfaces".
    /// </summary>
    [Fact]
    public void A_large_shallow_face_still_constrains_through_the_gate()
    {
        var meshes = WallOnly();
        // ~1,250 mm² near-horizontal face at Z 50 — two orders of magnitude over the gate.
        meshes.Add([
            new Vector3(10, 10, 50.0f), new Vector3(60, 10, 50.0f), new Vector3(10, 60, 50.4f),
        ]);

        var z = AdaptiveLayerHeights.ComputeZPositions(
            meshes, 0f, 100f, MaxH, MinH, MaxH, Quality, minFaceAreaMm2: Gate);

        Assert.Contains(Heights(z), h => h < MaxH - 1e-3f);
        var thinnest = AdaptiveLayerHeights.LastReasons.OrderBy(r => r.Height).First();
        Assert.True(thinnest.BindingArea > Gate,
            $"a real shallow face must survive the gate; bound by {thinnest.BindingArea} mm²");
    }

    /// <summary>
    /// ⚠️ The safety property. If NOTHING in a cross-section clears the gate, the gate must stand
    /// down rather than silently letting the layer jump to full thickness — a fully slivered mesh
    /// gets the old answer, not a worse one.
    /// </summary>
    [Fact]
    public void When_nothing_clears_the_gate_the_result_is_identical_to_ungated()
    {
        var slivers = SliversOnly();

        var ungated = AdaptiveLayerHeights.ComputeZPositions(
            slivers, 0f, 60f, MaxH, MinH, MaxH, Quality, minFaceAreaMm2: 0f);
        var gated = AdaptiveLayerHeights.ComputeZPositions(
            slivers, 0f, 60f, MaxH, MinH, MaxH, Quality, minFaceAreaMm2: Gate);

        Assert.Equal(ungated, gated);
        // Not vacuous: the slivers really were constraining something.
        Assert.Contains(Heights(ungated), h => h < MaxH - 1e-3f);
    }

    [Fact]
    public void Gate_defaults_to_one_bead_footprint_and_can_be_turned_off()
    {
        var s = new SliceSettings { BeadWidth = 6f, LayerHeight = 3f, MinLayerHeight = 2f };
        Assert.Equal(12f, s.ResolvedMinFaceAreaMm2, 3);          // 6 x 2

        // The floor drives it, not nominal — that's the thinnest layer a bead could sit in.
        Assert.Equal(6f, new SliceSettings
            { BeadWidth = 6f, LayerHeight = 3f, MinLayerHeight = 1f }.ResolvedMinFaceAreaMm2, 3);

        Assert.Equal(40f, new SliceSettings
            { BeadWidth = 6f, LayerHeight = 3f, MinLayerHeight = 2f,
              AdaptiveMinFaceAreaMm2 = 40f }.ResolvedMinFaceAreaMm2, 3);

        // 0 = every triangle votes, i.e. the pre-gate behaviour.
        Assert.Equal(0f, new SliceSettings
            { BeadWidth = 6f, LayerHeight = 3f, MinLayerHeight = 2f,
              AdaptiveMinFaceAreaMm2 = -1f }.ResolvedMinFaceAreaMm2, 3);
    }

    private static float[] Heights(float[] z)
    {
        var h = new float[Math.Max(0, z.Length - 1)];
        for (int i = 1; i < z.Length; i++) h[i - 1] = z[i] - z[i - 1];
        return h;
    }

    /// <summary>Two 5,000 mm² triangles forming a vertical wall — no constraint of their own.</summary>
    private static List<Vector3[]> WallOnly() =>
    [
        [
            new Vector3(0, 0, 0),   new Vector3(100, 0, 0),   new Vector3(100, 0, 100),
            new Vector3(0, 0, 0),   new Vector3(100, 0, 100), new Vector3(0, 0, 100),
        ],
    ];

    /// <summary>The wall, plus one 0.51 mm² near-horizontal sliver spanning Z 50.0–50.2.</summary>
    private static List<Vector3[]> WallWithOneSliver()
    {
        var meshes = WallOnly();
        meshes.Add([
            new Vector3(10, 10, 50.0f), new Vector3(11, 10, 50.0f), new Vector3(10, 11, 50.2f),
        ]);
        return meshes;
    }

    /// <summary>Nothing but sub-gate slivers, stepping up the Z range.</summary>
    private static List<Vector3[]> SliversOnly()
    {
        var verts = new List<Vector3>();
        for (int i = 0; i < 30; i++)
        {
            float z = 2f + i * 2f;
            verts.Add(new Vector3(10, 10, z));
            verts.Add(new Vector3(11, 10, z));
            verts.Add(new Vector3(10, 11, z + 0.2f));
        }
        return [verts.ToArray()];
    }
}
