using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Reference-counted pool of <see cref="MeshRenderer"/> instances keyed by
/// <see cref="MeshData.CacheId"/>. Reuses GPU uploads when switching cells.
/// Must be called on the OpenGL thread.
/// </summary>
public static class GpuMeshCache
{
    private sealed class Entry
    {
        public required MeshRenderer Renderer { get; init; }
        public int RefCount;
    }

    private static readonly Dictionary<int, Entry> _entries = new();

    public static MeshRenderer Acquire(MeshData data)
    {
        if (!_entries.TryGetValue(data.CacheId, out var entry))
        {
            entry = new Entry { Renderer = new MeshRenderer(data), RefCount = 0 };
            _entries[data.CacheId] = entry;
        }

        entry.RefCount++;
        return entry.Renderer;
    }

    public static void Release(MeshRenderer? renderer)
    {
        if (renderer is null) return;

        foreach (var entry in _entries.Values)
        {
            if (!ReferenceEquals(entry.Renderer, renderer)) continue;
            entry.RefCount = Math.Max(0, entry.RefCount - 1);
            return;
        }
    }

    /// <summary>Disposes all pooled GPU meshes (app shutdown).</summary>
    public static void DisposeAll()
    {
        foreach (var entry in _entries.Values)
            entry.Renderer.Dispose();
        _entries.Clear();
    }

    public static void ReleaseSubtree(SceneNode root)
    {
        foreach (var n in root.SelfAndDescendants())
        {
            Release(n.Mesh);
            n.Mesh = null;
        }
    }
}