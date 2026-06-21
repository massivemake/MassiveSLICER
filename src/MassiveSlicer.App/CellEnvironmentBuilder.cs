using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using SceneNodeClone = MassiveSlicer.Viewport.Scene.SceneNodeClone;
using OpenTK.Mathematics;

namespace MassiveSlicer.App;

/// <summary>Pre-loads LFAM cell environment geometry: stands, rotary bed, multi-tool docks.</summary>
internal static class CellEnvironmentBuilder
{
    /// <summary>When true, <see cref="Lfam3MinimalProbe"/> strips LFAM 3 environment geometry.</summary>
    internal static bool Lfam3MinimalProbeActive => Lfam3MinimalProbe.Enabled;

    private static readonly Dictionary<string, (long Mtime, long Size, SceneNode Template)> _preparedStandCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (long Mtime, long Size, SceneNode Template)> _preparedSceneGlbCache = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Re-applies stand / rotary / dock transforms from the live cell JSON onto cached
    /// scene nodes (geometry is reused; only placement matrices change).
    /// </summary>
    public static void RefreshPlacements(CellSwapPayload payload)
    {
        var cell = payload.Config;
        var rp   = cell.Robot.WorldPosition;

        foreach (var stand in cell.Stands)
        {
            var node = payload.EnvironmentNodes.FirstOrDefault(n => n.Name == stand.Name);
            if (node is null) continue;
            var pos = StandWorldPosition(stand.Position);
            var rot = StandWorldRotation(stand.Rotation);
            node.LocalTransform = rot * Matrix4.CreateTranslation(pos);
        }

        foreach (var env in payload.EnvironmentNodes)
        {
            if (env.Name != "RotaryBed" || cell.RotaryBed is not { } rb) continue;
            ApplyRotaryRootTransform(env, rb, rp);
            var w = env.LocalTransform.Row3.Xyz;
            System.Console.WriteLine(
                $"[cell] rotary placement: world=({w.X:F1}, {w.Y:F1}, {w.Z:F1})  " +
                $"basePos=[{rb.BasePos[0]:F1}, {rb.BasePos[1]:F1}, {rb.BasePos[2]:F1}]  " +
                $"baseAbc=[{rb.BaseAbc[0]:F2}, {rb.BaseAbc[1]:F2}, {rb.BaseAbc[2]:F2}]");
        }

        if (payload.MultiTools is { } mt)
        {
            foreach (var tool in cell.EffectiveTools)
            {
                if (tool.Dock is not { } d) continue;
                if (!mt.Tools.TryGetValue(tool.Name, out var pair) || pair.DockHolder is null) continue;
                pair.DockHolder.LocalTransform = DockWorldMatrix(rp, d) * Matrix4.CreateRotationY(MathF.PI / 2f);
            }
        }
    }

    private static void ApplyRotaryRootTransform(SceneNode root, RotaryBedCellConfig rb, Float3 robroot)
    {
        var bp = rb.BasePos;
        var ba = rb.BaseAbc;
        var world = new Vector3(robroot.X + bp[0], robroot.Y + bp[1], robroot.Z + bp[2]);
        root.LocalTransform = KukaAbcMatrix(ba.Length > 0 ? ba[0] : 0f,
                                            ba.Length > 1 ? ba[1] : 0f,
                                            ba.Length > 2 ? ba[2] : 0f)
                          * Matrix4.CreateTranslation(world);
    }

    public static BuiltEnvironment Build(CellConfig cell)
    {
        var envNodes = new List<SceneNode>();
        SceneNode? pivot = null;
        CellMultiToolSet? multi = null;

        if (!Lfam3MinimalProbeActive)
        {
            foreach (var stand in cell.Stands)
                TryAddStand(envNodes, stand);
        }
        else
        {
            System.Console.WriteLine("[cell] Lfam3MinimalProbe: skipping stands");
        }

        if (cell.RotaryBed is { } rb)
            pivot = TryAddRotaryBed(envNodes, rb, cell.Robot.WorldPosition);

        if (!Lfam3MinimalProbeActive)
        {
            var dockable = cell.EffectiveTools.Where(t => t.Dock is not null).ToList();
            if (dockable.Count > 0)
                multi = BuildMultiTools(cell, dockable);
        }
        else
        {
            System.Console.WriteLine("[cell] Lfam3MinimalProbe: skipping spindle/scanner/multi-tool visuals");
        }

        return new BuiltEnvironment(envNodes, pivot, multi);
    }

    public static SceneNode? BuildFlangeAttachment(FlangeAttachmentCellConfig cfg)
    {
        if (!AssetPaths.Exists(cfg.ModelPath)) return null;
        try
        {
            var root = GltfLoader.Load(AssetPaths.Resolve(cfg.ModelPath));
            return WrapGlbTool(root, cfg.Name, Matrix4.CreateRotationY(MathF.PI / 2f));
        }
        catch { return null; }
    }

    private static CellMultiToolSet BuildMultiTools(CellConfig cell, List<ToolCellConfig> dockable)
    {
        var rp   = cell.Robot.WorldPosition;
        var dict = new Dictionary<string, ToolVisualPair>(StringComparer.Ordinal);
        var meshTemplateByPath = new Dictionary<string, SceneNode>(StringComparer.OrdinalIgnoreCase);

        var defaultName = cell.EffectiveTools.FirstOrDefault(t => t.Default)?.Name
                       ?? dockable.FirstOrDefault(t => t.Default)?.Name
                       ?? cell.EffectiveTools.FirstOrDefault()?.Name
                       ?? "";

        foreach (var tool in cell.EffectiveTools)
        {
            if (!AssetPaths.Exists(tool.ModelPath))
            {
                System.Console.Error.WriteLine($"[cell] missing tool model: {tool.ModelPath} ({tool.Name})");
                continue;
            }

            try
            {
                var resolved = AssetPaths.Resolve(tool.ModelPath);
                if (!meshTemplateByPath.TryGetValue(resolved, out var template))
                {
                    template = BuildToolHolderMesh(tool);
                    if (template is null) continue;
                    meshTemplateByPath[resolved] = template;
                }

                var holder = SceneNodeClone.DeepClone(template);
                if (holder is null) continue;

                SceneNode? dock = null;
                if (tool.Dock is { } d)
                {
                    dock = SceneNodeClone.DeepClone(holder);
                    dock.Name           = $"Dock_{tool.Name}";
                    dock.LocalTransform = DockWorldMatrix(rp, d) * Matrix4.CreateRotationY(MathF.PI / 2f);
                    dock.Selectable     = false;
                    dock.Visible        = true;
                    TintStandMeshes(dock);
                }

                holder.Name       = $"Tool_{tool.Name}";
                holder.Selectable = false;
                holder.Visible    = false;
                dict[tool.Name]   = new ToolVisualPair(holder, dock);
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[cell] tool load failed {tool.Name}: {ex.Message}");
            }
        }

        int docked = dict.Values.Count(p => p.DockHolder is { Visible: true });
        System.Console.WriteLine(
            $"[cell] multi-tool visuals: {dict.Count} loaded ({docked} parked on docks, default={defaultName})");

        return new CellMultiToolSet
        {
            Tools           = dict,
            DefaultToolName = defaultName,
            MountedToolName = null,
        };
    }

    private static SceneNode LoadPreparedStand(string resolvedPath, string standName)
    {
        var fi = new FileInfo(resolvedPath);
        long mtime = fi.LastWriteTimeUtc.Ticks;
        long size  = fi.Length;

        if (_preparedStandCache.TryGetValue(resolvedPath, out var cached)
            && cached.Mtime == mtime && cached.Size == size)
            return SceneNodeClone.DeepClone(cached.Template);

        var native = GltfLoader.LoadNativeMeters(resolvedPath);
        ImportHelper.RecenterStandYup(native);
        var stats = StandMeshPreparer.OptimizeSubtree(native);
        var merge = SceneMeshMerger.MergeSubtree(native, standName + "_merged");
        System.Console.WriteLine(
            $"[stands] {standName}: cleanup {stats.BeforeTriangles:N0} → {stats.AfterTriangles:N0} tris; " +
            $"merged {merge.SourceMeshes} meshes → 1 draw call, {merge.Triangles:N0} tris");

        _preparedStandCache[resolvedPath] = (mtime, size, SceneNodeClone.DeepClone(native));
        return SceneNodeClone.DeepClone(native);
    }

    private static readonly Dictionary<string, (long Mtime, long Size, SceneNode Template)> _rotaryBedCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads a rotary-bed GLB without mesh cleanup (CONNECT parts share an axis at the origin).
    /// </summary>
    private static SceneNode LoadRotaryBedPart(string resolvedPath, string label)
    {
        var fi = new FileInfo(resolvedPath);
        long mtime = fi.LastWriteTimeUtc.Ticks;
        long size  = fi.Length;

        if (_rotaryBedCache.TryGetValue(resolvedPath, out var cached)
            && cached.Mtime == mtime && cached.Size == size)
            return SceneNodeClone.DeepClone(cached.Template);

        var root = GltfLoader.Load(resolvedPath);
        var before = SceneTriangleStats.Count(root);
        var merge  = SceneMeshMerger.MergeSubtree(root, Path.GetFileNameWithoutExtension(resolvedPath) + "_merged");
        System.Console.WriteLine(
            $"[cell] {label}: merged {merge.SourceMeshes} meshes → 1 draw call, {merge.Triangles:N0} tris (was {before.Meshes} meshes)");

        _rotaryBedCache[resolvedPath] = (mtime, size, SceneNodeClone.DeepClone(root));
        return SceneNodeClone.DeepClone(root);
    }

    private static SceneNode LoadPreparedSceneGlb(string resolvedPath, string label)
    {
        var fi = new FileInfo(resolvedPath);
        long mtime = fi.LastWriteTimeUtc.Ticks;
        long size  = fi.Length;

        if (_preparedSceneGlbCache.TryGetValue(resolvedPath, out var cached)
            && cached.Mtime == mtime && cached.Size == size)
            return SceneNodeClone.DeepClone(cached.Template);

        var root  = GltfLoader.Load(resolvedPath);
        var stats = StandMeshPreparer.OptimizeSubtree(root, StandMeshPreparer.DefaultSceneGlbOptions);
        System.Console.WriteLine(
            $"[cell] {label}: mesh cleanup {stats.BeforeTriangles:N0} → {stats.AfterTriangles:N0} tris ({stats.Meshes} meshes)");

        _preparedSceneGlbCache[resolvedPath] = (mtime, size, SceneNodeClone.DeepClone(root));
        return SceneNodeClone.DeepClone(root);
    }

    private static void TryAddStand(List<SceneNode> envNodes, StandCellConfig stand)
    {
        if (!AssetPaths.Exists(stand.ModelPath))
        {
            System.Console.Error.WriteLine($"[stands] missing model: {stand.ModelPath} ({stand.Name})");
            return;
        }

        try
        {
            var modelPath = AssetPaths.Resolve(stand.ModelPath);
            var native    = LoadPreparedStand(modelPath, stand.Name);
            TintStandMeshes(native);

            var frame = new SceneNode
            {
                Name           = stand.Name + "_Frame",
                LocalTransform = GltfLoader.GltfToScene,
                Selectable     = false,
            };
            frame.AddChild(native);

            var pos = StandWorldPosition(stand.Position);
            var rot = StandWorldRotation(stand.Rotation);
            var wrap = new SceneNode
            {
                Name           = stand.Name,
                LocalTransform = rot * Matrix4.CreateTranslation(pos),
                Selectable     = false,
            };
            wrap.AddChild(frame);
            envNodes.Add(wrap);
            System.Console.WriteLine(
                $"[stands] loaded {stand.Name} at ({pos.X:F0}, {pos.Y:F0}, {pos.Z:F0})");
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[stands] failed {stand.Name}: {ex.Message}");
        }
    }

    private static SceneNode? TryAddRotaryBed(List<SceneNode> envNodes, RotaryBedCellConfig rb, Float3 robroot)
    {
        if (!AssetPaths.Exists(rb.BottomPath) || !AssetPaths.Exists(rb.TopPath))
        {
            System.Console.Error.WriteLine(
                $"[cell] missing rotary bed: bottom={rb.BottomPath} top={rb.TopPath}");
            return null;
        }

        try
        {
            var bottomPath = AssetPaths.Resolve(rb.BottomPath);
            var topPath    = AssetPaths.Resolve(rb.TopPath);
            var bottom     = LoadRotaryBedPart(bottomPath, "rotary bottom");
            var top        = LoadRotaryBedPart(topPath, "rotary top");
            TintStandMeshes(bottom);
            TintStandMeshes(top);

            var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false };
            pivot.AddChild(top);

            var root = new SceneNode { Name = "RotaryBed", Selectable = false };
            root.AddChild(bottom);
            root.AddChild(pivot);

            ApplyRotaryRootTransform(root, rb, robroot);
            var world = root.LocalTransform.Row3.Xyz;
            envNodes.Add(root);
            System.Console.WriteLine($"[cell] rotary bed at ({world.X:F0}, {world.Y:F0}, {world.Z:F0})");
            return pivot;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[cell] rotary bed load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>CONNECT seed metres → slicer Z-up world mm.</summary>
    private static Vector3 StandWorldPosition(float[] pos)
    {
        float x = pos.Length > 0 ? pos[0] * 1000f : 0f;
        float y = pos.Length > 2 ? -pos[2] * 1000f : 0f;
        float z = pos.Length > 1 ? pos[1] * 1000f : 0f;
        return new Vector3(x, y, z);
    }

    /// <summary>
    /// Three.js Y-up XYZ euler (radians) → Z-up world rotation.
    /// Conjugates through the same +90° X frame change used by <see cref="GltfLoader.GltfToScene"/>.
    /// </summary>
    private static Matrix4 StandWorldRotation(float[] rot)
    {
        float rx = rot.Length > 0 ? rot[0] : 0f;
        float ry = rot.Length > 1 ? rot[1] : 0f;
        float rz = rot.Length > 2 ? rot[2] : 0f;

        var rThree = Matrix4.CreateRotationX(rx)
                   * Matrix4.CreateRotationY(ry)
                   * Matrix4.CreateRotationZ(rz);
        var frame  = Matrix4.CreateRotationX(MathF.PI / 2f);
        Matrix4.Invert(frame, out var frameInv);
        return frameInv * rThree * frame;
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
            var toolRoot = LoadPreparedSceneGlb(AssetPaths.Resolve(tool.ModelPath), $"tool {tool.Name}");
            return WrapGlbTool(toolRoot, tool.Name, Matrix4.CreateRotationY(MathF.PI / 2f));
        }

        if (!AssetPaths.Exists(tool.ModelPath)) return null;
        var stlNode = StlLoader.Load(AssetPaths.Resolve(tool.ModelPath), tool.Name);
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

        // Strip GltfToScene from the root — robot flange world transform supplies mm scale.
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