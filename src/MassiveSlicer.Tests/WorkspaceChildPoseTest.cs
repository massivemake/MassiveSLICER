using MassiveSlicer.App;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Lay on Face and the move gizmo edit the mesh leaf of a STEP import. The outliner
/// root's matrix never sees that, and reloading the STEP rebuilds the leaf at its
/// file pose. Child poses are what put the flip and the move back.
/// </summary>
public class WorkspaceChildPoseTest
{
    [Fact]
    public void Leaf_flip_and_move_round_trip_onto_a_fresh_import()
    {
        var edited = Bracket();
        var leaf = edited.Children[0].Children[0];
        leaf.LocalTransform = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(90f))
                            * Matrix4.CreateTranslation(10f, 20f, 30f);

        // The root stays the import scale. The flip is not in it.
        Assert.Equal(1000f, edited.LocalTransform.Row0.X, 3);
        Assert.Equal(0f, edited.LocalTransform.Row0.Y, 3);

        var poses = WorkspaceService.CollectChildPoses(edited);
        var reloaded = Bracket();
        WorkspaceService.ApplyChildPoses(reloaded, poses);

        AssertClose(leaf.LocalTransform, reloaded.Children[0].Children[0].LocalTransform);
        AssertClose(edited.Children[0].LocalTransform, reloaded.Children[0].LocalTransform);
    }

    [Fact]
    public void Modifier_group_appended_after_the_mesh_does_not_steal_the_leaf_path()
    {
        var edited = Bracket();
        edited.AddChild(new SceneNode { Name = "Modifiers", IsAuthoringOverlay = true });
        var leaf = edited.Children[0].Children[0];
        leaf.LocalTransform = Matrix4.CreateTranslation(4f, 5f, 6f);

        var poses = WorkspaceService.CollectChildPoses(edited);
        Assert.DoesNotContain(poses, p => p.Path == "1");
        Assert.Contains(poses, p => p.Path == "0/0");

        var reloaded = Bracket();
        WorkspaceService.ApplyChildPoses(reloaded, poses);
        AssertClose(leaf.LocalTransform, reloaded.Children[0].Children[0].LocalTransform);
    }

    [Fact]
    public void Embedded_mesh_is_written_in_the_root_frame()
    {
        var root = Bracket();
        var leaf = root.Children[0].Children[0];
        leaf.LocalTransform = Matrix4.CreateTranslation(10f, 0f, 0f);

        var baked = WorkspaceService.MeshInRootSpace(root);
        Assert.NotNull(baked);
        // Leaf vertex (0,0,0) moves by the leaf translation. Root scale is not baked in;
        // that scale stays on the root matrix.
        Assert.Equal(10f, baked!.Positions[0].X, 3);
        Assert.Equal(0f, baked.Positions[0].Y, 3);
        Assert.Equal(0f, baked.Positions[0].Z, 3);
    }

    [Fact]
    public void Child_poses_survive_the_mass_file()
    {
        var doc = new WorkspaceDocument
        {
            Models =
            [
                new WorkspaceModelEntry
                {
                    Name = "bracket",
                    ChildPoses =
                    [
                        new WorkspaceNodePose
                        {
                            Path = "0/0",
                            LocalTransform =
                            [
                                0, -1, 0, 0,
                                1, 0, 0, 0,
                                0, 0, 1, 0,
                                12, 0, 4, 1,
                            ],
                        },
                    ],
                },
            ],
        };

        var path = Path.Combine(Path.GetTempPath(), $"massive-child-pose-{Guid.NewGuid():N}.mass");
        try
        {
            WorkspaceLoader.Save(doc, path);
            var loaded = WorkspaceLoader.Load(path);
            var pose = Assert.Single(Assert.Single(loaded!.Models).ChildPoses);
            Assert.Equal("0/0", pose.Path);
            Assert.Equal(12f, pose.LocalTransform[12], 4);
            Assert.Equal(-1f, pose.LocalTransform[1], 4);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void AssertClose(Matrix4 expected, Matrix4 actual)
    {
        for (int r = 0; r < 4; r++)
        for (int c = 0; c < 4; c++)
            Assert.Equal(expected[r, c], actual[r, c], 4);
    }

    /// <summary>Same shape as a cascadio STEP: scaled root, one body node, one mesh leaf.</summary>
    private static SceneNode Bracket()
    {
        var root = new SceneNode
        {
            Name = "bracket",
            LocalTransform = Matrix4.CreateScale(1000f),
        };
        var body = new SceneNode { Name = "body" };
        var leaf = new SceneNode
        {
            Name = "mesh",
            PendingMesh = new MeshData(
                [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                [0u, 1u, 2u],
                "mesh"),
        };
        body.AddChild(leaf);
        root.AddChild(body);
        return root;
    }
}
