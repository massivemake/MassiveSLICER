using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public sealed class SpindleBitCylinderTest
{
    [Fact]
    public void CreateCylinder_height_matches_extent_and_base_is_at_origin()
    {
        var mesh = MeshFactory.CreateCylinder(radius: 5f, height: 40f, segments: 16, name: "cyl");
        Assert.Equal("cyl", mesh.Name);
        var (min, max) = mesh.LocalBounds;
        Assert.InRange(min.Z, -0.01f, 0.01f);
        Assert.InRange(max.Z, 39.9f, 40.1f);
        Assert.InRange(max.X, 4.9f, 5.1f);
        Assert.True(mesh.Positions.Length > 16);
    }

    [Fact]
    public void FindAnchor_matches_SpindleBit_suffix()
    {
        var root = new SceneNode { Name = "Tool" };
        var body = new SceneNode { Name = "Mesh_0.006" };
        var disc = new SceneNode { Name = "Mesh_0.007__SpindleBit" };
        root.AddChild(body);
        root.AddChild(disc);
        Assert.Same(disc, SpindleBitCylinder.FindAnchor(root));
    }

    [Fact]
    public void UniqueExtentAxis_picks_the_unlike_side_even_when_puck_is_thick()
    {
        // Live SpindleBit AABB: thicker in X (31 mm) than Ø in Y/Z (23 mm).
        var axis = SpindleBitCylinder.UniqueExtentAxis(new Vector3(31.46f, 22.95f, 22.95f));
        Assert.Equal(Vector3.UnitX, axis);
    }

    [Fact]
    public void EstimateFaceNormal_thick_disc_is_along_symmetry_axis_not_rim()
    {
        // Thick puck along +X (same proportions as spindle.glb SpindleBit).
        var mesh = MakeDisc(axis: Vector3.UnitX, radius: 11.5f, thickness: 31.5f, segments: 24);
        var n = Vector3.Normalize(SpindleBitCylinder.EstimateFaceNormal(mesh));
        Assert.True(MathF.Abs(n.X) > 0.98f, $"expected ±X (disc normal), got {n}");
        Assert.InRange(MathF.Abs(n.Y), 0f, 0.1f);
        Assert.InRange(MathF.Abs(n.Z), 0f, 0.1f);
    }

    [Fact]
    public void ComputeLocalTransform_origin_is_disc_center_axis_follows_face_normal()
    {
        var center = new Vector3(-0.085f, 113.85f, 609.15f);
        var faceNormal = Vector3.UnitX;
        var body = new Vector3(2.7f, -42f, 300f);
        var m = SpindleBitCylinder.ComputeLocalTransform(center, faceNormal, body, flip: false);
        var origin = m.Row3.Xyz;
        Assert.InRange((origin - center).Length, 0f, 0.01f);
        var zAxis = Vector3.Normalize(m.Row2.Xyz);
        Assert.True(MathF.Abs(zAxis.X) > 0.98f, $"expected ±X, got {zAxis}");
    }

    [Fact]
    public void EffectiveCylinderLength_falls_back_flute_then_default()
    {
        var bit = new MillBitTool { CylinderLengthMm = 0, TotalLengthMm = 0, FluteLengthMm = 12 };
        Assert.Equal(12, bit.EffectiveCylinderLengthMm);
        bit.FluteLengthMm = 0;
        Assert.Equal(50, bit.EffectiveCylinderLengthMm);
        bit.CylinderLengthMm = 80;
        Assert.Equal(80, bit.EffectiveCylinderLengthMm);
    }

    static MeshData MakeDisc(Vector3 axis, float radius, float thickness, int segments)
    {
        axis = Vector3.Normalize(axis);
        var hint = MathF.Abs(axis.Z) > 0.9f ? Vector3.UnitX : Vector3.UnitZ;
        var x = Vector3.Normalize(Vector3.Cross(hint, axis));
        var y = Vector3.Cross(axis, x);
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        float half = thickness * 0.5f;
        for (int cap = 0; cap < 2; cap++)
        {
            float s = cap == 0 ? -half : half;
            var n = axis * (cap == 0 ? -1f : 1f);
            for (int i = 0; i < segments; i++)
            {
                float t = 2f * MathF.PI * i / segments;
                var p = x * (MathF.Cos(t) * radius) + y * (MathF.Sin(t) * radius) + axis * s;
                positions.Add(p);
                normals.Add(n);
            }
        }
        // Rim verts — more of these than face verts, the old folded-normal trap.
        for (int i = 0; i < segments * 4; i++)
        {
            float t = 2f * MathF.PI * i / (segments * 4);
            var radial = x * MathF.Cos(t) + y * MathF.Sin(t);
            positions.Add(radial * radius);
            normals.Add(radial);
        }
        return new MeshData(positions.ToArray(), normals.ToArray(), null, "disc");
    }
}
