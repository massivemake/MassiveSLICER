using MassiveSlicer.Core.IO;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.App;

/// <summary>Caches pre-built cell swap payloads keyed by cell JSON path and mtime.</summary>
internal static class CellSceneCache
{
    private static readonly Dictionary<string, (long MtimeUtcTicks, CellSwapPayload Template)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bump when stand prep / env build changes so cached cells rebuild.</summary>
    private const int GeometryVersion = 14;

    public static string CacheKey(string cellPath)
    {
        var full = Path.GetFullPath(cellPath);
        if (!File.Exists(full))
            return $"{full}|0|0||v{GeometryVersion}";

        var fi = new FileInfo(full);
        string assets = "";
        try { assets = CellAssetPaths.AssetFingerprint(CellLoader.Load(full)); }
        catch { /* malformed JSON — key still tracks the file itself */ }

        return $"{full}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}|{assets}|v{GeometryVersion}";
    }

    /// <summary>
    /// Drop cached cell geometry and force all referenced GLB/STL assets to reload from disk.
    /// Used by <c>reload-cell</c> and dev-mode saves.
    /// </summary>
    public static int Invalidate(string cellPath)
    {
        var full = Path.GetFullPath(cellPath);
        var prefix = full + "|";
        foreach (var key in _cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            _cache.Remove(key);

        int count = 0;
        try
        {
            var cell = CellLoader.Load(full);
            foreach (var resolved in CellAssetPaths.ExistingResolvedPaths(cell))
            {
                var ext = Path.GetExtension(resolved);
                if (ext.Equals(".glb", StringComparison.OrdinalIgnoreCase)
                 || ext.Equals(".gltf", StringComparison.OrdinalIgnoreCase))
                    GltfLoader.InvalidateAsset(resolved);
                else
                    GltfLoader.Invalidate(resolved);

                count++;
            }
        }
        catch { /* cell JSON unreadable — scene cache still cleared */ }

        return count;
    }

    public static bool TryGet(string cacheKey, out CellSwapPayload template)
    {
        if (_cache.TryGetValue(cacheKey, out var entry))
        {
            template = entry.Template;
            return true;
        }

        template = null!;
        return false;
    }

    public static void Store(string cacheKey, CellSwapPayload payload)
        => _cache[cacheKey] = (0, ClonePayload(payload));

    public static CellSwapPayload ClonePayload(CellSwapPayload src)
    {
        var env = new List<SceneNode>(src.EnvironmentNodes.Count);
        foreach (var node in src.EnvironmentNodes)
            env.Add(SceneNodeClone.DeepClone(node));

        // The rotary-bed pivot ("RotaryBed_Top") is a descendant of one of the EnvironmentNodes
        // (the "RotaryBed" root), which we just cloned into `env`. It MUST be the SAME instance that
        // lives in the cloned graph — otherwise rotating it on E1 sync spins a detached orphan and
        // the visible top never turns. Resolve it inside `env` instead of cloning it separately.
        SceneNode? rotaryPivot = src.RotaryBedPivot is null
            ? null
            : env.SelectMany(n => n.SelfAndDescendants())
                 .FirstOrDefault(d => d.Name == src.RotaryBedPivot.Name)
              ?? SceneNodeClone.DeepClone(src.RotaryBedPivot);

        CellEnvironmentBuilder.CellMultiToolSet? multi = null;
        if (src.MultiTools is { } mt)
        {
            var tools = new Dictionary<string, CellEnvironmentBuilder.ToolVisualPair>(StringComparer.Ordinal);
            foreach (var (name, pair) in mt.Tools)
            {
                tools[name] = new CellEnvironmentBuilder.ToolVisualPair(
                    SceneNodeClone.DeepClone(pair.FlangeHolder),
                    pair.DockHolder is null ? null : SceneNodeClone.DeepClone(pair.DockHolder));
            }

            multi = new CellEnvironmentBuilder.CellMultiToolSet
            {
                Tools           = tools,
                DefaultToolName = mt.DefaultToolName,
                MountedToolName = mt.MountedToolName,
            };
        }

        return new CellSwapPayload(
            src.Config,
            src.CellPath,
            CloneOpt(src.RobotBaseNode),
            CloneOpt(src.BoosterNode),
            CloneOpt(src.BedNode),
            CloneOpt(src.ToolHolder),
            src.FirstTool,
            env,
            rotaryPivot,
            multi,
            CloneOpt(src.FlangeAttachment));
    }

    private static SceneNode? CloneOpt(SceneNode? node)
        => node is null ? null : SceneNodeClone.DeepClone(node);
}