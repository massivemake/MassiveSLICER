using MassiveSlicer.Viewport;
using OpenTK.Mathematics;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class PbrMaterialSettingsTest
{
    [Fact]
    public void Matches_detects_layer_toggle_changes()
    {
        var a = new PbrMaterialSettings();
        var b = new PbrMaterialSettings();
        Assert.True(a.Matches(b));

        b.NormalMapEnabled = false;
        Assert.False(a.Matches(b));

        a.CopyFrom(b);
        Assert.True(a.Matches(b));
    }

    [Fact]
    public void Matches_detects_factor_override_changes()
    {
        var a = new PbrMaterialSettings { MetallicFactorOverride = 0.5f };
        var b = new PbrMaterialSettings();
        Assert.False(a.Matches(b));

        b.MetallicFactorOverride = 0.5f;
        Assert.True(a.Matches(b));

        b.EmissiveFactorOverride = new Vector3(1f, 0.5f, 0.2f);
        Assert.False(a.Matches(b));
    }

    [Fact]
    public void CopyFrom_clears_nullable_overrides()
    {
        var src = new PbrMaterialSettings();
        var dst = new PbrMaterialSettings
        {
            MetallicFactorOverride = 0.8f,
            EmissiveFactorOverride = new Vector3(1f, 1f, 1f),
            LayerOverlayStrength = 0.25f,
            AoMapEnabled = false,
        };

        src.CopyFrom(dst);
        Assert.Equal(0.8f, src.MetallicFactorOverride);
        Assert.Equal(new Vector3(1f, 1f, 1f), src.EmissiveFactorOverride);
        Assert.False(src.AoMapEnabled);

        dst.MetallicFactorOverride = null;
        dst.EmissiveFactorOverride = null;
        src.CopyFrom(dst);
        Assert.Null(src.MetallicFactorOverride);
        Assert.Null(src.EmissiveFactorOverride);
    }
}