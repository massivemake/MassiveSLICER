using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;
using SysNum = System.Numerics;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>
/// Loads GLB/GLTF files and returns a <see cref="SceneNode"/> hierarchy ready to be
/// queued for GPU upload on the render thread.
/// <para>
/// Coordinate conversion: GLTF is Y-up right-hand; the returned root node's
/// <see cref="SceneNode.LocalTransform"/> includes a +90deg X rotation and ×1000 scale
/// so the output is Z-up right-hand in millimetres (our scene convention).
/// Node-local transforms are preserved in the hierarchy for future FK use.
/// </para>
/// </summary>
public static class GltfLoader
{
    private static readonly Dictionary<string, (long MtimeUtcTicks, SceneNode Template)> _graphCache = new(StringComparer.OrdinalIgnoreCase);

    // Converts GLTF Y-up metres -> scene Z-up millimetres.
    // Row-vector: v' = v * GltfToScene
    //   new.x =  1000 * old.x
    //   new.y = -1000 * old.z   (GLTF Z -> -Y in Z-up)
    //   new.z =  1000 * old.y   (GLTF Y -> Z in Z-up)
    private static readonly SysNum.Matrix4x4 YupToZupSys =
        SysNum.Matrix4x4.CreateRotationX(MathF.PI / 2f) *
        SysNum.Matrix4x4.CreateScale(1000f);

    /// <summary>Y-up metres → Z-up millimetres (rotation + scale, no translation).</summary>
    public static readonly Matrix4 GltfToScene = ToMatrix4(YupToZupSys);

    /// <summary>
    /// Loads all nodes from a GLB/GLTF file and returns a root <see cref="SceneNode"/>
    /// whose hierarchy mirrors the GLTF scene graph.  The root's
    /// <see cref="SceneNode.LocalTransform"/> applies the Y-up->Z-up conversion and
    /// metre->millimetre scale; all child local transforms are the raw GLTF node matrices.
    /// </summary>
    /// <param name="path">Absolute or working-directory-relative path to the file.</param>
    /// <returns>Root scene node; add it (or a positioned wrapper) to the scene graph.</returns>
    public static SceneNode Load(string path)
    {
        var root = LoadSceneGraph(path);
        root.LocalTransform = GltfToScene;
        return root;
    }

    /// <summary>
    /// Loads a GLB in native GLTF Y-up metres (no <see cref="GltfToScene"/> on the root).
    /// Matches MassiveCONNECT <c>loadGlb</c>: auto-scales mm exports down to metres when the
    /// bbox diagonal exceeds 100.
    /// </summary>
    public static SceneNode LoadNativeMeters(string path)
    {
        var root = LoadSceneGraph(path);
        if (TryNativeMetreScale(root) is { } scale)
            root.LocalTransform = Matrix4.CreateScale(scale);
        return root;
    }

    // -- Private helpers ------------------------------------------------------

    private static readonly ReadSettings TryFixSettings  = new() { Validation = ValidationMode.TryFix };
    private static readonly ReadSettings SkipValidation = new() { Validation = ValidationMode.Skip };

    private static SceneNode LoadSceneGraph(string path)
    {
        var fullPath = Path.GetFullPath(path);
        long mtime = File.GetLastWriteTimeUtc(fullPath).Ticks;
        if (_graphCache.TryGetValue(fullPath, out var cached) && cached.MtimeUtcTicks == mtime)
            return SceneNodeClone.DeepClone(cached.Template);

        ModelRoot model;
        try
        {
            model = LoadModelRoot(fullPath);
        }
        catch (Exception ex) when (ex.Message.Contains("draco", StringComparison.OrdinalIgnoreCase)
                                || ex.Message.Contains("KHR_draco", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "This file uses Draco mesh compression (KHR_draco_mesh_compression), " +
                "which is not supported by the loader.\n\n" +
                "To fix, re-export without Draco compression:\n" +
                "  • Blender: File > Export > glTF 2.0 > uncheck \"Draco mesh compression\"\n" +
                "  • Other tools: disable the Draco/compression option in export settings", ex);
        }
        catch (Exception ex) when (ex.Message.Contains("meshopt", StringComparison.OrdinalIgnoreCase)
                                || ex.Message.Contains("EXT_meshopt", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "This GLB uses EXT_meshopt_compression but runtime decode failed. " +
                "Fallback: scripts/repair-lfam3-glbs.ps1 or npx @gltf-transform/cli copy in.glb out.glb", ex);
        }

        var root = new SceneNode
        {
            Name           = Path.GetFileNameWithoutExtension(fullPath),
            LocalTransform = Matrix4.Identity,
        };

        var scene = model.DefaultScene;
        if (scene != null)
        {
            foreach (var child in scene.VisualChildren)
                root.AddChild(BuildNode(child));
        }

        _graphCache[fullPath] = (mtime, SceneNodeClone.DeepClone(root));
        return SceneNodeClone.DeepClone(root);
    }

    /// <summary>
    /// MassiveCONNECT exports from glTF-Transform can fail strict validation; retry leniently.
    /// </summary>
    private static ModelRoot LoadModelRoot(string path)
    {
        var loadPath = GlbRepair.EnsureLoadable(
            GlbMeshoptDecoder.EnsureDecoded(AssetLocalCache.EnsureLocal(path)));
        try
        {
            return LoadModelRootCore(loadPath);
        }
        finally
        {
            if (IsTempRepairFile(loadPath, path))
            {
                try { File.Delete(loadPath); } catch { /* temp cleanup */ }
            }
        }
    }

    private static bool IsTempRepairFile(string loadPath, string originalPath)
    {
        if (string.Equals(loadPath, originalPath, StringComparison.OrdinalIgnoreCase))
            return false;

        var name = Path.GetFileName(loadPath);
        return loadPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
            && name.StartsWith("mslicer-glb-", StringComparison.Ordinal);
    }

    private static ModelRoot LoadModelRootCore(string path)
    {
        try
        {
            return ModelRoot.Load(path);
        }
        catch (SchemaException)
        {
            try { return ModelRoot.Load(path, TryFixSettings); }
            catch (SchemaException) { return ModelRoot.Load(path, SkipValidation); }
        }
    }

    private static float? TryNativeMetreScale(SceneNode root)
    {
        var (min, max) = ComputeNativeAabb(root);
        if (min.X > max.X) return null;

        var diag = (max - min).Length;
        return diag > 100f ? 0.001f : null;
    }

    private static (Vector3 Min, Vector3 Max) ComputeNativeAabb(SceneNode root)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        Span<Vector3> corners = stackalloc Vector3[8];

        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is not { } mesh) continue;
            var world        = n.WorldTransform;
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

    private static SceneNode BuildNode(Node gltfNode)
    {
        var node = new SceneNode
        {
            Name           = gltfNode.Name ?? "node",
            LocalTransform = ToMatrix4(gltfNode.LocalMatrix),
        };

        if (gltfNode.Mesh is not null)
        {
            foreach (var prim in gltfNode.Mesh.Primitives)
            {
                var nodeLabel = gltfNode.Name ?? gltfNode.Mesh.Name ?? "Mesh";
                var data = ExtractPrimitive(prim, nodeLabel);
                if (data is not null)
                    node.AddChild(new SceneNode { Name = data.Name, PendingMesh = data });
            }
        }

        foreach (var child in gltfNode.VisualChildren)
            node.AddChild(BuildNode(child));

        return node;
    }

    private static MeshData? ExtractPrimitive(MeshPrimitive prim, string name)
    {
        var posAccessor = prim.GetVertexAccessor("POSITION");
        if (posAccessor is null) return null;

        // Vertex positions are in the node's local space -- transforms are handled
        // by the SceneNode hierarchy, not baked here.
        var rawPositions = posAccessor.AsVector3Array();
        var positions    = new Vector3[rawPositions.Count];
        for (int i = 0; i < rawPositions.Count; i++)
            positions[i] = ToOpenTK(rawPositions[i]);

        Vector3[] normals;
        var normAccessor = prim.GetVertexAccessor("NORMAL");
        if (normAccessor is not null)
        {
            var rawNormals = normAccessor.AsVector3Array();
            normals = new Vector3[rawNormals.Count];
            for (int i = 0; i < rawNormals.Count; i++)
                normals[i] = ToOpenTK(rawNormals[i]);
        }
        else
        {
            normals = ComputeFlatNormals(positions, null);
        }

        uint[]? indices = null;
        var indexAccessor = prim.GetIndexAccessor();
        if (indexAccessor is not null)
        {
            var raw = indexAccessor.AsIndicesArray();
            indices = new uint[raw.Count];
            for (int i = 0; i < raw.Count; i++)
                indices[i] = raw[i];
        }

        if (normAccessor is null && indices is not null)
            normals = ComputeFlatNormals(positions, indices);

        // Extract PBR material properties.
        Vector4? baseColor = null;
        float metallic  = 0f;
        float roughness = 0.5f;

        var mat = prim.Material;
        if (mat?.Name is { Length: > 0 } matName)
            name = $"{name}__{matName}";

        if (mat is not null)
        {
            var bcCh = mat.FindChannel("BaseColor");
            if (bcCh.HasValue)
            {
#pragma warning disable CS0618
                var p = bcCh.Value.Parameter;
#pragma warning restore CS0618
                baseColor = new Vector4(p.X, p.Y, p.Z, p.W);
            }

            var mrCh = mat.FindChannel("MetallicRoughness");
            if (mrCh.HasValue)
            {
                var parms = mrCh.Value.Parameters;
                if (parms.Count >= 1 && parms[0].Value is float mf) metallic  = mf;
                if (parms.Count >= 2 && parms[1].Value is float rf) roughness = rf;
            }
        }

        return new MeshData(positions, normals, indices, name, baseColor, metallic, roughness);
    }

    private static Matrix4 ToMatrix4(SysNum.Matrix4x4 m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);

    private static Vector3 ToOpenTK(SysNum.Vector3 v) => new(v.X, v.Y, v.Z);

    private static Vector3[] ComputeFlatNormals(Vector3[] positions, uint[]? indices)
    {
        var normals = new Vector3[positions.Length];

        if (indices is not null)
        {
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = (int)indices[i], b = (int)indices[i + 1], c = (int)indices[i + 2];
                var n = Vector3.Normalize(Vector3.Cross(positions[b] - positions[a],
                                                        positions[c] - positions[a]));
                normals[a] += n;
                normals[b] += n;
                normals[c] += n;
            }
        }
        else
        {
            for (int i = 0; i + 2 < positions.Length; i += 3)
            {
                var n = Vector3.Normalize(Vector3.Cross(positions[i + 1] - positions[i],
                                                        positions[i + 2] - positions[i]));
                normals[i] = normals[i + 1] = normals[i + 2] = n;
            }
        }

        for (int i = 0; i < normals.Length; i++)
            if (normals[i].LengthSquared > 0.001f)
                normals[i] = Vector3.Normalize(normals[i]);

        return normals;
    }
}
