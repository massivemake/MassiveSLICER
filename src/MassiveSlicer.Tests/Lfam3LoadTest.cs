using MassiveSlicer.Core.IO;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public class Lfam3LoadTest
{
    private static string RepoRoot =>
        AssetPaths.FindCellsDirectory() is { } cells
            ? Path.GetFullPath(Path.Combine(cells, "..", ".."))
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Theory]
    [InlineData("assets/cells/LFAM3/LFAM3Robot.glb")]
    [InlineData("assets/cells/LFAM3/Toolheads/hv_extruder.glb")]
    [InlineData("assets/cells/LFAM3/Toolheads/scanner.glb")]
    [InlineData("assets/cells/LFAM3/Toolheads/spindle.glb")]
    [InlineData("assets/cells/LFAM3/rotary_bed_bottom.glb")]
    [InlineData("assets/cells/LFAM3/rotary_bed_top.glb")]
    [InlineData("assets/cells/LFAM3/booster_frame.glb")]
    [InlineData("assets/cells/LFAM3/Stands/stand_extruder.glb")]
    [InlineData("assets/cells/LFAM3/Stands/stand_scanner.glb")]
    [InlineData("assets/cells/LFAM3/Stands/stand_spindle.glb")]
    [InlineData("assets/cells/LFAM3/Toolheads/affecto_staubli.glb")]
    public void Lfam3_glb_loads_without_error(string rel)
    {
        Directory.SetCurrentDirectory(RepoRoot);
        Assert.True(AssetPaths.Exists(rel), $"missing: {rel}");

        var root = GltfLoader.Load(AssetPaths.Resolve(rel));
        var (min, max) = Bounds(root);
        var ext = max - min;
        Assert.True(ext.Length > 1f, $"{rel}: extent too small ({ext})");
    }

    [Fact]
    public void Production_tool_holder_strips_scene_frame_for_flange_scaling()
    {
        Directory.SetCurrentDirectory(RepoRoot);
        var rel = "assets/cells/LFAM2/ToolHeads/hv_extruder.glb";
        var toolRoot = GltfLoader.Load(AssetPaths.Resolve(rel));

        var stripped = StripToHolder(toolRoot, Matrix4.CreateRotationY(MathF.PI / 2f));
        var (bMin, bMax) = Bounds(stripped);
        Assert.True((bMax - bMin).Length < 5f, "stripped tool stays metre-native until flange applies scale");

        var framed = WrapWithSceneFrame(toolRoot, Matrix4.CreateRotationY(MathF.PI / 2f));
        var (fMin, fMax) = Bounds(framed);
        Assert.True((fMax - fMin).Length > 100f, "keeping GltfToScene on holder double-scales with flange");
    }

    private static SceneNode StripToHolder(SceneNode toolRoot, Matrix4 local)
    {
        var children = toolRoot.Children.ToList();
        foreach (var child in children)
            toolRoot.RemoveChild(child);

        var holder = new SceneNode { LocalTransform = local };
        foreach (var child in children)
            holder.AddChild(child);
        return holder;
    }

    private static SceneNode WrapWithSceneFrame(SceneNode toolRoot, Matrix4 local)
    {
        var children = toolRoot.Children.ToList();
        foreach (var child in children)
            toolRoot.RemoveChild(child);

        var frame = new SceneNode { LocalTransform = toolRoot.LocalTransform };
        foreach (var child in children)
            frame.AddChild(child);

        var holder = new SceneNode { LocalTransform = local };
        holder.AddChild(frame);
        return holder;
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