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
    public void FindAnchor_prefers_SpindleBitTCP_plane_over_legacy_disc()
    {
        var root = new SceneNode { Name = "Tool" };
        var body = new SceneNode { Name = "Mesh_0.006" };
        var disc = new SceneNode { Name = "Mesh_0.007__SpindleBit" };
        var plane = new SceneNode { Name = "Mesh_0.001__SpindleBitTCP" };
        root.AddChild(body);
        root.AddChild(disc);
        root.AddChild(plane);
        Assert.Same(plane, SpindleBitCylinder.FindAnchor(root));
        Assert.Same(plane, SpindleBitCylinder.FindTcpPlane(root));
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
    public void MmToParentLocal_metre_disc_is_one_thousandth()
    {
        // Live puck after bake: ~31 mm × Ø23 mm → metres.
        var discM = MakeDisc(Vector3.UnitX, radius: 0.0115f, thickness: 0.0315f, segments: 16);
        Assert.InRange(SpindleBitCylinder.MmToParentLocal(discM, 76.2f), 0.0761f, 0.0763f);
        Assert.InRange(SpindleBitCylinder.MmToParentLocal(discM, 1f), 0.00099f, 0.00101f);
    }

    [Fact]
    public void MmToParentLocal_millimetre_disc_stays_millimetres()
    {
        var discMm = MakeDisc(Vector3.UnitX, radius: 11.5f, thickness: 31.5f, segments: 16);
        Assert.InRange(SpindleBitCylinder.MmToParentLocal(discMm, 76.2f), 76.1f, 76.3f);
    }

    [Fact]
    public void BuildNode_on_metre_disc_is_bit_scale_not_cell_scale()
    {
        var discM = MakeDisc(Vector3.UnitX, radius: 0.0115f, thickness: 0.0315f, segments: 16);
        var node = SpindleBitCylinder.BuildNode(76.2f, 1f, Matrix4.Identity, discM);
        var (min, max) = node.PendingMesh!.LocalBounds;
        Assert.InRange(max.Z - min.Z, 0.00099f, 0.00101f); // 1 mm stick-out
        Assert.InRange(max.X, 0.0380f, 0.0382f);            // Ø 76.2 mm
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

    [Fact]
    public void TryGetCutterWorld_uses_disc_center_and_moves_to_cylinder_tip()
    {
        var root = new SceneNode { Name = "Spindle", LocalTransform = Matrix4.Identity };
        var discMesh = MakeDisc(Vector3.UnitZ, radius: 11.5f, thickness: 4f, segments: 16);
        var disc = new SceneNode
        {
            Name = "Mesh__SpindleBit",
            PendingMesh = discMesh,
            LocalTransform = Matrix4.CreateTranslation(10f, 20f, 30f),
        };
        root.AddChild(disc);
        Assert.True(SpindleBitCylinder.TryGetCutterWorld(root, out var origin, out var axis));
        Assert.True(axis.Z > 0.9f, $"axis away from body should be +Z, got {axis}");
        Assert.InRange(origin.X, 9f, 11f);
        Assert.InRange(origin.Y, 19f, 21f);

        var cylLocal = SpindleBitCylinder.ComputeLocalTransform(discMesh, bodyCentroidLocal: Vector3.Zero, flip: false);
        var cyl = SpindleBitCylinder.BuildNode(20f, 50f, cylLocal, discMesh);
        SpindleBitCylinder.AttachPreview(root, disc, cyl);
        Assert.True(SpindleBitCylinder.TryGetCutterWorld(root, out var tip, out _));
        Assert.True(tip.Z > origin.Z + 1f, $"cylinder tip should be past the disc ({origin.Z} -> {tip.Z})");
    }

    [Fact]
    public void AttachPreview_survives_hiding_the_tcp_plane()
    {
        var root = new SceneNode { Name = "Spindle", LocalTransform = Matrix4.Identity };
        var planeMesh = MakePlane(Vector3.UnitZ, 20f);
        var plane = new SceneNode
        {
            Name = "Mesh_0.001__SpindleBitTCP",
            PendingMesh = planeMesh,
            LocalTransform = Matrix4.CreateTranslation(0f, 0f, 110f),
        };
        root.AddChild(plane);
        var local = SpindleBitCylinder.ComputeLocalTransform(root, plane, planeMesh, flip: false);
        var cyl = SpindleBitCylinder.BuildNode(20f, 50f, local, planeMesh);
        SpindleBitCylinder.AttachPreview(root, plane, cyl);
        SpindleBitCylinder.HideTcpDatum(root);

        Assert.False(plane.Visible);
        Assert.True(cyl.Visible);
        Assert.Same(root, cyl.Parent);
        Assert.DoesNotContain(cyl, plane.Children);
    }

    [Fact]
    public void ComputeLocalTransform_follows_housing_axis_not_puck_thickness()
    {
        // Housing is a tall Z cylinder (the spindle). Puck is thick along X — the old
        // code used that and cocked the preview off the spindle.
        var root = new SceneNode { Name = "Spindle", LocalTransform = Matrix4.Identity };
        var housing = new SceneNode
        {
            Name = "Body",
            PendingMesh = MakeDisc(Vector3.UnitZ, radius: 40f, thickness: 200f, segments: 20),
            LocalTransform = Matrix4.Identity,
        };
        var disc = new SceneNode
        {
            Name = "Mesh__SpindleBit",
            PendingMesh = MakeDisc(Vector3.UnitX, radius: 11.5f, thickness: 31.5f, segments: 16),
            LocalTransform = Matrix4.CreateTranslation(0f, 0f, 110f),
        };
        root.AddChild(housing);
        root.AddChild(disc);

        Assert.Same(housing, SpindleBitCylinder.FindHousing(root, disc));
        var m = SpindleBitCylinder.ComputeLocalTransform(root, disc, disc.PendingMesh!, flip: false);
        var z = Vector3.Normalize(m.Row2.Xyz);
        Assert.True(z.Z > 0.95f, $"expected +Z along the housing (purple line), got {z}");
        Assert.InRange(MathF.Abs(z.X), 0f, 0.15f);
    }

    [Fact]
    public void ComputeLocalTransform_tcp_plane_uses_plane_normal_not_housing()
    {
        // Housing long axis is +X (the 90-deg-off case in the shop shot). The
        // authored SpindleBitTCP plane lies in XY, so its normal is +Z — that
        // is the purple line / bit axis.
        var root = new SceneNode { Name = "Spindle", LocalTransform = Matrix4.Identity };
        var housing = new SceneNode
        {
            Name = "Body",
            PendingMesh = MakeDisc(Vector3.UnitX, radius: 40f, thickness: 200f, segments: 20),
            LocalTransform = Matrix4.Identity,
        };
        var planeMesh = MakePlane(normal: Vector3.UnitZ, size: 20f);
        var plane = new SceneNode
        {
            Name = "Mesh_0.001__SpindleBitTCP",
            PendingMesh = planeMesh,
            LocalTransform = Matrix4.CreateTranslation(0f, 0f, 110f),
        };
        root.AddChild(housing);
        root.AddChild(plane);

        Assert.Same(plane, SpindleBitCylinder.FindAnchor(root));
        var m = SpindleBitCylinder.ComputeLocalTransform(root, plane, planeMesh, flip: false);
        var z = Vector3.Normalize(m.Row2.Xyz);
        Assert.True(z.Z > 0.95f, $"expected +Z (plane normal / purple line), got {z}");
        Assert.InRange(MathF.Abs(z.X), 0f, 0.15f);
    }

    static MeshData MakePlane(Vector3 normal, float size)
    {
        normal = Vector3.Normalize(normal);
        var hint = MathF.Abs(normal.Z) > 0.9f ? Vector3.UnitX : Vector3.UnitZ;
        var x = Vector3.Normalize(Vector3.Cross(hint, normal));
        var y = Vector3.Cross(normal, x);
        float h = size * 0.5f;
        var positions = new[]
        {
            -x * h - y * h,
             x * h - y * h,
             x * h + y * h,
            -x * h + y * h,
        };
        var normals = new[] { normal, normal, normal, normal };
        uint[] indices = [0, 1, 2, 0, 2, 3];
        return new MeshData(positions, normals, indices, "SpindleBitTCP");
    }
}
