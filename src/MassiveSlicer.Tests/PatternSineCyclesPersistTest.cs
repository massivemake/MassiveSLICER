using System.Text.Json;
using System.Text.Json.Serialization;
using MassiveSlicer.Core.Models;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Curtain_SineWave LFAM2→LFAM3: PatternSineCyclesPerLayer 180 vanished from the
/// LFAM3 .mass (WhenWritingDefault drops 0, so a clobbered VM saves as "not set").
/// 180 must always round-trip; 0 must be written explicitly so it cannot be confused
/// with a missing key.
/// </summary>
public sealed class PatternSineCyclesPersistTest
{
    private static readonly JsonSerializerOptions SaveLikeMass = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private static readonly JsonSerializerOptions LoadLikeMass = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void PatternSineCyclesPerLayer_180_RoundTripsInMassSaveOptions()
    {
        var prefs = new AppPreferences
        {
            PatternType = "Sine",
            PatternSineCyclesPerLayer = 180,
            PatternAmplitude = 2,
            PatternWavelengthMm = 15,
        };
        string json = JsonSerializer.Serialize(prefs, SaveLikeMass);
        Assert.Contains("PatternSineCyclesPerLayer", json);
        Assert.Contains("180", json);

        var loaded = JsonSerializer.Deserialize<AppPreferences>(json, LoadLikeMass);
        Assert.NotNull(loaded);
        Assert.Equal(180, loaded.PatternSineCyclesPerLayer);
        Assert.Equal("Sine", loaded.PatternType);
        Assert.Equal(2, loaded.PatternAmplitude);
    }

    [Fact]
    public void PatternSineCyclesPerLayer_Zero_IsWritten_NotOmitted()
    {
        var prefs = new AppPreferences
        {
            PatternType = "Sine",
            PatternSineCyclesPerLayer = 0,
        };
        string json = JsonSerializer.Serialize(prefs, SaveLikeMass);
        Assert.Contains("PatternSineCyclesPerLayer", json);

        var loaded = JsonSerializer.Deserialize<AppPreferences>(json, LoadLikeMass);
        Assert.NotNull(loaded);
        Assert.Equal(0, loaded.PatternSineCyclesPerLayer);
    }
}
