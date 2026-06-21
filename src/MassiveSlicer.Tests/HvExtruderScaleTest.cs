using MassiveSlicer.Core.IO;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public class HvExtruderScaleTest
{
    private static string RepoRoot =>
        AssetPaths.FindCellsDirectory() is { } cells
            ? Path.GetFullPath(Path.Combine(cells, "..", ".."))
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void Lfam3_extruder_matches_lfam2_native_mesh_extent()
    {
        Directory.SetCurrentDirectory(RepoRoot);

        const string lfam2Path = "assets/cells/LFAM2/ToolHeads/hv_extruder.glb";
        const string lfam3Path = "assets/cells/LFAM3/Toolheads/hv_extruder.glb";

        Assert.True(AssetPaths.Exists(lfam3Path));

        var e2 = Extent(BuildStripHolder(lfam2Path));
        var e3 = Extent(BuildStripHolder(lfam3Path));

        Assert.True(e2 > 0.5f, $"LFAM2 native extent {e2:F3}");
        Assert.InRange(e3 / e2, 0.95f, 1.05f);
    }

    [Fact]
    public void Production_tool_holder_strips_gltf_scene_frame()
    {
        Directory.SetCurrentDirectory(RepoRoot);
        var rel = "assets/cells/LFAM2/ToolHeads/hv_extruder.glb";

        var stripped = BuildStripHolder(rel);
        var wrapped  = BuildFrameHolder(rel);

        Assert.True(Extent(stripped) < 5f, "stripped holder stays metre-native for flange scaling");
        Assert.True(Extent(wrapped) > 100f, "keeping GltfToScene on holder double-scales with flange");
    }

    private static SceneNode BuildStripHolder(string modelPath)
    {
        var toolRoot = GltfLoader.Load(AssetPaths.Resolve(modelPath));
        var children = toolRoot.Children.ToList();
        foreach (var child in children)
            toolRoot.RemoveChild(child);

        var holder = new SceneNode { LocalTransform = Matrix4.CreateRotationY(MathF.PI / 2f) };
        foreach (var child in children)
            holder.AddChild(child);
        return holder;
    }

    private static SceneNode BuildFrameHolder(string modelPath)
    {
        var toolRoot = GltfLoader.Load(AssetPaths.Resolve(modelPath));
        var children = toolRoot.Children.ToList();
        foreach (var child in children)
            toolRoot.RemoveChild(child);

        var frame = new SceneNode { LocalTransform = toolRoot.LocalTransform };
        foreach (var child in children)
            frame.AddChild(child);

        var holder = new SceneNode { LocalTransform = Matrix4.CreateRotationY(MathF.PI / 2f) };
        holder.AddChild(frame);
        return holder;
    }

    private static float Extent(SceneNode root)
    {
        var (min, max) = Bounds(root);
        return (max - min).Length;
    }

    private static (Vector3 Min, Vector3 Max) Bounds(SceneNode root)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        Span<Vector3> corners = stackalloc Vector3[8];

        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is not { } mesh) continue;
            var world = n.WorldTransform;
            var (bMin, bMax) = mesh.LocalBounds;
            corners[0] = new(bMin.X, bMin.Y, bMin.Z); corners[1] = new(bMax.X, bMin.Y, bMin.Z);
            corners[2] = new(bMin.X, bMax.Y, bMin.Z); corners[3] = new(bMax.X, bMax.Y, bMin.Z);
            corners[4] = new(bMin.X, bMin.Y, bMax.Z); corners[5] = new(bMax.X, bMin.Y, bMax.Z);
            corners[6] = new(bMin.X, bMax.Y, bMax.Z); corners[7] = new(bMax.X, bMax.Y, bMax.Z);
            foreach (var p in corners)
            {
                var w = new Vector3(
                    p.X * world.M11 + p.Y * world.M21 + p.Z * world.M31 + world.M41,
                    p.X * world.M12 + p.Y * world.M22 + p.Z * world.M32 + world.M42,
                    p.X * world.M13 + p.Y * world.M23 + p.Z * world.M33 + world.M43);
                min = Vector3.ComponentMin(min, w);
                max = Vector3.ComponentMax(max, w);
            }
        }

        return (min, max);
    }
}