using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

/// <summary>
/// The brim direction crosses a string boundary: the UI and prefs store a display word, the
/// slicer takes an enum. A typo in that mapping is silent — the dropdown would read "Inward"
/// while the toolpath still came out Outward — so the mapping is pinned here.
/// </summary>
public class BrimDirectionSettingTest
{
    [Theory]
    [InlineData("Outside", BrimDirection.Outside)]
    [InlineData("Inside",  BrimDirection.Inside)]
    [InlineData("Both",    BrimDirection.Both)]
    public void Display_string_maps_to_the_slicing_enum(string display, BrimDirection expected)
        => Assert.Equal(expected, AdditiveSettingsViewModel.ParseBrimDirection(display));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("inside")]      // wrong case is NOT a silent Inside
    [InlineData("Sideways")]
    public void Anything_unrecognised_falls_back_to_outside(string? display)
        => Assert.Equal(BrimDirection.Outside, AdditiveSettingsViewModel.ParseBrimDirection(display));

    [Fact]
    public void Every_dropdown_option_maps_to_a_distinct_direction()
    {
        // Guards against adding an option to the UI list and forgetting the parse arm — the
        // new entry would silently behave as Outward.
        var vm = new AdditiveSettingsViewModel();
        var mapped = vm.BrimDirectionOptions
                       .Select(AdditiveSettingsViewModel.ParseBrimDirection)
                       .ToList();
        Assert.Equal(vm.BrimDirectionOptions.Length, mapped.Distinct().Count());
    }

    [Fact]
    public void Default_is_outside_everywhere_so_old_files_do_not_change_behaviour()
    {
        Assert.Equal("Outside", new AdditiveSettingsViewModel().BrimDirectionDisplay);
        Assert.Equal("Outside", new AppPreferences().BrimDirectionDisplay);
        Assert.Equal(BrimDirection.Outside, new SliceSettings().BrimDirection);
    }

    [Fact]
    public void Every_direction_has_a_display_string_that_is_actually_in_the_dropdown()
    {
        // The bug this pins: the console command hard-coded "Outward"/"Inward". When the options
        // were renamed to Outside/Inside those strings matched no option, so setting the direction
        // from the console left the dropdown rendering BLANK while the slicer quietly fell back to
        // Outside. Anything that sets a direction must produce a string the UI can show.
        var vm = new AdditiveSettingsViewModel();
        foreach (BrimDirection d in Enum.GetValues<BrimDirection>())
        {
            string display = AdditiveSettingsViewModel.BrimDirectionDisplayFor(d);
            Assert.Contains(display, vm.BrimDirectionOptions);
            Assert.Equal(d, AdditiveSettingsViewModel.ParseBrimDirection(display));
        }
    }
}
