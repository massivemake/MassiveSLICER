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
    [InlineData("Outward", BrimDirection.Outward)]
    [InlineData("Inward",  BrimDirection.Inward)]
    [InlineData("Both",    BrimDirection.Both)]
    public void Display_string_maps_to_the_slicing_enum(string display, BrimDirection expected)
        => Assert.Equal(expected, AdditiveSettingsViewModel.ParseBrimDirection(display));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("inward")]      // wrong case is NOT a silent Inward
    [InlineData("Sideways")]
    public void Anything_unrecognised_falls_back_to_outward(string? display)
        => Assert.Equal(BrimDirection.Outward, AdditiveSettingsViewModel.ParseBrimDirection(display));

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
    public void Default_is_outward_everywhere_so_old_files_do_not_change_behaviour()
    {
        Assert.Equal("Outward", new AdditiveSettingsViewModel().BrimDirectionDisplay);
        Assert.Equal("Outward", new AppPreferences().BrimDirectionDisplay);
        Assert.Equal(BrimDirection.Outward, new SliceSettings().BrimDirection);
    }
}
