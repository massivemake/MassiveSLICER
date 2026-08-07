using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

/// <summary>
/// A slice plane landing exactly on a mesh vertex used to produce a zero-length
/// "contour" at that point. It could not be extruded, but it survived into the
/// toolpath as its own path and forced a travel out to it and back — on the Dragon
/// column that was a 1.6 m round trip on a single layer.
/// </summary>
public class DegenerateContourTest
{
    // Box occupying X/Y 0..100, Z 0..10.
    private static Vector3[] Box(float x0, float y0, float x1, float y1, float z0, float z1)
    {
        Vector3 V(float x, float y, float z) => new(x, y, z);
        var c = new[]
        {
            V(x0, y0, z0), V(x1, y0, z0), V(x1, y1, z0), V(x0, y1, z0),
            V(x0, y0, z1), V(x1, y0, z1), V(x1, y1, z1), V(x0, y1, z1),
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

    /// <summary>Square pyramid; its apex sits exactly at <paramref name="apexZ"/>.</summary>
    private static Vector3[] Pyramid(float cx, float cy, float half, float baseZ, float apexZ)
    {
        var a = new Vector3(cx, cy, apexZ);
        var b0 = new Vector3(cx - half, cy - half, baseZ);
        var b1 = new Vector3(cx + half, cy - half, baseZ);
        var b2 = new Vector3(cx + half, cy + half, baseZ);
        var b3 = new Vector3(cx - half, cy + half, baseZ);
        return
        [
            b0, b2, b1, b0, b3, b2,          // base
            b0, b1, a,  b1, b2, a,           // sides
            b2, b3, a,  b3, b0, a,
        ];
    }

    // Surface mode skips the contour inset, so the raw cross-section becomes the
    // toolpath centreline directly. That is the mode the Dragon column is sliced in,
    // and the one where a degenerate contour reaches the toolpath — with the inset on,
    // Clipper annihilates zero-area loops before they get that far.
    private static SliceSettings Settings() => new()
    {
        SlicingMode      = SlicingMode.Surface,
        LayerHeight      = 1f,
        FirstLayerHeight = 1f,
        BeadWidth        = 8f,
        InfillPattern    = InfillPattern.None,
    };

    /// <summary>
    /// Every run of extrusion between travels, with its total path length.
    /// A single zero-length move is normal — a closed contour repeats its first point,
    /// so the closing move has no length. What must never happen is a whole PATH with
    /// nothing in it: that is what costs a travel out and back.
    /// </summary>
    private static List<(int Layer, float Len)> ExtrusionPaths(Toolpath toolpath)
    {
        var paths = new List<(int, float)>();
        for (int li = 0; li < toolpath.Layers.Count; li++)
        {
            float len = 0f;
            bool open = false;
            foreach (var mv in toolpath.Layers[li].Moves)
            {
                if (mv.Kind == MoveKind.Extrude)
                {
                    len += Vector3.Distance(mv.From, mv.To);
                    open = true;
                }
                else if (open)
                {
                    paths.Add((li, len));
                    len = 0f;
                    open = false;
                }
            }
            if (open) paths.Add((li, len));
        }
        return paths;
    }

    /// <summary>
    /// Layers land at Z = 1, 2, 3 … so the pyramid apex at Z = 5 is grazed exactly.
    /// </summary>
    [Fact]
    public void Plane_grazing_a_vertex_creates_no_unprintable_path()
    {
        var toolpath = PlanarSlicer.Slice(
            [Box(0, 0, 100, 100, 0, 10), Pyramid(300, 300, 30, 0f, 5f)],
            Settings());

        var unprintable = ExtrusionPaths(toolpath).Where(p => p.Len < 8f).ToList();

        Assert.True(unprintable.Count == 0,
            "paths too short to extrude survived into the toolpath: " +
            string.Join(", ", unprintable.Select(p => $"layer {p.Layer} = {p.Len:F4}mm")));
    }

    /// <summary>
    /// Guards the test above against passing vacuously: the box and the pyramid's
    /// real cross-sections must still be there.
    /// </summary>
    [Fact]
    public void Real_geometry_survives_the_filter()
    {
        var toolpath = PlanarSlicer.Slice(
            [Box(0, 0, 100, 100, 0, 10), Pyramid(300, 300, 30, 0f, 5f)],
            Settings());

        Assert.True(toolpath.Layers.Count >= 9, $"expected ~9 layers, got {toolpath.Layers.Count}");

        float extruded = toolpath.Layers
            .SelectMany(l => l.Moves)
            .Where(m => m.Kind == MoveKind.Extrude)
            .Sum(m => Vector3.Distance(m.From, m.To));
        Assert.True(extruded > 3000f, $"expected the box walls to be printed, got {extruded:F0}mm");

        // The pyramid is a separate island: layers below the apex must reach out to it.
        bool reachesPyramid = toolpath.Layers
            .SelectMany(l => l.Moves)
            .Any(m => m.From.X > 250f);
        Assert.True(reachesPyramid, "pyramid cross-sections were dropped along with the degenerate one");
    }

    /// <summary>A loop far shorter than one bead cannot be extruded and must not survive.</summary>
    [Fact]
    public void Sub_bead_island_is_discarded()
    {
        // 0.2mm-wide post next to the box: perimeter 0.8mm, far under the 8mm bead.
        var toolpath = PlanarSlicer.Slice(
            [Box(0, 0, 100, 100, 0, 10), Box(300, 300, 300.2f, 300.2f, 0, 10)],
            Settings());

        bool touchedPost = toolpath.Layers
            .SelectMany(l => l.Moves)
            .Any(m => m.From.X > 250f || m.To.X > 250f);

        Assert.False(touchedPost, "an unprintable 0.2mm post was still given a toolpath");
    }
}
