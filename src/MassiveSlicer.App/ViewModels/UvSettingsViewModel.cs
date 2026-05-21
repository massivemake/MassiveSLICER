using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Manages the UV viewer panel: which UV channel is displayed and
/// which texture maps (displacement, normal, diffuse) are currently loaded.
/// </summary>
public sealed class UvSettingsViewModel : ViewModelBase
{
    private int _uvChannel;

    /// <summary>UV channel index displayed in the viewer (0 = UV0, 1 = UV1).</summary>
    public int UvChannel
    {
        get => _uvChannel;
        set => SetField(ref _uvChannel, value);
    }

    private bool _hasDisplacementMap;

    /// <summary>Whether a displacement texture is currently loaded.</summary>
    public bool HasDisplacementMap
    {
        get => _hasDisplacementMap;
        set => SetField(ref _hasDisplacementMap, value);
    }

    private bool _hasNormalMap;

    /// <summary>Whether a normal map texture is currently loaded.</summary>
    public bool HasNormalMap
    {
        get => _hasNormalMap;
        set => SetField(ref _hasNormalMap, value);
    }

    private bool _hasDiffuseMap;

    /// <summary>Whether a diffuse/colour texture is currently loaded.</summary>
    public bool HasDiffuseMap
    {
        get => _hasDiffuseMap;
        set => SetField(ref _hasDiffuseMap, value);
    }
}
