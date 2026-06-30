using MassiveSlicer.App;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public sealed class ScanMergerTest
{
    [Fact]
    public void IsScan_detects_scan_names()
    {
        Assert.True(OutlinerModelOps.IsScan(new SceneNode { Name = "Scan 12-34-56" }));
        Assert.True(OutlinerModelOps.IsScan(new SceneNode { Name = "Merged Scan (2 clouds)" }));
        Assert.False(OutlinerModelOps.IsScan(new SceneNode { Name = "part.glb" }));
    }

    [Fact]
    public void Merge_mesh_output_combines_world_geometry()
    {
        var a = ScanNode("Scan A", Matrix4.CreateTranslation(0f, 0f, 0f), 0f);
        var b = ScanNode("Scan B", Matrix4.CreateTranslation(5f, 0f, 0f), 1f);

        var result = ScanMerger.Merge([a, b], ScanMergeOutput.Mesh);
        Assert.NotNull(result);
        Assert.True(result!.PointCount >= 6);
        Assert.True(result.TriangleCount >= 2);
        Assert.False(result.Mesh.RenderAsPoints);
    }

    [Fact]
    public void Merge_point_cloud_output_has_no_indices()
    {
        var a = ScanNode("Scan A", Matrix4.Identity, 0f);
        var b = ScanNode("Scan B", Matrix4.CreateTranslation(2f, 0f, 0f), 0.5f);

        var result = ScanMerger.Merge([a, b], ScanMergeOutput.PointCloud);
        Assert.NotNull(result);
        Assert.Null(result!.Mesh.Indices);
        Assert.True(result.Mesh.RenderAsPoints);
    }

    [Fact]
    public void AlignToReferenceRotationOnly_preserves_moving_centroid()
    {
        var reference = new List<Vector3>
        {
            new(0, 0, 0), new(10, 0, 0), new(0, 10, 0), new(10, 10, 0),
        };
        var offset = new Vector3(50f, -20f, 5f);
        var moving = reference.Select(p => p + offset).ToList();

        var align = ScanPointCloudAligner.AlignToReferenceRotationOnly(reference, moving);
        var aligned = moving.Select(p => ScanPointCloudAligner.TransformPoint(p, align)).ToList();
        var alignedCentroid = Centroid(aligned);
        var movingCentroid  = Centroid(moving);

        Assert.Equal(movingCentroid.X, alignedCentroid.X, 0.2f);
        Assert.Equal(movingCentroid.Y, alignedCentroid.Y, 0.2f);
        Assert.Equal(movingCentroid.Z, alignedCentroid.Z, 0.2f);
    }

    [Fact]
    public void AlignToReference_closes_small_translation_gap()
    {
        var reference = new List<Vector3>
        {
            new(0, 0, 0), new(10, 0, 0), new(0, 10, 0), new(10, 10, 0),
        };
        var moving = reference.Select(p => p + new Vector3(2f, -1f, 0.5f)).ToList();

        var align = ScanPointCloudAligner.AlignToReference(reference, moving);
        var aligned = moving.Select(p => ScanPointCloudAligner.TransformPoint(p, align)).ToList();

        for (int i = 0; i < reference.Count; i++)
        {
            Assert.Equal(reference[i].X, aligned[i].X, 0.5f);
            Assert.Equal(reference[i].Y, aligned[i].Y, 0.5f);
            Assert.Equal(reference[i].Z, aligned[i].Z, 0.5f);
        }
    }

    private static Vector3 Centroid(IReadOnlyList<Vector3> pts)
    {
        var sum = Vector3.Zero;
        foreach (var p in pts) sum += p;
        return sum / pts.Count;
    }

    private static SceneNode ScanNode(string name, Matrix4 local, float zOffset)
    {
        var mesh = BoxMesh(zOffset);
        return new SceneNode
        {
            Name         = name,
            LocalTransform = local,
            PendingMesh  = mesh,
            CullFaces    = false,
        };
    }

    private static MeshData BoxMesh(float zOffset)
    {
        var positions = new[]
        {
            new Vector3(0, 0, zOffset), new Vector3(10, 0, zOffset),
            new Vector3(10, 10, zOffset), new Vector3(0, 10, zOffset),
            new Vector3(0, 0, 10 + zOffset), new Vector3(10, 0, 10 + zOffset),
            new Vector3(10, 10, 10 + zOffset), new Vector3(0, 10, 10 + zOffset),
        };
        uint[] indices =
        [
            0, 1, 2, 0, 2, 3,
            4, 6, 5, 4, 7, 6,
            0, 4, 5, 0, 5, 1,
            1, 5, 6, 1, 6, 2,
            2, 6, 7, 2, 7, 3,
            3, 7, 4, 3, 4, 0,
        ];
        var normals = positions.Select(_ => -Vector3.UnitZ).ToArray();
        return new MeshData(positions, normals, indices, "scan");
    }
}