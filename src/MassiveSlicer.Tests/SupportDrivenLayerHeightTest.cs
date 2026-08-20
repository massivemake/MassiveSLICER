using System.Numerics;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

/// <summary>
/// Support-driven layer height chooses thickness from the MEASURED sideways step between one
/// layer's boundary and the next, rather than from triangle normals. It may only ever make a layer
/// thinner than the finish criterion already chose.
///
/// The circularity — thickness decides the next contour, which is what you must measure to pick
/// the thickness — is broken by the step being LINEAR in thickness: measure a trial at h, get
/// offset s, and the thickness that hits target s* is h × s*/s directly.
///
/// These feed synthetic contours rather than meshes, so the geometry is exact and the arithmetic
/// is checkable by hand.
/// </summary>
/// <summary>
/// Joins the existing "AdaptiveLayerHeights" collection because it shares the slicer's
/// diagnostic statics (AdaptiveLayerHeights.LastReasons,
/// SupportDrivenLayerHeights.LastDecisions, ProximityFlowPostProcessor.LastRuns). xUnit runs
/// test CLASSES in parallel, so without a shared collection these clobber each other: a test
/// asserting on what a slice published would fail whenever another class sliced at the same
/// moment. It passed when filtered and failed in the full suite -- a flaky test, not a bug.
/// </summary>
[Collection("AdaptiveLayerHeights")]
public class SupportDrivenLayerHeightTest
{
    private const float MinH = 2f, MaxH = 3f, Bead = 6f;
    private const float Target = 2.4f;      // 60 % overlap on a 6 mm bead
    private const float Tol    = 12f;       // 2 x bead width

    /// <summary>A square wall whose half-size grows by <paramref name="lean"/> mm per mm of Z.</summary>
    private static Func<float, IReadOnlyList<IReadOnlyList<Vector2>>> Wall(float lean)
        => z =>
        {
            float r = 200f + lean * z;
            return new[] { Ring(r) };
        };

    private static IReadOnlyList<Vector2> Ring(float r) =>
    [
        new(-r, -r), new(r, -r), new(r, r), new(-r, r), new(-r, -r),
    ];

    private static float[] Ladder(int n, float h0, float h)
    {
        var z = new float[n];
        z[0] = h0;
        for (int i = 1; i < n; i++) z[i] = z[i - 1] + h;
        return z;
    }

    private static float[] Heights(float[] z)
    {
        var h = new float[Math.Max(0, z.Length - 1)];
        for (int i = 1; i < z.Length; i++) h[i - 1] = z[i] - z[i - 1];
        return h;
    }

    [Fact]
    public void A_vertical_wall_is_never_thinned()
    {
        var z = SupportDrivenLayerHeights.Refine(
            Ladder(11, 3f, MaxH), 30f, Wall(0f), Target, Tol, MinH, MaxH, Bead);

        Assert.All(Heights(z), h => Assert.Equal(MaxH, h, 3));
        Assert.All(SupportDrivenLayerHeights.LastDecisions, d => Assert.False(d.Thinned));
    }

    /// <summary>
    /// A 45-degree lean steps sideways 1 mm per mm of Z, so a 3 mm layer would step 3 mm — past the
    /// 2.4 mm target. The linear rescale should land exactly on 2.4 mm, in one correction.
    /// </summary>
    [Fact]
    public void A_leaning_wall_is_thinned_to_exactly_the_thickness_that_hits_target()
    {
        var z = SupportDrivenLayerHeights.Refine(
            Ladder(11, 3f, MaxH), 30f, Wall(1f), Target, Tol, MinH, MaxH, Bead);

        var h = Heights(z);
        Assert.NotEmpty(h);
        Assert.All(h, v => Assert.Equal(Target, v, 2));      // 3 x 2.4/3.0 = 2.4
        Assert.Contains(SupportDrivenLayerHeights.LastDecisions, d => d.Thinned);
        Assert.All(SupportDrivenLayerHeights.LastDecisions, d => Assert.False(d.Unfixable));
    }

    /// <summary>
    /// A 2 mm-per-mm lean needs 1.2 mm to hit target, below the 2 mm floor. It must clamp to the
    /// floor AND be flagged — a thickness rule cannot fix geometry that is genuinely unsupported,
    /// and silently accepting it is the failure mode worth avoiding.
    /// </summary>
    [Fact]
    public void Geometry_too_steep_for_the_floor_clamps_and_is_flagged_unfixable()
    {
        var z = SupportDrivenLayerHeights.Refine(
            Ladder(11, 3f, MaxH), 30f, Wall(2f), Target, Tol, MinH, MaxH, Bead);

        Assert.All(Heights(z), v => Assert.Equal(MinH, v, 3));
        Assert.All(SupportDrivenLayerHeights.LastDecisions, d => Assert.True(d.Unfixable));
        var first = SupportDrivenLayerHeights.LastDecisions[0];
        Assert.InRange(first.NeededThicknessMm, 1.1f, 1.3f);   // 3 x 2.4/6.0 = 1.2
    }

    /// <summary>
    /// The bridging tolerance. A short off-target stretch is something a bead spans, so it must not
    /// thin a whole layer; the same stretch made long must. Same geometry, only the tolerance moves.
    /// </summary>
    [Fact]
    public void A_short_off_target_stretch_is_bridged_but_a_long_one_thins_the_layer()
    {
        // A 4 mm square notch stepping 4 mm outward. Only its OUTER face is past the 2.4 mm
        // target — the two side segments have midpoints 2 mm off, which is within target — so the
        // measured run is 4 mm. (Getting this wrong is easy: a deeper notch puts its side segments
        // past target too and the run becomes the whole excursion, not just the outer face.)
        Func<float, IReadOnlyList<IReadOnlyList<Vector2>>> notched = z =>
        {
            const float r = 200f;
            bool bump = z > 3.5f;                     // first layer clean, everything above notched
            var pts = new List<Vector2> { new(-r, -r), new(r, -r) };
            if (bump)
            {
                pts.Add(new(r,       0f));
                pts.Add(new(r + 4f,  0f));            // side: midpoint 2 mm off, within target
                pts.Add(new(r + 4f,  4f));            // outer face: 4 mm off, 4 mm of run
                pts.Add(new(r,       4f));            // side: within target again
            }
            pts.Add(new(r, r)); pts.Add(new(-r, r)); pts.Add(new(-r, -r));
            return new[] { (IReadOnlyList<Vector2>)pts };
        };

        var bridged = SupportDrivenLayerHeights.Refine(
            Ladder(6, 3f, MaxH), 15f, notched, Target, bridgeToleranceMm: 12f,
            minLayerHeight: MinH, maxLayerHeight: MaxH, searchCellMm: Bead);
        Assert.All(Heights(bridged), v => Assert.Equal(MaxH, v, 3));

        var thinned = SupportDrivenLayerHeights.Refine(
            Ladder(6, 3f, MaxH), 15f, notched, Target, bridgeToleranceMm: 2f,
            minLayerHeight: MinH, maxLayerHeight: MaxH, searchCellMm: Bead);
        Assert.Contains(SupportDrivenLayerHeights.LastDecisions, d => d.Thinned);
        Assert.True(Heights(thinned)[0] < MaxH - 1e-3f,
            "a 4 mm stretch past a 2 mm tolerance must thin the layer");
    }

    [Fact]
    public void It_can_only_thin_never_thicken()
    {
        // Ladder already at the floor; a vertical wall gives it no reason to change anything.
        var z = SupportDrivenLayerHeights.Refine(
            Ladder(11, 2f, MinH), 25f, Wall(0f), Target, Tol, MinH, MaxH, Bead);
        Assert.All(Heights(z), v => Assert.Equal(MinH, v, 3));
    }

    [Fact]
    public void Degenerate_input_is_returned_untouched()
    {
        var one = new[] { 3f };
        Assert.Same(one, SupportDrivenLayerHeights.Refine(
            one, 30f, Wall(1f), Target, Tol, MinH, MaxH, Bead));

        var ladder = Ladder(5, 3f, MaxH);
        Assert.Same(ladder, SupportDrivenLayerHeights.Refine(
            ladder, 30f, Wall(1f), targetOffsetMm: 0f, Tol, MinH, MaxH, Bead));
    }
}
