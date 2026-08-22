using MassiveSlicer.Viewport.Rendering;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public sealed class MillFrontSurfaceBoxTest
{
    static MeshData UnitCube()
    {
        // 8 corners, 12 triangles. +Z face is 4,5,6,7.
        var p = new[]
        {
            new Vector3(-1, -1, -1), new Vector3( 1, -1, -1),
            new Vector3( 1,  1, -1), new Vector3(-1,  1, -1),
            new Vector3(-1, -1,  1), new Vector3( 1, -1,  1),
            new Vector3( 1,  1,  1), new Vector3(-1,  1,  1),
        };
        uint[] idx =
        [
            0,2,1, 0,3,2, // -Z outward
            4,5,6, 4,6,7, // +Z outward
            0,1,5, 0,5,4, // -Y
            3,7,6, 3,6,2, // +Y
            0,4,7, 0,7,3, // -X
            1,2,6, 1,6,5, // +X
        ];
        var n = new Vector3[p.Length];
        return new MeshData(p, n, idx, "cube");
    }

    static Vector3 OrthoProject(Vector3 world, Vector3 eye)
    {
        // Look down -Z from eye.z: screen xy = world xy mapped to 100..300, depth = eye.Z - world.Z
        return new Vector3(world.X * 50f + 200f, world.Y * 50f + 200f, eye.Z - world.Z);
    }

    [Fact]
    public void FrontFacing_plusZ_from_positive_z_camera()
    {
        var eye = new Vector3(0, 0, 10);
        Assert.True(MillFrontSurfaceBox.IsFrontFacing(
            new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), eye));
        Assert.False(MillFrontSurfaceBox.IsFrontFacing(
            new Vector3(-1, -1, -1), new Vector3(1, 1, -1), new Vector3(1, -1, -1), eye));
    }

    [Fact]
    public void BoxSelect_paints_front_face_not_back()
    {
        var mesh = UnitCube();
        var eye = new Vector3(0, 0, 10);
        Vector3 Project(Vector3 w) => OrthoProject(w, eye);
        bool Inside(float x, float y) => x is >= 100 and <= 300 && y is >= 100 and <= 300;

        MillFrontSurfaceBox.CreateDepthBuffer(400, 400, out var zmin, out int gw, out int gh);
        MillFrontSurfaceBox.AccumulateDepth(mesh, Matrix4.Identity, eye, Project, Inside, zmin, gw, gh);

        var hits = new HashSet<int>();
        MillFrontSurfaceBox.CollectVisibleVerts(
            mesh, Matrix4.Identity, eye, Project, Inside, zmin, gw, gh, hits);

        // +Z face verts 4,5,6,7 must be in; -Z face verts 0,1,2,3 must not.
        Assert.Contains(4, hits);
        Assert.Contains(5, hits);
        Assert.Contains(6, hits);
        Assert.Contains(7, hits);
        Assert.DoesNotContain(0, hits);
        Assert.DoesNotContain(1, hits);
        Assert.DoesNotContain(2, hits);
        Assert.DoesNotContain(3, hits);
    }
}
