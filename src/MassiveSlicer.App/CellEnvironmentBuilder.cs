using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.App;

/// <summary>Pre-loads LFAM cell environment geometry: stands, rotary bed, multi-tool docks.</summary>
internal static class CellEnvironmentBuilder
{
    internal sealed record BuiltEnvironment(
        IReadOnlyList<SceneNode> EnvironmentNodes,
        SceneNode?               RotaryBedPivot,
        CellMultiToolSet?        MultiTools);

    internal sealed class CellMultiToolSet
    {
        public required Dictionary<string, ToolVisualPair> Tools { get; init; }
        public required string DefaultToolName { get; init; }
        public string? MountedToolName { get; set; }
    }

    internal sealed record ToolVisualPair(SceneNode FlangeHolder, SceneNode? DockHolder);

    public static BuiltEnvironment Build(CellConfig cell)
    {
        var envNodes = new List<SceneNode>();
        SceneNode? pivot = null;
        CellMultiToolSet? multi = null;

        foreach (var stand in cell.Stands)
            TryAddStand(envNodes, stand);

        if (cell.RotaryBed is { } rb)
            pivot = TryAddRotaryBed(envNodes, rb, cell.Robot.WorldPosition);

        var dockable = cell.EffectiveTools.Where(t => t.Dock is not null).ToList();
        if (dockable.Count > 0)
            multi = BuildMultiTools(cell, dockable);

        return new BuiltEnvironment(envNodes, pivot, multi);
    }

    public static SceneNode? BuildFlangeAttachment(FlangeAttachmentCellConfig cfg)
    {
        if (!File.Exists(cfg.ModelPath)) return null;
        try
        {
            var root = GltfLoader.Load(cfg.ModelPath);
            return WrapGlbTool(root, cfg.Name, Matrix4.CreateRotationY(MathF.PI / 2f));
        }
        catch { return null; }
    }

    private static CellMultiToolSet BuildMultiTools(CellConfig cell, List<ToolCellConfig> dockable)
    {
        var rp   = cell.Robot.WorldPosition;
        var dict = new Dictionary<string, ToolVisualPair>(StringComparer.Ordinal);

        foreach (var tool in cell.EffectiveTools)
        {
            if (!File.Exists(tool.ModelPath)) continue;
            try
            {
                var holder = BuildToolHolderMesh(tool);
                if (holder is null) continue;

                SceneNode? dock = null;
                if (tool.Dock is { } d)
                {
                    dock = BuildToolHolderMesh(tool);
                    if (dock is not null)
                    {
                        dock.Name           = $"Dock_{tool.Name}";
                        dock.LocalTransform = DockWorldMatrix(rp, d) * Matrix4.CreateRotationY(MathF.PI / 2f);
                        dock.Selectable     = false;
                    }
                }

                holder.Name       = $"Tool_{tool.Name}";
                holder.Selectable = false;
                holder.Visible    = false;
                dict[tool.Name]   = new ToolVisualPair(holder, dock);
            }
            catch { /* skip broken tool */ }
        }

        var defaultName = cell.EffectiveTools.FirstOrDefault(t => t.Default)?.Name
                       ?? dockable.FirstOrDefault(t => t.Default)?.Name
                       ?? cell.EffectiveTools.FirstOrDefault()?.Name
                       ?? "";

        return new CellMultiToolSet
        {
            Tools           = dict,
            DefaultToolName = defaultName,
            MountedToolName = defaultName,
        };
    }

    private static void TryAddStand(List<SceneNode> envNodes, StandCellConfig stand)
    {
        if (!File.Exists(stand.ModelPath)) return;
        try
        {
            var mesh = GltfLoader.Load(stand.ModelPath);
            TintStandMeshes(mesh);
            var pos = StandWorldPosition(stand.Position);
            var rot = StandWorldRotation(stand.Rotation);
            var wrap = new SceneNode
            {
                Name           = stand.Name,
                LocalTransform = rot * Matrix4.CreateTranslation(pos),
                Selectable     = false,
            };
            wrap.AddChild(mesh);
            envNodes.Add(wrap);
        }
        catch { /* non-critical */ }
    }

    private static SceneNode? TryAddRotaryBed(List<SceneNode> envNodes, RotaryBedCellConfig rb, Float3 robroot)
    {
        if (!File.Exists(rb.BottomPath) || !File.Exists(rb.TopPath)) return null;
        try
        {
            var bottom = GltfLoader.Load(rb.BottomPath);
            var top    = GltfLoader.Load(rb.TopPath);
            TintStandMeshes(bottom);
            TintStandMeshes(top);

            var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false };
            pivot.AddChild(top);

            var root = new SceneNode { Name = "RotaryBed", Selectable = false };
            root.AddChild(bottom);
            root.AddChild(pivot);

            var bp = rb.BasePos;
            var ba = rb.BaseAbc;
            var world = new Vector3(robroot.X + bp[0], robroot.Y + bp[1], robroot.Z + bp[2]);
            root.LocalTransform = KukaAbcMatrix(ba.Length > 0 ? ba[0] : 0f,
                                                ba.Length > 1 ? ba[1] : 0f,
                                                ba.Length > 2 ? ba[2] : 0f)
                              * Matrix4.CreateTranslation(world);
            envNodes.Add(root);
            return pivot;
        }
        catch { return null; }
    }

    /// <summary>CONNECT seed metres → slicer Z-up world mm.</summary>
    private static Vector3 StandWorldPosition(float[] pos)
    {
        float x = pos.Length > 0 ? pos[0] * 1000f : 0f;
        float y = pos.Length > 2 ? -pos[2] * 1000f : 0f;
        float z = pos.Length > 1 ? pos[1] * 1000f : 0f;
        return new Vector3(x, y, z);
    }

    private static Matrix4 StandWorldRotation(float[] rot)
    {
        float rx = rot.Length > 0 ? rot[0] : 0f;
        float ry = rot.Length > 1 ? rot[1] : 0f;
        float rz = rot.Length > 2 ? rot[2] : 0f;
        return Matrix4.CreateRotationZ(rz) * Matrix4.CreateRotationY(ry) * Matrix4.CreateRotationX(rx);
    }

    private static Matrix4 DockWorldMatrix(Float3 robroot, ToolDockCellConfig dock)
        => KukaAbcMatrix(dock.A, dock.B, dock.C)
         * Matrix4.CreateTranslation(robroot.X + dock.X, robroot.Y + dock.Y, robroot.Z + dock.Z);

    private static Matrix4 KukaAbcMatrix(float aDeg, float bDeg, float cDeg)
    {
        float d = MathF.PI / 180f;
        return Matrix4.CreateRotationX(cDeg * d)
             * Matrix4.CreateRotationY(bDeg * d)
             * Matrix4.CreateRotationZ(aDeg * d);
    }

    internal static SceneNode? BuildToolHolderMesh(ToolCellConfig tool)
    {
        bool isGlb = tool.ModelPath.EndsWith(".glb",  StringComparison.OrdinalIgnoreCase)
                  || tool.ModelPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

        if (isGlb)
        {
            var toolRoot = GltfLoader.Load(tool.ModelPath);
            return WrapGlbTool(toolRoot, tool.Name, Matrix4.CreateRotationY(MathF.PI / 2f));
        }

        if (!File.Exists(tool.ModelPath)) return null;
        var stlNode = StlLoader.Load(tool.ModelPath, tool.Name);
        var holder  = new SceneNode
        {
            Name           = tool.Name,
            LocalTransform = Matrix4.CreateScale(1f / 1000f)
                           * Matrix4.CreateRotationX(-MathF.PI / 2f)
                           * Matrix4.CreateRotationY(MathF.PI / 2f),
            Selectable     = false,
        };
        holder.AddChild(stlNode);
        return holder;
    }

    private static SceneNode WrapGlbTool(SceneNode toolRoot, string name, Matrix4 local)
    {
        var children = toolRoot.Children.ToList();
        foreach (var child in children)
            toolRoot.RemoveChild(child);

        var holder = new SceneNode { Name = name, LocalTransform = local, Selectable = false };
        foreach (var child in children)
            holder.AddChild(child);
        return holder;
    }

    private static void TintStandMeshes(SceneNode root)
    {
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is not { } mesh) continue;
            n.PendingMesh = new MeshData(
                mesh.Positions, mesh.Normals, mesh.Indices, mesh.Name,
                new Vector4(0.91f, 0.92f, 0.93f, 1f),
                metallic: 0.25f, roughness: 0.6f);
        }
    }
}