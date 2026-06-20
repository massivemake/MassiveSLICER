using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
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
    // Converts GLTF Y-up metres -> scene Z-up millimetres.
    // Row-vector: v' = v * GltfToScene
    //   new.x =  1000 * old.x
    //   new.y = -1000 * old.z   (GLTF Z -> -Y in Z-up)
    //   new.z =  1000 * old.y   (GLTF Y -> Z in Z-up)
    private static readonly SysNum.Matrix4x4 YupToZupSys =
        SysNum.Matrix4x4.CreateRotationX(MathF.PI / 2f) *
        SysNum.Matrix4x4.CreateScale(1000f);

    private static readonly Matrix4 GltfToScene = ToMatrix4(YupToZupSys);

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
        ModelRoot model;
        try
        {
            model = ModelRoot.Load(path);
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

        // Root node applies the coordinate-system and unit conversion.
        var root = new SceneNode
        {
            Name           = Path.GetFileNameWithoutExtension(path),
            LocalTransform = GltfToScene,
        };

        var scene = model.DefaultScene;
        if (scene != null)
        {
            foreach (var child in scene.VisualChildren)
                root.AddChild(BuildNode(child));
        }

        return root;
    }

    // -- Private helpers ------------------------------------------------------

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
