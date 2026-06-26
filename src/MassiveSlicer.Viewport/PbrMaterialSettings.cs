using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport;

/// <summary>
/// Global PBR viewport controls: per-map layer toggles, layer-overlay compositing, and optional
/// factor overrides applied to user-selectable meshes in <see cref="ShaderMode.Standard"/>.
/// </summary>
public sealed class PbrMaterialSettings
{
    public bool BaseColorMapEnabled { get; set; } = true;
    public bool MetallicRoughnessMapEnabled { get; set; } = true;
    public bool NormalMapEnabled { get; set; } = true;
    public bool AoMapEnabled { get; set; } = true;
    public bool EmissiveMapEnabled { get; set; } = true;
    public bool LayerOverlayEnabled { get; set; } = true;
    public float LayerOverlayStrength { get; set; } = 0.62f;

    public float? MetallicFactorOverride { get; set; }
    public float? RoughnessFactorOverride { get; set; }
    public float? NormalScaleOverride { get; set; }
    public float? OcclusionStrengthOverride { get; set; }
    public Vector3? EmissiveFactorOverride { get; set; }

    public void CopyFrom(PbrMaterialSettings other)
    {
        BaseColorMapEnabled            = other.BaseColorMapEnabled;
        MetallicRoughnessMapEnabled    = other.MetallicRoughnessMapEnabled;
        NormalMapEnabled               = other.NormalMapEnabled;
        AoMapEnabled                   = other.AoMapEnabled;
        EmissiveMapEnabled             = other.EmissiveMapEnabled;
        LayerOverlayEnabled            = other.LayerOverlayEnabled;
        LayerOverlayStrength           = other.LayerOverlayStrength;
        MetallicFactorOverride         = other.MetallicFactorOverride;
        RoughnessFactorOverride        = other.RoughnessFactorOverride;
        NormalScaleOverride            = other.NormalScaleOverride;
        OcclusionStrengthOverride      = other.OcclusionStrengthOverride;
        EmissiveFactorOverride         = other.EmissiveFactorOverride;
    }

    public bool Matches(PbrMaterialSettings other) =>
        BaseColorMapEnabled         == other.BaseColorMapEnabled
        && MetallicRoughnessMapEnabled == other.MetallicRoughnessMapEnabled
        && NormalMapEnabled         == other.NormalMapEnabled
        && AoMapEnabled             == other.AoMapEnabled
        && EmissiveMapEnabled       == other.EmissiveMapEnabled
        && LayerOverlayEnabled      == other.LayerOverlayEnabled
        && MathF.Abs(LayerOverlayStrength - other.LayerOverlayStrength) < 0.0001f
        && MetallicFactorOverride.Equals(other.MetallicFactorOverride)
        && RoughnessFactorOverride.Equals(other.RoughnessFactorOverride)
        && NormalScaleOverride.Equals(other.NormalScaleOverride)
        && OcclusionStrengthOverride.Equals(other.OcclusionStrengthOverride)
        && EmissiveFactorOverride.Equals(other.EmissiveFactorOverride);
}