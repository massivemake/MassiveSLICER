using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Soft mill-area surface paint via <b>world-space vertex weights</b> (not a fragile UV atlas).
/// Works on any imported mesh (STEP/GLB/STL) without requiring good material UVs.
/// Weights are uploaded into the dedicated paint-UV vertex channel (x = weight, y = 0)
/// so material TEXCOORD_0 and PBR maps (units 4–8) are never modified.
/// </summary>
public sealed class MillSurfacePaint : IDisposable
{
    readonly Dictionary<SceneNode, Layer> _layers = new();
    bool _disposed;

    public float Coverage01
    {
        get
        {
            long painted = 0, total = 0;
            foreach (var layer in _layers.Values)
            {
                painted += layer.PaintedVertexCount;
                total   += layer.Weights.Length;
            }
            return total == 0 ? 0f : painted / (float)total;
        }
    }

    public int PaintedVertexCount
    {
        get
        {
            long n = 0;
            foreach (var layer in _layers.Values) n += layer.PaintedVertexCount;
            return (int)Math.Min(n, int.MaxValue);
        }
    }

    public bool HasPaint => PaintedVertexCount > 0;

    public Layer EnsureLayer(SceneNode meshNode)
    {
        if (_layers.TryGetValue(meshNode, out var existing))
            return existing;

        var mesh = meshNode.Mesh?.PickingData
            ?? throw new InvalidOperationException("Mesh has no GPU picking data yet.");
        _ = meshNode.Mesh
            ?? throw new InvalidOperationException("Mesh renderer not ready.");

        var layer = new Layer(meshNode, mesh);
        _layers[meshNode] = layer;
        return layer;
    }

    public bool TryGetLayer(SceneNode meshNode, out Layer layer)
        => _layers.TryGetValue(meshNode, out layer!);

    /// <summary>
    /// Soft brush stamp in <b>world millimetres</b> around <paramref name="worldHit"/>.
    /// <paramref name="falloff"/> 0 = hard disk, 1 = soft gaussian edge.
    /// Always also floods the hit triangle so a successful pick always leaves a visible mark.
    /// </summary>
    public void StampWorld(
        SceneNode meshNode,
        Vector3 worldHit,
        float radiusMm,
        float falloff,
        float strength,
        bool erase,
        int hitTriangleIndex = -1)
    {
        var layer = EnsureLayer(meshNode);
        if (hitTriangleIndex >= 0)
            layer.StampTriangle(hitTriangleIndex, erase);
        layer.StampWorld(worldHit, radiusMm, falloff, strength, erase);
    }

    /// <summary>Set all three vertices of a triangle to fully painted (Face tool).</summary>
    public void StampTriangle(SceneNode meshNode, MeshData mesh, int triangleIndex, bool erase)
    {
        _ = mesh;
        var layer = EnsureLayer(meshNode);
        layer.StampTriangle(triangleIndex, erase);
    }

    /// <summary>Stamp vertices whose world positions project inside a screen region.</summary>
    public void StampScreenRegion(
        SceneNode meshNode,
        Func<Vector3, bool> worldPointAccepted,
        float strength,
        bool erase)
    {
        var layer = EnsureLayer(meshNode);
        layer.StampWhere(worldPointAccepted, strength, erase);
    }

    public void Clear()
    {
        foreach (var layer in _layers.Values)
        {
            if (layer.MeshNode.Mesh is { } mr)
            {
                mr.SelectionMaskTex = 0;
                mr.SelectionVertexPaint = false;
            }
            layer.Dispose();
        }
        _layers.Clear();
    }

    /// <summary>Upload dirty vertex weights to GPU. GL thread only.</summary>
    public void UploadDirty()
    {
        foreach (var layer in _layers.Values)
            layer.EnsureGpuAndUpload();
    }

    public void RebindAll()
    {
        foreach (var layer in _layers.Values)
        {
            if (layer.MeshNode.Mesh is { } mr)
                mr.SelectionVertexPaint = layer.PaintedVertexCount > 0;
        }
    }

    public string Describe()
    {
        if (_layers.Count == 0) return "no paint layers";
        var parts = new List<string>();
        foreach (var (node, layer) in _layers)
            parts.Add($"{node.Name}: {layer.PaintedVertexCount}/{layer.Weights.Length} verts");
        return string.Join("; ", parts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
    }

    public sealed class Layer : IDisposable
    {
        public SceneNode MeshNode { get; }
        public MeshData Mesh { get; }
        public float[] Weights { get; }
        public int PaintedVertexCount { get; private set; }

        bool _dirty;
        bool _disposed;
        Vector2[]? _paintAttrCache;

        public Layer(SceneNode node, MeshData mesh)
        {
            MeshNode = node;
            Mesh = mesh;
            Weights = new float[mesh.Positions.Length];
            _dirty = false;
        }

        public void StampWorld(
            Vector3 worldHit, float radiusMm, float falloff, float strength, bool erase)
        {
            if (Weights.Length == 0) return;
            radiusMm = MathF.Max(0.5f, radiusMm);
            falloff = Math.Clamp(falloff, 0f, 1f);
            strength = Math.Clamp(strength, 0.05f, 1f);

            var wt = MeshNode.WorldTransform;
            float r = radiusMm;
            float r2 = r * r;
            // Hard→soft: power curve. falloff 0 → almost hard edge, 1 → broad gaussian.
            float power = 1f + falloff * 3.5f;
            float invR = 1f / r;

            var pos = Mesh.Positions;
            // Dense meshes: still full pass but cheap math — CAD parts stay interactive.
            for (int i = 0; i < pos.Length; i++)
            {
                var lp = pos[i];
                // Row-vector local → world
                float wx = lp.X * wt.M11 + lp.Y * wt.M21 + lp.Z * wt.M31 + wt.M41;
                float wy = lp.X * wt.M12 + lp.Y * wt.M22 + lp.Z * wt.M32 + wt.M42;
                float wz = lp.X * wt.M13 + lp.Y * wt.M23 + lp.Z * wt.M33 + wt.M43;
                float dx = wx - worldHit.X;
                float dy = wy - worldHit.Y;
                float dz = wz - worldHit.Z;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 > r2) continue;

                float t = MathF.Sqrt(d2) * invR; // 0 at centre, 1 at edge
                float w;
                if (falloff < 0.05f)
                    w = t < 0.92f ? strength : strength * MathF.Max(0f, 1f - (t - 0.92f) / 0.08f);
                else
                    w = MathF.Pow(MathF.Max(0f, 1f - t), power) * strength;

                ApplyWeight(i, w, erase);
            }
            _dirty = true;
        }

        public void StampIndices(IEnumerable<int> indices, float strength, bool erase)
        {
            strength = Math.Clamp(strength, 0.05f, 1f);
            foreach (var i in indices)
            {
                if ((uint)i >= (uint)Weights.Length) continue;
                ApplyWeight(i, strength, erase);
            }
            _dirty = true;
        }

        public void StampTriangle(int triangleIndex, bool erase)
        {
            if (!Picker.TryGetTriangleLocal(Mesh, triangleIndex, out _, out _, out _))
                return;
            GetTriVerts(Mesh, triangleIndex, out int i0, out int i1, out int i2);
            float w = erase ? 0f : 1f;
            if (erase)
            {
                SetWeight(i0, 0f);
                SetWeight(i1, 0f);
                SetWeight(i2, 0f);
            }
            else
            {
                ApplyWeight(i0, w, erase: false);
                ApplyWeight(i1, w, erase: false);
                ApplyWeight(i2, w, erase: false);
            }
            _dirty = true;
        }

        public void StampWhere(Func<Vector3, bool> acceptWorld, float strength, bool erase)
        {
            var wt = MeshNode.WorldTransform;
            var pos = Mesh.Positions;
            strength = Math.Clamp(strength, 0.05f, 1f);
            for (int i = 0; i < pos.Length; i++)
            {
                var lp = pos[i];
                float wx = lp.X * wt.M11 + lp.Y * wt.M21 + lp.Z * wt.M31 + wt.M41;
                float wy = lp.X * wt.M12 + lp.Y * wt.M22 + lp.Z * wt.M32 + wt.M42;
                float wz = lp.X * wt.M13 + lp.Y * wt.M23 + lp.Z * wt.M33 + wt.M43;
                if (!acceptWorld(new Vector3(wx, wy, wz))) continue;
                ApplyWeight(i, strength, erase);
            }
            _dirty = true;
        }

        void ApplyWeight(int i, float w, bool erase)
        {
            float cur = Weights[i];
            float next;
            if (erase)
                next = MathF.Max(0f, cur * (1f - w));
            else
                next = MathF.Max(cur, w);

            next = Math.Clamp(next, 0f, 1f);
            if (MathF.Abs(next - cur) < 1e-5f) return;
            if (cur < 0.02f && next >= 0.02f) PaintedVertexCount++;
            else if (cur >= 0.02f && next < 0.02f) PaintedVertexCount--;
            Weights[i] = next;
        }

        void SetWeight(int i, float next)
        {
            next = Math.Clamp(next, 0f, 1f);
            float cur = Weights[i];
            if (MathF.Abs(next - cur) < 1e-5f) return;
            if (cur < 0.02f && next >= 0.02f) PaintedVertexCount++;
            else if (cur >= 0.02f && next < 0.02f) PaintedVertexCount--;
            Weights[i] = next;
        }

        static void GetTriVerts(MeshData mesh, int tri, out int i0, out int i1, out int i2)
        {
            if (mesh.Indices is { } idx)
            {
                int i = tri * 3;
                i0 = (int)idx[i]; i1 = (int)idx[i + 1]; i2 = (int)idx[i + 2];
            }
            else
            {
                i0 = tri * 3; i1 = i0 + 1; i2 = i0 + 2;
            }
        }

        /// <summary>GL thread: push weights into paint-UV attribute and enable selection.</summary>
        public void EnsureGpuAndUpload()
        {
            if (_disposed || !_dirty) return;
            if (MeshNode.Mesh is not { } mr) return;

            _paintAttrCache ??= new Vector2[Weights.Length];
            for (int i = 0; i < Weights.Length; i++)
                _paintAttrCache[i] = new Vector2(Weights[i], 0f);

            mr.ApplyPaintUvs(_paintAttrCache);
            mr.SelectionVertexPaint = true;
            mr.SelectionTint = new Vector3(0.25f, 1.0f, 0.20f); // lime green
            mr.SelectionTintStrength = 0.88f;
            // Clear legacy texture mask path so only vertex paint drives the wash.
            mr.SelectionMaskTex = 0;
            _dirty = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (MeshNode.Mesh is { } mr)
            {
                mr.SelectionVertexPaint = false;
                mr.SelectionMaskTex = 0;
            }
        }
    }
}
