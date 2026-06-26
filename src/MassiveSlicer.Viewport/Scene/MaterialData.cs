using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>glTF alpha rendering mode.</summary>
public enum AlphaMode
{
    Opaque,
    /// <summary>Alpha-tested: fragments below <see cref="MaterialData.AlphaCutoff"/> are discarded.</summary>
    Mask,
    /// <summary>Alpha-blended (full transparency). v1 renders this like <see cref="Opaque"/> until sorting lands.</summary>
    Blend,
}

/// <summary>
/// Metallic-roughness PBR material: scalar/vector factors plus optional per-channel textures.
/// Shared immutably by reference off <see cref="MeshData.Material"/>. Designed to be reusable
/// beyond imported meshes (toolpath/printed-surface materials, slicer queries) — keep it free
/// of GL/render concerns.
/// </summary>
public sealed class MaterialData
{
    /// <summary>Base colour factor, sRGB, RGBA (multiplies <see cref="BaseColor"/> texture). Default opaque white.</summary>
    public Vector4 BaseColorFactor { get; init; } = Vector4.One;

    /// <summary>Metallic factor 0..1 (multiplies the B channel of <see cref="MetallicRoughness"/>).</summary>
    public float MetallicFactor { get; init; }

    /// <summary>Roughness factor 0..1 (multiplies the G channel of <see cref="MetallicRoughness"/>). Default 1.</summary>
    public float RoughnessFactor { get; init; } = 1f;

    /// <summary>Emissive colour factor, linear RGB (multiplies <see cref="Emissive"/> texture).</summary>
    public Vector3 EmissiveFactor { get; init; } = Vector3.Zero;

    /// <summary>Tangent-space normal map XY scale. Default 1.</summary>
    public float NormalScale { get; init; } = 1f;

    /// <summary>Occlusion strength: 0 = ignore AO map, 1 = full. Default 1.</summary>
    public float OcclusionStrength { get; init; } = 1f;

    public AlphaMode AlphaMode { get; init; } = AlphaMode.Opaque;
    public float AlphaCutoff { get; init; } = 0.5f;
    public bool DoubleSided { get; init; }

    /// <summary>Base colour map (sRGB). Null when the material uses a flat colour factor only.</summary>
    public TextureData? BaseColor { get; init; }

    /// <summary>Packed metallic-roughness map (linear): G = roughness, B = metallic (glTF convention).</summary>
    public TextureData? MetallicRoughness { get; init; }

    /// <summary>Tangent-space normal map (linear).</summary>
    public TextureData? Normal { get; init; }

    /// <summary>Ambient-occlusion map (linear); R channel.</summary>
    public TextureData? Occlusion { get; init; }

    /// <summary>Emissive map (sRGB).</summary>
    public TextureData? Emissive { get; init; }

    /// <summary>True when at least one texture channel is present.</summary>
    public bool HasAnyTexture =>
        BaseColor is not null || MetallicRoughness is not null || Normal is not null
        || Occlusion is not null || Emissive is not null;
}
