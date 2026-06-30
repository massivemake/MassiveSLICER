using System.Text.Json;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class AppPreferencesTest
{
    [Fact]
    public void Serialize_round_trips_adaptive_and_overhang_settings()
    {
        var prefs = new AppPreferences
        {
            AdaptiveLayerHeight = true,
            AdaptiveQuality     = 0.25,
            MinLayerHeight      = 1.5,
            OverhangOrientation = true,
            MaxOverhangTiltDeg  = 60,
            DisableContourOffset = true,
            SeamMode            = "Zig-zag",
            InfillPattern       = "Grid",
            WaveEffect          = "Sine",
        };

        var json = JsonSerializer.Serialize(prefs);
        var loaded = JsonSerializer.Deserialize<AppPreferences>(json);

        Assert.NotNull(loaded);
        Assert.True(loaded.AdaptiveLayerHeight);
        Assert.Equal(0.25, loaded.AdaptiveQuality);
        Assert.Equal(1.5, loaded.MinLayerHeight);
        Assert.True(loaded.OverhangOrientation);
        Assert.Equal(60, loaded.MaxOverhangTiltDeg);
        Assert.True(loaded.DisableContourOffset);
        Assert.Equal("Zig-zag", loaded.SeamMode);
        Assert.Equal("Grid", loaded.InfillPattern);
        Assert.Equal("Sine", loaded.WaveEffect);
    }
}