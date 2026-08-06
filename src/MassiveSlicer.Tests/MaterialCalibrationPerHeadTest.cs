using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

/// <summary>
/// HV and HF have different screws, so each keeps its own purge test. Calibration used to
/// write the HV flow rate no matter which head was purged, and the dialog kept one set of
/// inputs — so switching heads showed the other head's grams.
/// </summary>
public class MaterialCalibrationPerHeadTest
{
    private static MaterialPresetEditorViewModel Editor()
        => new() { MaterialDensity = 1.17, Temperature1 = 250, Temperature2 = 250, Temperature3 = 250 };

    [Fact]
    public void Calibrating_HF_leaves_the_HV_flow_rate_alone()
    {
        var vm = Editor();
        vm.FlowRate = 0.4115;                 // existing HV calibration
        vm.CalibIsHf = true;
        vm.CalibMotorPercent = 50; vm.CalibTimeSec = 60; vm.CalibWeightG = 127;

        vm.ApplyCalibration();

        Assert.Equal(0.4115, vm.FlowRate, 4);          // HV untouched
        Assert.True(vm.FlowRateHf > 0, "HF flow rate should have been written");
    }

    [Fact]
    public void Calibrating_HV_leaves_the_HF_flow_rate_alone()
    {
        var vm = Editor();
        vm.FlowRateHf = 0.6019;
        vm.CalibIsHv = true;
        vm.CalibMotorPercent = 50; vm.CalibTimeSec = 60; vm.CalibWeightG = 127;

        vm.ApplyCalibration();

        Assert.Equal(0.6019, vm.FlowRateHf, 4);        // HF untouched
        Assert.True(vm.FlowRate > 0);
    }

    [Fact]
    public void Toggling_heads_shows_that_heads_own_numbers_not_the_others()
    {
        var vm = Editor();
        vm.CalibIsHf = true;
        vm.CalibWeightG = 127; vm.CalibMotorPercent = 40; vm.CalibTimeSec = 90;

        vm.CalibIsHv = true;                            // switch to the never-calibrated head
        Assert.Equal(0, vm.CalibWeightG);               // 0 g, not 127
        Assert.Equal(50, vm.CalibMotorPercent);         // defaults, not HF's 40
        Assert.Equal(60, vm.CalibTimeSec);

        vm.CalibIsHf = true;                            // back again — HF's values survive
        Assert.Equal(127, vm.CalibWeightG);
        Assert.Equal(40, vm.CalibMotorPercent);
        Assert.Equal(90, vm.CalibTimeSec);
    }

    [Fact]
    public void Both_heads_survive_a_save_and_reload()
    {
        var vm = Editor();
        vm.CalibIsHv = true; vm.CalibWeightG = 100; vm.CalibMotorPercent = 30;
        vm.CalibIsHf = true; vm.CalibWeightG = 127; vm.CalibMotorPercent = 55;

        var reloaded = Editor();
        reloaded.LoadFrom(vm.ToPreset());

        reloaded.CalibIsHv = true;
        Assert.Equal(100, reloaded.CalibWeightG);
        Assert.Equal(30,  reloaded.CalibMotorPercent);
        reloaded.CalibIsHf = true;
        Assert.Equal(127, reloaded.CalibWeightG);
        Assert.Equal(55,  reloaded.CalibMotorPercent);
    }
}
