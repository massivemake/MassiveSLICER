using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

/// <summary>
/// Zig-zag seam is a single-skin mode: closed thin-wall loops are cut to their longest
/// open face. Field failure (Scene 08 Panel 04, the Drone): on ENCLOSED models the same
/// cut amputates the perimeter and — with same-layer travel off — KeepLongestOpenFaceOnly
/// kept ONE island per layer, deleting the rest of the model. The guard keeps enclosed
/// rings (mean width ≫ bead) closed and reports a warning.
/// </summary>
public class ZigZagEnclosedGuardTest
{
    private const float Bead = 8f;

    /// <summary>Closed rectangle ring (CCW), first vertex repeated at the end.</summary>
    private static List<Vector2> Rect(float x0, float y0, float w, float h)
        =>
        [
            new(x0, y0), new(x0 + w, y0), new(x0 + w, y0 + h), new(x0, y0 + h), new(x0, y0),
        ];

    [Fact]
    public void Thin_wall_loop_is_still_cut_to_an_open_skin()
    {
        // 600×6mm loop ≈ a single-wall panel outline: mean width well under one bead.
        var contours = new List<List<Vector2>> { Rect(0, 0, 600, 6) };
        var closed   = new List<bool> { true };
        PlanarSlicer.ZigZagEnclosedKeptCount = 0;

        PlanarSlicer.ExtractSingleSkinOpenFaces(contours, closed, Bead);

        Assert.False(closed[0]);
        Assert.Equal(0, PlanarSlicer.ZigZagEnclosedKeptCount);
    }

    [Fact]
    public void Enclosed_solid_ring_stays_closed_and_is_counted()
    {
        // 600×300mm perimeter of an enclosed solid: mean width ≈ 200mm ≫ bead.
        // Elongated enough (aspect 2 < 2.5 would keep it via ring-like — use 3:1).
        var contours = new List<List<Vector2>> { Rect(0, 0, 900, 300) };
        var closed   = new List<bool> { true };
        PlanarSlicer.ZigZagEnclosedKeptCount = 0;

        PlanarSlicer.ExtractSingleSkinOpenFaces(contours, closed, Bead);

        Assert.True(closed[0]);
        Assert.Equal(1, PlanarSlicer.ZigZagEnclosedKeptCount);
    }

    [Fact]
    public void KeepLongest_prunes_when_all_faces_are_open()
    {
        // Original single-panel behavior preserved: two open skins → keep the longest.
        var contours = new List<List<Vector2>>
        {
            new() { new(0, 0), new(100, 0) },
            new() { new(0, 50), new(500, 50) },
        };
        var closed = new List<bool> { false, false };

        PlanarSlicer.KeepLongestOpenFaceOnly(contours, closed);

        Assert.Single(contours);
        Assert.Equal(500f, Vector2.Distance(contours[0][0], contours[0][^1]));
    }

    [Fact]
    public void KeepLongest_never_deletes_closed_islands()
    {
        // The Panel 04 failure: enclosed ring + open skin on one layer. Pruning would
        // silently delete model geometry, so nothing may be dropped.
        var contours = new List<List<Vector2>>
        {
            Rect(0, 0, 900, 300),
            new() { new(0, 500), new(2000, 500) },
        };
        var closed = new List<bool> { true, false };

        PlanarSlicer.KeepLongestOpenFaceOnly(contours, closed);

        Assert.Equal(2, contours.Count);
    }

    [Fact]
    public void Warning_is_attached_and_counter_resets()
    {
        var tp = new Toolpath();
        PlanarSlicer.ZigZagEnclosedKeptCount = 3;

        PlanarSlicer.AttachZigZagWarning(tp);

        Assert.Single(tp.Warnings);
        Assert.Contains("single-wall", tp.Warnings[0]);
        Assert.Contains("Normal", tp.Warnings[0]);
        Assert.Equal(0, PlanarSlicer.ZigZagEnclosedKeptCount);

        // No-op when the guard never fired.
        var tp2 = new Toolpath();
        PlanarSlicer.AttachZigZagWarning(tp2);
        Assert.Empty(tp2.Warnings);
    }

    [Fact]
    public void Average_ring_width_matches_geometry()
    {
        // 600×6 rectangle: 2A/P = 2·3600/1212 ≈ 5.94 (≈ the 6mm wall thickness).
        Assert.Equal(5.94f, PlanarSlicer.AverageRingWidth(Rect(0, 0, 600, 6)), 1);
        // 900×300 rectangle: 2A/P = 2·270000/2400 = 225.
        Assert.Equal(225f, PlanarSlicer.AverageRingWidth(Rect(0, 0, 900, 300)), 0);
    }
}
