using System.Linq;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Verifies the "Restricted" (non-Infinite) Cut behavior Jeff specified: bounded in the plane's
/// own footprint (SizeX/SizeY), but still a full through-cut wherever that footprint overlaps the
/// mesh — not a partial-depth pocket. Outside the footprint the mesh must stay whole and connected.
/// </summary>
public sealed class BoundedCutSplitterTest
{
    /// <summary>Axis-aligned box, half-extents (hx, hy, hz), centered at the origin.</summary>
    private static MeshData Box(float hx, float hy, float hz)
    {
        var p = new Vector3[]
        {
            new(-hx,-hy,-hz), new( hx,-hy,-hz), new( hx, hy,-hz), new(-hx, hy,-hz),
            new(-hx,-hy, hz), new( hx,-hy, hz), new( hx, hy, hz), new(-hx, hy, hz),
        };
        uint[] idx =
        [
            0,1,2, 0,2,3, 4,6,5, 4,7,6, 0,4,5, 0,5,1, 2,6,7, 2,7,3, 0,3,7, 0,7,4, 1,5,6, 1,6,2,
        ];
        var nrm = new Vector3[p.Length];
        for (int i = 0; i < p.Length; i++) nrm[i] = Vector3.Normalize(p[i]);
        return new MeshData(p, nrm, idx, "box");
    }

    [Fact]
    public void Footprint_covering_the_whole_cross_section_fully_severs_the_mesh_same_as_infinite()
    {
        // A long thin bar (100 x 20 x 20), cut horizontally through the middle with a rectangle
        // big enough to cover the bar's entire X/Y footprint -- should behave exactly like an
        // Infinite cut: fully separated into a top half and a bottom half.
        var bar = Box(50f, 10f, 10f);

        var result = BoundedCutSplitter.Split(
            bar, planePoint: Vector3.Zero, planeNormal: Vector3.UnitZ,
            tangentU: Vector3.UnitX, tangentV: Vector3.UnitY,
            halfSizeX: 100f, halfSizeY: 100f);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
    }

    [Fact]
    public void Footprint_narrower_than_the_bar_leaves_it_bridged_as_one_piece()
    {
        // Same bar, but the rectangle only covers a 20mm-wide slice out of the bar's 100mm
        // length -- outside that slice the bar must stay whole, so top and bottom remain
        // connected via the uncut material on both ends. Exactly Jeff's "restricted cut, so I
        // can't cut all the way through the length" scenario.
        var bar = Box(50f, 10f, 10f);

        var result = BoundedCutSplitter.Split(
            bar, planePoint: Vector3.Zero, planeNormal: Vector3.UnitZ,
            tangentU: Vector3.UnitX, tangentV: Vector3.UnitY,
            halfSizeX: 10f, halfSizeY: 100f);

        Assert.NotNull(result);
        Assert.Single(result!);
    }

    [Fact]
    public void Footprint_narrow_on_both_axes_still_leaves_a_bridge()
    {
        var bar = Box(50f, 50f, 10f);

        var result = BoundedCutSplitter.Split(
            bar, planePoint: Vector3.Zero, planeNormal: Vector3.UnitZ,
            tangentU: Vector3.UnitX, tangentV: Vector3.UnitY,
            halfSizeX: 10f, halfSizeY: 10f);

        Assert.NotNull(result);
        Assert.Single(result!);
    }

    [Fact]
    public void Footprint_wide_enough_on_both_axes_fully_severs()
    {
        var bar = Box(50f, 50f, 10f);

        var result = BoundedCutSplitter.Split(
            bar, planePoint: Vector3.Zero, planeNormal: Vector3.UnitZ,
            tangentU: Vector3.UnitX, tangentV: Vector3.UnitY,
            halfSizeX: 100f, halfSizeY: 100f);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
    }

    [Fact]
    public void Footprint_entirely_outside_the_mesh_returns_null()
    {
        var box = Box(10f, 10f, 10f);

        var result = BoundedCutSplitter.Split(
            box, planePoint: new Vector3(1000f, 0f, 0f), planeNormal: Vector3.UnitZ,
            tangentU: Vector3.UnitX, tangentV: Vector3.UnitY,
            halfSizeX: 5f, halfSizeY: 5f);

        Assert.Null(result);
    }

    [Fact]
    public void Plane_outside_the_mesh_depth_wise_returns_null_even_if_footprint_overlaps()
    {
        // Footprint (X/Y) overlaps the box, but the cut plane's height (Z=1000) is nowhere near
        // the box's actual Z extent -- nothing to cut.
        var box = Box(10f, 10f, 10f);

        var result = BoundedCutSplitter.Split(
            box, planePoint: new Vector3(0f, 0f, 1000f), planeNormal: Vector3.UnitZ,
            tangentU: Vector3.UnitX, tangentV: Vector3.UnitY,
            halfSizeX: 100f, halfSizeY: 100f);

        Assert.Null(result);
    }

    [Fact]
    public void Result_triangle_count_is_conserved_no_gaps_or_duplicated_geometry()
    {
        // A sanity check on volume/geometry integrity: cutting with a large-enough footprint
        // should produce the same total triangle count as a plain Infinite split (2 caps added,
        // no slab-wall geometry actually removed since the box fits entirely inside the rectangle).
        var box = Box(10f, 10f, 10f);
        var infinite = PlanarMeshSplitter.Split(box, Vector3.Zero, Vector3.UnitZ);
        int infiniteTriCount = TriCount(infinite.Positive) + TriCount(infinite.Negative);

        var bounded = BoundedCutSplitter.Split(
            box, Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, halfSizeX: 100f, halfSizeY: 100f);

        Assert.NotNull(bounded);
        Assert.Equal(infiniteTriCount, bounded!.Sum(TriCount));
    }

    private static int TriCount(MeshData m) => (m.Indices?.Length ?? m.Positions.Length) / 3;

    [Fact]
    public void A_single_cut_sides_own_cap_face_is_not_a_phantom_extra_island()
    {
        // CapLoop nudges every cap vertex by a tiny bias along the face normal to avoid
        // z-fighting with the side wall it caps -- purely cosmetic, but if MeshIslands' weld
        // tolerance is tighter than that bias, the cap would register as disconnected from the
        // very side wall it's capping, turning ONE ordinary side of ONE ordinary cut into 2
        // "islands" (walls + a phantom floating cap). This must never happen, on either side.
        var box = Box(10f, 10f, 10f);
        var cut = PlanarMeshSplitter.Split(box, Vector3.Zero, Vector3.UnitZ);

        Assert.Single(MeshIslands.Split(cut.Positive));
        Assert.Single(MeshIslands.Split(cut.Negative));
    }
}
