using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.App;

/// <summary>Builds <see cref="CellSwapPayload"/> off the UI thread with scene caching.</summary>
internal static class CellSceneLoader
{
    private static readonly Vector4 BedLimeGreen = new(0.35f, 1.0f, 0.05f, 1f);
    private const float BedAluminumLimeTint = 0.45f;

    public static CellSwapPayload Load(string path, RightPanelTab defaultTab, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);

        var cacheKey = CellSceneCache.CacheKey(path);
        if (CellSceneCache.TryGet(cacheKey, out var cached))
        {
            var refreshed = CellSceneCache.ClonePayload(cached) with { Config = CellLoader.Load(path) };
            CellEnvironmentBuilder.RefreshPlacements(refreshed);
            return refreshed;
        }

        var cell = CellLoader.Load(path);
        ct.ThrowIfCancellationRequested();

        SceneNode? robotBaseNode = null;
        if (!AssetPaths.Exists(cell.Robot.ModelPath))
        {
            System.Console.Error.WriteLine(
                $"[cell] robot model missing: {cell.Robot.ModelPath} (resolved: {AssetPaths.Resolve(cell.Robot.ModelPath)})");
        }
        else
        {
            try
            {
                var robot = GltfLoader.Load(AssetPaths.Resolve(cell.Robot.ModelPath));
                var p     = cell.Robot.WorldPosition;
                robotBaseNode = new SceneNode
                {
                    Name           = $"{cell.Name}_Robot",
                    LocalTransform = Matrix4.CreateTranslation(p.X, p.Y, p.Z),
                    Selectable     = false,
                };
                robotBaseNode.AddChild(robot);
                robotBaseNode.MarkEnvironmentSubtree();
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[cell] failed to load robot model: {ex.Message}");
            }
        }

        ct.ThrowIfCancellationRequested();

        SceneNode? boosterNode = null;
        if (cell.BoosterFrame is { } frame && AssetPaths.Exists(frame.ModelPath))
        {
            try
            {
                var framePath = AssetPaths.Resolve(frame.ModelPath);
                var ext  = Path.GetExtension(framePath).ToLowerInvariant();
                var node = (ext is ".glb" or ".gltf")
                    ? GltfLoader.Load(framePath)
                    : StlLoader.Load(framePath, $"{cell.Name}_BoosterFrame");
                var p    = frame.WorldPosition;
                if (p.X != 0f || p.Y != 0f || p.Z != 0f)
                {
                    boosterNode = new SceneNode
                    {
                        Name           = node.Name + "_Root",
                        LocalTransform = Matrix4.CreateTranslation(p.X, p.Y, p.Z),
                        Selectable     = false,
                    };
                    boosterNode.AddChild(node);
                }
                else
                {
                    node.Selectable = false;
                    boosterNode     = node;
                }
            }
            catch { /* non-critical */ }
        }

        ct.ThrowIfCancellationRequested();

        var environment = CellEnvironmentBuilder.Build(cell);

        SceneNode? bedNode = null;
        if (cell.Bed.Hidden)
            System.Console.WriteLine($"[cell] {cell.Name}: flat bed mesh hidden (rotary bed or grid only).");
        else if (cell.Bed.ModelPath is { } bedPath && !AssetPaths.Exists(bedPath))
            System.Console.Error.WriteLine($"[cell] bed model missing: {bedPath}");
        if (!cell.Bed.Hidden && cell.Bed.ModelPath is { } bedPath2 && AssetPaths.Exists(bedPath2))
        {
            try
            {
                var resolved = AssetPaths.Resolve(bedPath2);
                var bedExt  = Path.GetExtension(resolved).ToLowerInvariant();
                var bed     = (bedExt is ".glb" or ".gltf")
                    ? GltfLoader.Load(resolved)
                    : StlLoader.Load(resolved, $"{cell.Name}_Bed");
                var o       = cell.Bed.VisualMeshOrigin(cell.Robot.WorldPosition);
                var wrapper = new SceneNode
                {
                    Name           = bed.Name + "_Root",
                    LocalTransform = Matrix4.CreateTranslation(o.X, o.Y, o.Z),
                    Selectable     = false,
                };
                wrapper.AddChild(bed);
                wrapper.MarkEnvironmentSubtree();
                ApplyBedMaterialTint(wrapper);
                bedNode = wrapper;
            }
            catch { /* non-critical */ }
        }

        ct.ThrowIfCancellationRequested();

        ToolCellConfig? firstTool;
        if (Lfam3MinimalProbe.IsActive(cell.Name))
        {
            firstTool = cell.EffectiveTools.FirstOrDefault(t => t.Name == "HV Extruder")
                     ?? cell.EffectiveTools.FirstOrDefault();
            System.Console.WriteLine("[cell] Lfam3MinimalProbe: robot + extruder only");
        }
        else
        {
            bool cellHasScan = cell.ScanToolName is not null;
            var defaultTabToolName = defaultTab switch
            {
                RightPanelTab.Scan when cellHasScan => cell.ScanToolName,
                RightPanelTab.Additive              => "HV Extruder",
                _                                   => cellHasScan ? cell.ScanToolName : "HV Extruder",
            };
            firstTool = (defaultTabToolName is not null
                            ? cell.EffectiveTools.FirstOrDefault(t => t.Name == defaultTabToolName)
                            : null)
                     ?? (cell.EffectiveTools.Count > 0 ? cell.EffectiveTools[0] : null);
        }

        SceneNode? toolHolder = null;
        if (Lfam3MinimalProbe.IsActive(cell.Name))
        {
            if (firstTool is not null)
            {
                try { toolHolder = BuildToolHolder(firstTool); }
                catch { /* non-critical */ }
            }
        }
        else if (environment.MultiTools is null && firstTool is not null)
        {
            try { toolHolder = BuildToolHolder(firstTool); }
            catch { /* non-critical */ }
        }

        SceneNode? flangeAttachment = null;
        if (!Lfam3MinimalProbe.IsActive(cell.Name) && cell.FlangeAttachment is { } fa)
            flangeAttachment = CellEnvironmentBuilder.BuildFlangeAttachment(fa);

        var payload = new CellSwapPayload(
            cell, path, robotBaseNode, boosterNode, bedNode, toolHolder, firstTool,
            environment.EnvironmentNodes, environment.RotaryBedPivot, environment.MultiTools,
            flangeAttachment, Generation: 0);

        CellSceneCache.Store(cacheKey, payload);

        long tris = 0;
        int meshes = 0;
        void AddStats(SceneNode? n)
        {
            if (n is null) return;
            var (m, t) = SceneTriangleStats.Count(n);
            meshes += m;
            tris   += t;
        }
        AddStats(robotBaseNode);
        AddStats(boosterNode);
        AddStats(bedNode);
        AddStats(toolHolder);
        AddStats(flangeAttachment);
        foreach (var env in environment.EnvironmentNodes) AddStats(env);
        if (environment.MultiTools is { } mt)
        {
            foreach (var pair in mt.Tools.Values)
            {
                AddStats(pair.FlangeHolder);
                AddStats(pair.DockHolder);
            }
        }
        System.Console.WriteLine($"[cell] {cell.Name}: {meshes} meshes, {tris:N0} triangles (CPU)");

        var result = CellSceneCache.ClonePayload(payload);
        CellEnvironmentBuilder.RefreshPlacements(result);
        return result;
    }

    private static SceneNode BuildToolHolder(ToolCellConfig tool)
    {
        bool isGlb = tool.ModelPath.EndsWith(".glb",  StringComparison.OrdinalIgnoreCase)
                  || tool.ModelPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

        if (isGlb)
        {
            var toolRoot = GltfLoader.Load(AssetPaths.Resolve(tool.ModelPath));
            var children = toolRoot.Children.ToList();
            foreach (var child in children)
                toolRoot.RemoveChild(child);

            var holder = new SceneNode
            {
                Name           = "Tool",
                LocalTransform = Matrix4.CreateRotationY(MathF.PI / 2f),
                Selectable     = false,
            };
            foreach (var child in children)
                holder.AddChild(child);
            return holder;
        }

        var stlNode = StlLoader.Load(AssetPaths.Resolve(tool.ModelPath), "Tool");
        var stlHolder = new SceneNode
        {
            Name           = "Tool",
            LocalTransform = Matrix4.CreateScale(1f / 1000f)
                           * Matrix4.CreateRotationX(-MathF.PI / 2f)
                           * Matrix4.CreateRotationY(MathF.PI / 2f),
            Selectable     = false,
        };
        stlHolder.AddChild(stlNode);
        return stlHolder;
    }

    private static void ApplyBedMaterialTint(SceneNode root)
    {
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is not { } mesh) continue;
            if (!IsBedAluminumMesh(mesh)) continue;

            n.PendingMesh = new MeshData(
                mesh.Positions, mesh.Normals, mesh.Indices, mesh.Name,
                TintToward(mesh.BaseColor, BedLimeGreen, BedAluminumLimeTint),
                metallic: 0.85f, roughness: 0.38f);
        }
    }

    private static bool IsBedAluminumMesh(MeshData mesh)
    {
        if (mesh.Name.Contains("BaseKuka", StringComparison.OrdinalIgnoreCase))
            return false;

        if (mesh.Name.Contains("Silver", StringComparison.OrdinalIgnoreCase)
         || mesh.Name.Contains("Aluminum", StringComparison.OrdinalIgnoreCase)
         || mesh.Name.Contains("Aluminium", StringComparison.OrdinalIgnoreCase))
            return true;

        var c = mesh.BaseColor;
        if (c.X > 0.9f && c.Y > 0.9f && c.Z > 0.85f && mesh.Metallic < 0.25f)
            return false;

        return mesh.Metallic >= 0.35f;
    }

    private static Vector4 TintToward(Vector4 from, Vector4 toward, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return new Vector4(
            from.X + (toward.X - from.X) * amount,
            from.Y + (toward.Y - from.Y) * amount,
            from.Z + (toward.Z - from.Z) * amount,
            from.W);
    }
}