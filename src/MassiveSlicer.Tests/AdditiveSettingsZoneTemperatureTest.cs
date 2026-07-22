using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

/// <summary>
/// The "Additional temp all zones" sidebar field is an AMPLIFIER, not a setter: it must
/// add/subtract on top of each zone's own material-preset temperature, never overwrite or
/// flatten the three zones to a single value.
/// </summary>
public sealed class AdditiveSettingsZoneTemperatureTest
{
    [Fact]
    public void No_offset_exports_each_zones_own_material_setpoint()
    {
        var vm = new AdditiveSettingsViewModel
        {
            Temperature1 = 290,
            Temperature2 = 275,
            Temperature3 = 300,
        };

        Assert.Equal("290/275/300", vm.ExportTemperaturesLabel);
        Assert.Equal(290f, vm.GetEffectiveExportTemperature(1));
        Assert.Equal(275f, vm.GetEffectiveExportTemperature(2));
        Assert.Equal(300f, vm.GetEffectiveExportTemperature(3));
    }

    [Fact]
    public void Positive_offset_adds_to_every_zone_independently()
    {
        var vm = new AdditiveSettingsViewModel
        {
            Temperature1 = 290,
            Temperature2 = 275,
            Temperature3 = 300,
            TemperatureOffset = "+10",
        };

        Assert.Equal(300f, vm.GetEffectiveExportTemperature(1));
        Assert.Equal(285f, vm.GetEffectiveExportTemperature(2));
        Assert.Equal(310f, vm.GetEffectiveExportTemperature(3));
        // The material setpoint label itself is unaffected by the offset — it's the export
        // preview that changes, not the material.
        Assert.Equal("290/275/300", vm.ExportTemperaturesLabel);
    }

    [Fact]
    public void Negative_offset_subtracts_from_every_zone_independently()
    {
        var vm = new AdditiveSettingsViewModel
        {
            Temperature1 = 290,
            Temperature2 = 275,
            Temperature3 = 300,
            TemperatureOffset = "-15",
        };

        Assert.Equal(275f, vm.GetEffectiveExportTemperature(1));
        Assert.Equal(260f, vm.GetEffectiveExportTemperature(2));
        Assert.Equal(285f, vm.GetEffectiveExportTemperature(3));
    }
}
