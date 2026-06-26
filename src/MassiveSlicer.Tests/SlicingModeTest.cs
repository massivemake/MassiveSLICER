using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

public class SlicingModeTest
{
    [Fact]
    public void Surface_mode_ignores_infill_and_keeps_tool_vertical_by_default()
    {
        var mesh = BoxMesh(40f, 40f, 20f);
        static int ExtrudeCount(Toolpath tp) =>
            tp.Layers.Sum(l => l.Moves.Count(m => m.Kind == MoveKind.Extrude));

        static bool HasMoveNormals(Toolpath tp) =>
            tp.Layers.SelectMany(l => l.Moves)
                .Any(m => m.Kind == MoveKind.Extrude && m.Normal.LengthSquared() > 0.5f);

        var surfaceShell = PlanarSlicer.Slice([mesh], new SliceSettings
        {
            SlicingMode = SlicingMode.Surface,
            LayerHeight = 5f,
            FirstLayerHeight = 5f,
            BeadWidth = 6f,
            InfillPattern = InfillPattern.None,
        });

        var surfaceWithInfill = PlanarSlicer.Slice([mesh], new SliceSettings
        {
            SlicingMode = SlicingMode.Surface,
            LayerHeight = 5f,
            FirstLayerHeight = 5f,
            BeadWidth = 6f,
            InfillPattern = InfillPattern.Rectilinear,
            InfillSpacingMm = 10f,
        });

        Assert.Equal(ExtrudeCount(surfaceShell), ExtrudeCount(surfaceWithInfill));
        Assert.True(surfaceWithInfill.Layers.Count > 0);
        Assert.False(HasMoveNormals(surfaceWithInfill), "Surface mode should keep tool vertical unless overhang orientation is on");
    }

    private static Vector3[] BoxMesh(float w, float d, float h)
    {
        float hw = w * 0.5f, hd = d * 0.5f;
        float z0 = 0f, z1 = h;
        // 12 triangles (two per face), flat soup.
        return
        [
            // bottom
            new(-hw,-hd,z0), new(hw,-hd,z0), new(hw,hd,z0),
            new(-hw,-hd,z0), new(hw,hd,z0), new(-hw,hd,z0),
            // top
            new(-hw,-hd,z1), new(hw,hd,z1), new(hw,-hd,z1),
            new(-hw,-hd,z1), new(-hw,hd,z1), new(hw,hd,z1),
            // front (y-)
            new(-hw,-hd,z0), new(hw,-hd,z0), new(hw,-hd,z1),
            new(-hw,-hd,z0), new(hw,-hd,z1), new(-hw,-hd,z1),
            // back (y+)
            new(-hw,hd,z0), new(hw,hd,z1), new(hw,hd,z0),
            new(-hw,hd,z0), new(-hw,hd,z1), new(hw,hd,z1),
            // left (x-)
            new(-hw,-hd,z0), new(-hw,-hd,z1), new(-hw,hd,z1),
            new(-hw,-hd,z0), new(-hw,hd,z1), new(-hw,hd,z0),
            // right (x+)
            new(hw,-hd,z0), new(hw,hd,z1), new(hw,-hd,z1),
            new(hw,-hd,z0), new(hw,hd,z0), new(hw,hd,z1),
        ];
    }
}