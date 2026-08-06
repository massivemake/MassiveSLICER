using System.Text.Json;
using MassiveSlicer.App.Views;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class MaterialPresetJsonIoTest
{
    [Fact]
    public void Parse_single_object_round_trips_fields()
    {
        var src = new MaterialPreset
        {
            Name = "ASA - Black",
            MaterialType = "ASA",
            Color = "Black",
            Temperature1 = 250,
            Temperature2 = 250,
            Temperature3 = 250,
            FlowRate = 0.4115,
            FlowRateHf = 0.6019,
            MaterialDensity = 1.17,
            CostPerLb = 5,
            GlassTransitionC = 100,
            ThermalBondMarginC = 10,
            ThermalSagMarginC = 45,
            ThermalAmbientC = 30,
            CalibratedOn = "2026-08-01",
            CalibrationNote = "test",
        };

        string json = JsonSerializer.Serialize(src, new JsonSerializerOptions { WriteIndented = true });
        var parsed = MaterialPresetDialog.ParseMaterialPresetJson(json);
        Assert.NotNull(parsed);
        Assert.Equal(src.Name, parsed!.Name);
        Assert.Equal(src.FlowRate, parsed.FlowRate);
        Assert.Equal(src.FlowRateHf, parsed.FlowRateHf);
        Assert.Equal(src.ThermalAmbientC, parsed.ThermalAmbientC);
        Assert.Equal(src.CalibratedOn, parsed.CalibratedOn);
    }

    [Fact]
    public void Parse_array_uses_first_entry()
    {
        const string json = """
            [
              { "Name": "First", "MaterialType": "ABS", "Color": "Black", "FlowRate": 0.5 },
              { "Name": "Second", "MaterialType": "PETG", "Color": "Clear", "FlowRate": 0.4 }
            ]
            """;
        var parsed = MaterialPresetDialog.ParseMaterialPresetJson(json);
        Assert.NotNull(parsed);
        Assert.Equal("First", parsed!.Name);
        Assert.Equal(0.5, parsed.FlowRate);
    }

    [Fact]
    public void Editor_LoadFrom_after_parse_matches_export()
    {
        var vm = new MaterialPresetEditorViewModel();
        vm.LoadFrom(new MaterialPreset
        {
            Name = "PC - Natural",
            MaterialType = "PC",
            Color = "Natural",
            Temperature1 = 280,
            FlowRate = 0.5,
            MaterialDensity = 1.2,
        });
        var round = MaterialPresetDialog.ParseMaterialPresetJson(
            JsonSerializer.Serialize(vm.ToPreset()));
        Assert.NotNull(round);
        Assert.Equal("PC - Natural", round!.Name);
        Assert.Equal(280, round.Temperature1);
    }

    [Fact]
    public void LoadFrom_preserves_custom_name_not_type_color_pattern()
    {
        var vm = new MaterialPresetEditorViewModel();
        vm.LoadFrom(new MaterialPreset
        {
            Name = "PPGF",
            MaterialType = "Other",
            Color = "Natural",
            Temperature1 = 250,
            FlowRate = 0.4115,
        });
        Assert.Equal("PPGF", vm.Name);
        Assert.Equal("Other", vm.MaterialType);
        Assert.Equal("Natural", vm.Color);
    }
}
