using System.Text.Json;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.App.Console;

/// <summary>Serializes and applies PBR/material viewport state for <see cref="LocalControlBridge"/>.</summary>
public static class PbrMaterialBridge
{
    public static object BuildStatus(MainWindowViewModel main)
    {
        var vp = main.Viewport;
        var pbr = vp.PbrMaterial;
        var active = vp.ResolveActivePrintObjectItem();

        return new
        {
            ok = true,
            shaderMode = vp.ActiveShaderMode.ToString(),
            exposure = vp.Exposure,
            iblIntensity = vp.IblIntensity,
            lightAzimuth = vp.LightAzimuth,
            lightElevation = vp.LightElevation,
            lightIntensity = vp.LightIntensity,
            layerPreview = vp.AdditiveSettings?.ShowLayerPreview ?? false,
            activeObject = active?.Name,
            layers = new
            {
                baseColor = pbr.BaseColorMapEnabled,
                metallicRoughness = pbr.MetallicRoughnessMapEnabled,
                normal = pbr.NormalMapEnabled,
                ao = pbr.AoMapEnabled,
                emissive = pbr.EmissiveMapEnabled,
                layerOverlay = pbr.LayerOverlayEnabled,
            },
            layerOverlayStrength = pbr.LayerOverlayStrength,
            factors = new
            {
                metallic = pbr.MetallicFactorOverride,
                roughness = pbr.RoughnessFactorOverride,
                normalScale = pbr.NormalScaleOverride,
                occlusionStrength = pbr.OcclusionStrengthOverride,
                emissive = pbr.EmissiveFactorOverride is { } e
                    ? new[] { e.X, e.Y, e.Z }
                    : (float[]?)null,
            },
            availableMaps = SummarizeMaps(active?.Node),
            fileFactors = SummarizeFileFactors(active?.Node),
        };
    }

    public static void ApplyPatch(MainWindowViewModel main, JsonElement body)
    {
        var vp = main.Viewport;
        var pbr = vp.PbrMaterial;
        bool touchedPbr = false;

        if (TryGetString(body, "shaderMode", out var shaderMode)
            && Enum.TryParse<ShaderMode>(shaderMode, ignoreCase: true, out var mode))
            vp.ActiveShaderMode = mode;

        if (TryGetFloat(body, "exposure", out var exposure))
            vp.Exposure = exposure;
        if (TryGetFloat(body, "iblIntensity", out var ibl))
            vp.IblIntensity = ibl;
        if (TryGetFloat(body, "lightAzimuth", out var az))
            vp.LightAzimuth = az;
        if (TryGetFloat(body, "lightElevation", out var el))
            vp.LightElevation = el;
        if (TryGetFloat(body, "lightIntensity", out var li))
            vp.LightIntensity = li;

        if (TryGetBool(body, "layerPreview", out var layerPreview) && vp.AdditiveSettings is { } additive)
            additive.AdaptiveLayerHeight = layerPreview;

        if (body.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Object)
        {
            if (TryGetBool(layers, "baseColor", out var v)) { pbr.BaseColorMapEnabled = v; touchedPbr = true; }
            if (TryGetBool(layers, "metallicRoughness", out v)) { pbr.MetallicRoughnessMapEnabled = v; touchedPbr = true; }
            if (TryGetBool(layers, "normal", out v)) { pbr.NormalMapEnabled = v; touchedPbr = true; }
            if (TryGetBool(layers, "ao", out v)) { pbr.AoMapEnabled = v; touchedPbr = true; }
            if (TryGetBool(layers, "emissive", out v)) { pbr.EmissiveMapEnabled = v; touchedPbr = true; }
            if (TryGetBool(layers, "layerOverlay", out v)) { pbr.LayerOverlayEnabled = v; touchedPbr = true; }
        }

        if (TryGetFloat(body, "layerOverlayStrength", out var overlayStrength))
        {
            pbr.LayerOverlayStrength = Math.Clamp(overlayStrength, 0f, 1f);
            touchedPbr = true;
        }

        if (body.TryGetProperty("factors", out var factors) && factors.ValueKind == JsonValueKind.Object)
        {
            if (factors.TryGetProperty("metallic", out var metallicEl))
            {
                pbr.MetallicFactorOverride = metallicEl.ValueKind == JsonValueKind.Null
                    ? null : Math.Clamp(metallicEl.GetSingle(), 0f, 1f);
                touchedPbr = true;
            }
            if (factors.TryGetProperty("roughness", out var roughEl))
            {
                pbr.RoughnessFactorOverride = roughEl.ValueKind == JsonValueKind.Null
                    ? null : Math.Clamp(roughEl.GetSingle(), 0f, 1f);
                touchedPbr = true;
            }
            if (factors.TryGetProperty("normalScale", out var normalEl))
            {
                pbr.NormalScaleOverride = normalEl.ValueKind == JsonValueKind.Null
                    ? null : Math.Max(0f, normalEl.GetSingle());
                touchedPbr = true;
            }
            if (factors.TryGetProperty("occlusionStrength", out var aoEl))
            {
                pbr.OcclusionStrengthOverride = aoEl.ValueKind == JsonValueKind.Null
                    ? null : Math.Clamp(aoEl.GetSingle(), 0f, 1f);
                touchedPbr = true;
            }
            if (factors.TryGetProperty("emissive", out var emEl))
            {
                pbr.EmissiveFactorOverride = emEl.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.Array when emEl.GetArrayLength() >= 3 => new Vector3(
                        emEl[0].GetSingle(), emEl[1].GetSingle(), emEl[2].GetSingle()),
                    _ => pbr.EmissiveFactorOverride,
                };
                touchedPbr = true;
            }
        }

        if (touchedPbr)
            vp.NotifyPbrMaterialChanged();
    }

    private static object SummarizeMaps(SceneNode? root)
    {
        bool baseColor = false, mr = false, normal = false, ao = false, emissive = false;
        if (root is not null)
        {
            foreach (var n in root.SelfAndDescendants())
            {
                if (n.Mesh?.PickingData.Material is not { } mat) continue;
                if (mat.BaseColor is not null) baseColor = true;
                if (mat.MetallicRoughness is not null) mr = true;
                if (mat.Normal is not null) normal = true;
                if (mat.Occlusion is not null) ao = true;
                if (mat.Emissive is not null) emissive = true;
            }
        }

        return new { baseColor, metallicRoughness = mr, normal, ao, emissive };
    }

    private static object? SummarizeFileFactors(SceneNode? root)
    {
        MaterialData? mat = null;
        if (root is not null)
        {
            foreach (var n in root.SelfAndDescendants())
            {
                if (n.Mesh?.PickingData.Material is { } m) { mat = m; break; }
            }
        }

        if (mat is null) return null;
        var e = mat.EmissiveFactor;
        return new
        {
            metallic = mat.MetallicFactor,
            roughness = mat.RoughnessFactor,
            normalScale = mat.NormalScale,
            occlusionStrength = mat.OcclusionStrength,
            emissive = new[] { e.X, e.Y, e.Z },
            baseColor = new[] { mat.BaseColorFactor.X, mat.BaseColorFactor.Y, mat.BaseColorFactor.Z, mat.BaseColorFactor.W },
        };
    }

    private static bool TryGetString(JsonElement el, string name, out string value)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? "";
            return true;
        }
        value = "";
        return false;
    }

    private static bool TryGetBool(JsonElement el, string name, out bool value)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = prop.GetBoolean();
            return true;
        }
        value = false;
        return false;
    }

    private static bool TryGetFloat(JsonElement el, string name, out float value)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            value = prop.GetSingle();
            return true;
        }
        value = 0f;
        return false;
    }
}