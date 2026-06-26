using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>Summary of geometry and material data preserved when importing a GLB.</summary>
public sealed class GltfImportReport
{
    public required string SourcePath { get; init; }
    public int MeshCount { get; init; }
    public int VertexCount { get; init; }
    public int TriangleCount { get; init; }
    public IReadOnlyList<GltfMeshMaterialSummary> Meshes { get; init; } = [];
    public IReadOnlyList<string> EmbeddedTextures { get; init; } = [];
    public IReadOnlyList<string> MaterialTextureChannels { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IEnumerable<string> ToLogLines()
    {
        yield return $"[import] {Path.GetFileName(SourcePath)}: {MeshCount} mesh(es), {VertexCount:N0} verts, {TriangleCount:N0} tris";
        foreach (var mesh in Meshes)
            yield return $"  {mesh.Name}: base=({mesh.BaseColor.X:F2},{mesh.BaseColor.Y:F2},{mesh.BaseColor.Z:F2},{mesh.BaseColor.W:F2}) metal={mesh.Metallic:F2} rough={mesh.Roughness:F2} uvs={mesh.HasUvs}";

        if (MaterialTextureChannels.Count > 0)
            yield return $"  glTF texture channels: {string.Join(", ", MaterialTextureChannels)}";

        if (EmbeddedTextures.Count > 0)
            yield return $"  embedded images: {EmbeddedTextures.Count} ({string.Join(", ", EmbeddedTextures.Take(4))}{(EmbeddedTextures.Count > 4 ? ", …" : "")})";

        foreach (var warning in Warnings)
            yield return $"  ! {warning}";
    }
}

public sealed class GltfMeshMaterialSummary
{
    public required string Name { get; init; }
    public Vector4 BaseColor { get; init; }
    public float Metallic { get; init; }
    public float Roughness { get; init; }
    public bool HasUvs { get; init; }
}

/// <summary>Inspects GLB files for geometry and material data used by the viewport.</summary>
public static class GltfImportInspector
{
    public static GltfImportReport InspectFile(string path)
    {
        path = Path.GetFullPath(path);
        var node = GltfLoader.Load(path);
        return InspectLoaded(node, path);
    }

    public static GltfImportReport InspectLoaded(SceneNode root, string path)
    {
        path = Path.GetFullPath(path);
        var scene = SummarizeScene(root, path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ".glb" and not ".gltf")
            return scene;

        var raw = InspectRawGltf(path);
        var warnings = new List<string>(raw.Warnings);
        if (raw.MaterialTextureChannels.Count > 0)
            warnings.Add("Texture maps are present in the file but are not yet rendered — only scalar PBR factors are applied.");

        return new GltfImportReport
        {
            SourcePath = scene.SourcePath,
            MeshCount = scene.MeshCount,
            VertexCount = scene.VertexCount,
            TriangleCount = scene.TriangleCount,
            Meshes = scene.Meshes,
            EmbeddedTextures = raw.EmbeddedTextures,
            MaterialTextureChannels = raw.MaterialTextureChannels,
            Warnings = warnings,
        };
    }

    public static GltfImportReport SummarizeScene(SceneNode root, string sourcePath)
    {
        var meshes = new List<GltfMeshMaterialSummary>();
        int verts = 0;
        int tris = 0;

        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is not { } mesh) continue;

            verts += mesh.Positions.Length;
            tris += mesh.Indices is { Length: > 0 } idx
                ? idx.Length / 3
                : mesh.Positions.Length / 3;

            meshes.Add(new GltfMeshMaterialSummary
            {
                Name = mesh.Name,
                BaseColor = mesh.BaseColor,
                Metallic = mesh.Metallic,
                Roughness = mesh.Roughness,
                HasUvs = false,
            });
        }

        return new GltfImportReport
        {
            SourcePath = sourcePath,
            MeshCount = meshes.Count,
            VertexCount = verts,
            TriangleCount = tris,
            Meshes = meshes,
        };
    }

    private static RawGltfInfo InspectRawGltf(string path)
    {
        var textures = new List<string>();
        var channels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        try
        {
            var model = ModelRoot.Load(path);
            for (int i = 0; i < model.LogicalImages.Count; i++)
            {
                var image = model.LogicalImages[i];
                var label = image.Name;
                if (string.IsNullOrWhiteSpace(label))
                    label = $"image_{i}";
                textures.Add(label);
            }

            string[] channelNames =
            [
                "BaseColor", "MetallicRoughness", "Normal", "Emissive", "Occlusion",
                "Specular", "ClearCoat", "ClearCoatRoughness", "ClearCoatNormal",
                "Transmission", "Thickness",
            ];

            foreach (var mat in model.LogicalMaterials)
            {
                foreach (var name in channelNames)
                {
                    var ch = mat.FindChannel(name);
                    if (ch.HasValue && ch.Value.Texture is not null)
                        channels.Add(name);
                }
            }

            bool anyUvs = false;
            foreach (var mesh in model.LogicalMeshes)
            {
                foreach (var prim in mesh.Primitives)
                {
                    if (prim.GetVertexAccessor("TEXCOORD_0") is not null)
                        anyUvs = true;
                }
            }

            if (anyUvs && channels.Count > 0)
                warnings.Add("UV coordinates detected — required for future normal/displacement map support.");
        }
        catch (Exception ex)
        {
            warnings.Add($"Raw glTF inspection failed: {ex.Message}");
        }

        return new RawGltfInfo(
            textures,
            channels.OrderBy(c => c).ToList(),
            warnings);
    }

    private sealed record RawGltfInfo(
        List<string> EmbeddedTextures,
        List<string> MaterialTextureChannels,
        List<string> Warnings);
}