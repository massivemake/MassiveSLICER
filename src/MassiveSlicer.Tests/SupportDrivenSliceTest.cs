using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

/// <summary>
/// Support-driven layer height through the real slicer, not synthetic contours: the hook in
/// <see cref="PlanarSlicer.Slice"/> must thin an overhang, leave a vertical wall alone, and hand
/// the thinned layers less material with Adaptive layer height OFF — the case the flow gate
/// used to miss.
///
/// Asserts only on the returned toolpath. <see cref="SupportDrivenLayerHeights.LastDecisions"/>
/// is a shared static that any other class's slice can reset while this one runs.
/// </summary>
public class SupportDrivenSliceTest
{
    private const float Nominal = 3f;

    /// <summary>
    /// A hexahedron whose square cross-section grows by <paramref name="lean"/> mm per mm of Z —
    /// 1 is a 45-degree overhang on every side, 0 a plain box.
    /// </summary>
    private static Vector3[] Flare(float half, float height, float lean)
    {
        float top = half + lean * height;
        Vector3 V(float x, float y, float z) => new(x, y, z);
        var c = new[]
        {
            V(-half, -half, 0f), V(half, -half, 0f), V(half, half, 0f), V(-half, half, 0f),
            V(-top,  -top,  height), V(top,  -top,  height), V(top,  top,  height), V(-top,  top,  height),
        };
        int[] idx =
        [
            0,2,1, 0,3,2,   // bottom
            4,5,6, 4,6,7,   // top
            0,1,5, 0,5,4,   // -Y
            1,2,6, 1,6,5,   // +X
            2,3,7, 2,7,6,   // +Y
            3,0,4, 3,4,7,   // -X
        ];
        return [.. idx.Select(i => c[i])];
    }

    private static SliceSettings Settings(bool supportDriven) => new()
    {
        LayerHeight              = Nominal,
        FirstLayerHeight         = Nominal,
        MinLayerHeight           = 1f,
        BeadWidth                = 6f,
        InfillPattern            = InfillPattern.None,
        AdaptiveLayerHeight      = false,
        SupportDrivenLayerHeight = supportDriven,
    };

    [Fact]
    public void A_45_degree_overhang_is_thinned_and_its_flow_follows()
    {
        var tp = PlanarSlicer.Slice([Flare(100f, 60f, 1f)], Settings(supportDriven: true));

        // 45 degrees steps 3 mm sideways per 3 mm layer; the 60 % target on a 6 mm bead allows
        // 2.4 mm, so every layer above the first lands on 2.4 mm, not 3.
        var thin = tp.Layers.Skip(1).Where(l => l.Height > 0f && l.Height < Nominal - 0.1f).ToList();
        Assert.True(thin.Count > 10, $"expected most layers thinned, got {thin.Count} of {tp.Layers.Count}");
        Assert.All(thin, l => Assert.InRange(l.Height, 2.2f, 2.6f));

        // The flow gate: with Adaptive OFF these must still get less material, not a full 3 mm.
        Assert.All(thin, l => Assert.All(
            l.Moves.Where(m => m.Kind == MoveKind.Extrude),
            m => Assert.Equal(l.Height / Nominal, m.HeightScale, 2)));
    }

    [Fact]
    public void A_vertical_wall_is_sliced_exactly_as_with_the_feature_off()
    {
        var on  = PlanarSlicer.Slice([Flare(100f, 60f, 0f)], Settings(supportDriven: true));
        var off = PlanarSlicer.Slice([Flare(100f, 60f, 0f)], Settings(supportDriven: false));

        Assert.Equal(off.Layers.Count, on.Layers.Count);
        for (int i = 0; i < on.Layers.Count; i++)
            Assert.Equal(off.Layers[i].Z, on.Layers[i].Z, 4);
    }

    [Fact]
    public void Feature_off_leaves_an_overhang_at_nominal_thickness()
    {
        var tp = PlanarSlicer.Slice([Flare(100f, 60f, 1f)], Settings(supportDriven: false));

        Assert.All(tp.Layers.Skip(1).Where(l => l.Height > 0f),
            l => Assert.Equal(Nominal, l.Height, 3));
    }
}
