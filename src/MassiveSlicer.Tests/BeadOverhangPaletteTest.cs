using System.Numerics;
using System.Text.Json;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

public sealed class BeadOverhangPaletteTest
{
    [Fact]
    public void White_to_red_is_the_default_ramp()
    {
        Near(new Vector3(1f, 1f, 1f), BeadOverhangPalette.Color(0f, false));
        Near(new Vector3(1f, 0.75f, 0.75f), BeadOverhangPalette.Color(0.25f, false));
        Near(new Vector3(1f, 0.5f, 0.5f), BeadOverhangPalette.Color(0.5f, false));
        Near(new Vector3(1f, 0.25f, 0.25f), BeadOverhangPalette.Color(0.75f, false));
        Near(new Vector3(1f, 0f, 0f), BeadOverhangPalette.Color(1f, false));
    }

    [Fact]
    public void Spectrum_runs_blue_green_red()
    {
        Near(new Vector3(0f, 0f, 1f), BeadOverhangPalette.Color(0f, true));
        Near(new Vector3(0f, 0.5f, 0.5f), BeadOverhangPalette.Color(0.25f, true));
        Near(new Vector3(0f, 1f, 0f), BeadOverhangPalette.Color(0.5f, true));
        Near(new Vector3(0.5f, 0.5f, 0f), BeadOverhangPalette.Color(0.75f, true));
        Near(new Vector3(1f, 0f, 0f), BeadOverhangPalette.Color(1f, true));
    }

    [Fact]
    public void Scores_outside_zero_to_one_clamp()
    {
        Near(new Vector3(0f, 0f, 1f), BeadOverhangPalette.Color(-2f, true));
        Near(new Vector3(1f, 0f, 0f), BeadOverhangPalette.Color(4f, true));
        Near(new Vector3(1f, 1f, 1f), BeadOverhangPalette.Color(float.NaN, false));
    }

    [Fact]
    public void Preference_defaults_off_and_round_trips()
    {
        var omitted = JsonSerializer.Deserialize<AppPreferences>("{}");
        Assert.NotNull(omitted);
        Assert.False(omitted.BeadOverhangSpectrum);

        var json = JsonSerializer.Serialize(new AppPreferences { BeadOverhangSpectrum = true });
        var loaded = JsonSerializer.Deserialize<AppPreferences>(json);
        Assert.NotNull(loaded);
        Assert.True(loaded.BeadOverhangSpectrum);
    }

    static void Near(Vector3 expected, Vector3 actual)
    {
        Assert.True((expected - actual).Length() < 1e-4f,
            $"expected {expected}, got {actual}");
    }
}
