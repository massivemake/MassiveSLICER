using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;
using StbImageSharp;
using SysNum = System.Numerics;
using AlphaMode = MassiveSlicer.Viewport.Scene.AlphaMode;

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
            // Per-load image cache: a glTF image shared by several materials decodes once
            // and yields a single TextureData (one GPU upload). Keyed by Image.LogicalIndex.
            var textureCache = new Dictionary<int, TextureData>();
            foreach (var child in scene.VisualChildren)
                root.AddChild(BuildNode(child, textureCache));
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

    private static SceneNode BuildNode(Node gltfNode, Dictionary<int, TextureData> textureCache)
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
                var data = ExtractPrimitive(prim, nodeLabel, textureCache);
                if (data is not null)
                {
                    var meshNode = new SceneNode { Name = data.Name, PendingMesh = data };
                    // Double-sided materials must not back-face cull.
                    if (data.Material?.DoubleSided == true)
                        meshNode.CullFaces = false;
                    node.AddChild(meshNode);
                }
            }
        }

        foreach (var child in gltfNode.VisualChildren)
            node.AddChild(BuildNode(child, textureCache));

        return node;
    }

    private static MeshData? ExtractPrimitive(MeshPrimitive prim, string name,
                                              Dictionary<int, TextureData> textureCache)
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

        // UV0 (TEXCOORD_0) -- required for any texture sampling.
        Vector2[]? uvs = null;
        var uvAccessor = prim.GetVertexAccessor("TEXCOORD_0");
        if (uvAccessor is not null)
        {
            var rawUv = uvAccessor.AsVector2Array();
            uvs = new Vector2[rawUv.Count];
            for (int i = 0; i < rawUv.Count; i++)
                uvs[i] = new Vector2(rawUv[i].X, rawUv[i].Y);
        }

        // Tangents (xyz + w handedness) for normal mapping; compute from UVs when absent.
        Vector4[]? tangents = null;
        var tanAccessor = prim.GetVertexAccessor("TANGENT");
        if (tanAccessor is not null)
        {
            var rawTan = tanAccessor.AsVector4Array();
            tangents = new Vector4[rawTan.Count];
            for (int i = 0; i < rawTan.Count; i++)
                tangents[i] = new Vector4(rawTan[i].X, rawTan[i].Y, rawTan[i].Z, rawTan[i].W);
        }
        else if (uvs is not null)
        {
            tangents = ComputeTangents(positions, normals, uvs, indices);
        }

        // Extract PBR material properties.
        Vector4? baseColor = null;
        float metallic  = 0f;
        float roughness = 0.5f;

        var mat = prim.Material;
        if (mat?.Name is { Length: > 0 } matName)
            name = $"{name}__{matName}";

        MaterialData? material = null;
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

            material = ExtractMaterial(mat, baseColor ?? Vector4.One, metallic, roughness, textureCache);
        }

        return new MeshData(positions, normals, indices, name,
                            baseColor, metallic, roughness, uvs, tangents, material);
    }

    // -- Material / texture extraction ----------------------------------------

    private static MaterialData ExtractMaterial(
        Material mat, Vector4 baseColorFactor, float metallic, float roughness,
        Dictionary<int, TextureData> textureCache)
    {
        var emissive = Vector3.Zero;
        var emCh = mat.FindChannel("Emissive");
        if (emCh.HasValue)
        {
#pragma warning disable CS0618
            var e = emCh.Value.Parameter;
#pragma warning restore CS0618
            emissive = new Vector3(e.X, e.Y, e.Z);
        }

        var alphaMode = mat.Alpha switch
        {
            SharpGLTF.Schema2.AlphaMode.MASK  => AlphaMode.Mask,
            SharpGLTF.Schema2.AlphaMode.BLEND => AlphaMode.Blend,
            _                                  => AlphaMode.Opaque,
        };

        return new MaterialData
        {
            BaseColorFactor   = baseColorFactor,
            MetallicFactor    = metallic,
            RoughnessFactor   = roughness,
            EmissiveFactor    = emissive,
            NormalScale       = ChannelFloat(mat, "Normal",    "Scale",    1f),
            OcclusionStrength = ChannelFloat(mat, "Occlusion", "Strength", 1f),
            AlphaMode         = alphaMode,
            AlphaCutoff       = mat.AlphaCutoff,
            DoubleSided       = mat.DoubleSided,
            BaseColor         = DecodeChannelTexture(mat, "BaseColor",         srgb: true,  textureCache),
            MetallicRoughness = DecodeChannelTexture(mat, "MetallicRoughness", srgb: false, textureCache),
            Normal            = DecodeChannelTexture(mat, "Normal",            srgb: false, textureCache),
            Occlusion         = DecodeChannelTexture(mat, "Occlusion",         srgb: false, textureCache),
            Emissive          = DecodeChannelTexture(mat, "Emissive",          srgb: true,  textureCache),
        };
    }

    /// <summary>Reads a named float parameter from a material channel (e.g. normal Scale,
    /// occlusion Strength), matching by substring; returns <paramref name="def"/> if absent.</summary>
    private static float ChannelFloat(Material mat, string channel, string paramContains, float def)
    {
        var ch = mat.FindChannel(channel);
        if (!ch.HasValue) return def;
        foreach (var p in ch.Value.Parameters)
            if (p.Name.ToString()?.Contains(paramContains, StringComparison.OrdinalIgnoreCase) == true
                && p.Value is float f)
                return f;
        return def;
    }

    private static TextureData? DecodeChannelTexture(
        Material mat, string channel, bool srgb, Dictionary<int, TextureData> textureCache)
    {
        var ch = mat.FindChannel(channel);
        if (ch?.Texture?.PrimaryImage is not { } image) return null;

        int key = image.LogicalIndex;
        if (textureCache.TryGetValue(key, out var cached))
            return cached;

        var content = image.Content;          // SharpGLTF MemoryImage (encoded PNG/JPEG bytes)
        var bytes   = content.Content;          // ReadOnlyMemory<byte>
        if (bytes.Length == 0) return null;

        var (wrapS, wrapT) = MapWrap(ch.Value.Texture?.Sampler);
        var decoded = DecodeImage(bytes.ToArray(), srgb, wrapS, wrapT);
        if (decoded is not null)
            textureCache[key] = decoded;
        return decoded;
    }

    private static TextureData? DecodeImage(byte[] encoded, bool srgb,
                                            TextureWrapKind wrapS, TextureWrapKind wrapT)
    {
        try
        {
            var img = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha);
            if (img is null || img.Width <= 0 || img.Height <= 0) return null;
            return new TextureData(img.Data, img.Width, img.Height, srgb, wrapS, wrapT);
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[gltf] texture decode failed: {ex.Message}");
            return null;
        }
    }

    private static (TextureWrapKind S, TextureWrapKind T) MapWrap(TextureSampler? sampler)
    {
        static TextureWrapKind Map(TextureWrapMode m) => m switch
        {
            TextureWrapMode.CLAMP_TO_EDGE   => TextureWrapKind.ClampToEdge,
            TextureWrapMode.MIRRORED_REPEAT => TextureWrapKind.MirroredRepeat,
            _                                => TextureWrapKind.Repeat,
        };
        return sampler is null
            ? (TextureWrapKind.Repeat, TextureWrapKind.Repeat)
            : (Map(sampler.WrapS), Map(sampler.WrapT));
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

    /// <summary>
    /// Per-vertex tangents (xyz + w handedness) via the Lengyel algorithm, used when the glTF
    /// has UVs but no TANGENT attribute. Accumulates per-triangle tangent/bitangent from
    /// position+UV deltas, then Gram-Schmidt orthonormalises against the vertex normal.
    /// </summary>
    private static Vector4[] ComputeTangents(Vector3[] pos, Vector3[] nrm, Vector2[] uv, uint[]? indices)
    {
        int n = pos.Length;
        var tan1 = new Vector3[n];
        var tan2 = new Vector3[n];

        void Accumulate(int i0, int i1, int i2)
        {
            if (i0 >= n || i1 >= n || i2 >= n) return;
            var e1 = pos[i1] - pos[i0];
            var e2 = pos[i2] - pos[i0];
            var d1 = uv[i1] - uv[i0];
            var d2 = uv[i2] - uv[i0];
            float denom = d1.X * d2.Y - d2.X * d1.Y;
            if (MathF.Abs(denom) < 1e-9f) return;
            float r = 1f / denom;
            var sdir = (e1 * d2.Y - e2 * d1.Y) * r;
            var tdir = (e2 * d1.X - e1 * d2.X) * r;
            tan1[i0] += sdir; tan1[i1] += sdir; tan1[i2] += sdir;
            tan2[i0] += tdir; tan2[i1] += tdir; tan2[i2] += tdir;
        }

        if (indices is not null)
            for (int i = 0; i + 2 < indices.Length; i += 3)
                Accumulate((int)indices[i], (int)indices[i + 1], (int)indices[i + 2]);
        else
            for (int i = 0; i + 2 < pos.Length; i += 3)
                Accumulate(i, i + 1, i + 2);

        var result = new Vector4[n];
        for (int i = 0; i < n; i++)
        {
            var nv = nrm[i];
            var t  = tan1[i];
            var tang = t - nv * Vector3.Dot(nv, t);               // Gram-Schmidt
            tang = tang.LengthSquared > 1e-12f ? Vector3.Normalize(tang) : new Vector3(1f, 0f, 0f);
            float w = Vector3.Dot(Vector3.Cross(nv, t), tan2[i]) < 0f ? -1f : 1f;
            result[i] = new Vector4(tang, w);
        }
        return result;
    }
}
