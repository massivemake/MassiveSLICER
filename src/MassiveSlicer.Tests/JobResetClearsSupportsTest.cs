using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Support pockets and MODIFICATIONS cards are job data, so resetting the job must take them
/// with it. Moving the specs out of app preferences stopped them surviving an app relaunch,
/// but File &gt; New still left the previous job's pockets and cards in the panel — aimed at
/// geometry that no longer existed, and then applied to whatever mesh was imported next.
/// <para>
/// <c>ClearUserScene</c> is the single reset point: File &gt; New calls it, and so does the
/// workspace open path (which repopulates from the file immediately after).
/// </para>
/// </summary>
public sealed class JobResetClearsSupportsTest
{
    static StructuralSupportSpec Spec(string name) => new()
    {
        Name = name,
        AnchorX = 2400, AnchorY = -300, AnchorLayer = 0,
        CenterX = 2400, CenterY = -200, WidthMm = 92, DepthMm = 42,
    };

    static ViewportViewModel VmWithJobData()
    {
        var vm = new ViewportViewModel { AdditiveSettings = new AdditiveSettingsViewModel() };
        vm.AdditiveSettings!.StructuralSupports.Add(Spec("Support 1"));
        vm.AdditiveSettings.StructuralSupports.Add(Spec("Support 2"));
        vm.AdditiveSettings.SelectedSupportIndex = 1;
        vm.PaintModifications.Add(new PaintModificationListItem { Id = Guid.NewGuid() });
        vm.PaintModifications.Add(new PaintModificationListItem { Id = Guid.NewGuid() });
        return vm;
    }

    [Fact]
    public void Resetting_the_job_clears_the_support_pockets()
    {
        var vm = VmWithJobData();

        // Vacuity guard — if the fixture didn't actually hold supports, the assert below
        // would pass without proving anything.
        Assert.Equal(2, vm.AdditiveSettings!.StructuralSupports.Count);

        vm.ClearUserScene();

        Assert.Empty(vm.AdditiveSettings.StructuralSupports);
        Assert.Equal(-1, vm.AdditiveSettings.SelectedSupportIndex);
    }

    [Fact]
    public void Resetting_the_job_clears_the_modification_cards()
    {
        var vm = VmWithJobData();
        Assert.Equal(2, vm.PaintModifications.Count);

        vm.ClearUserScene();

        Assert.Empty(vm.PaintModifications);
    }

    [Fact]
    public void Resetting_the_job_also_clears_the_views_own_card_list()
    {
        // The view holds the authoritative list; this VM collection is only the display.
        // Clearing the display alone was not enough — adding one support resynced the panel
        // from the view's untouched list and every old card reappeared. So the reset has to
        // reach the view, which it does by asking it to restore nothing.
        var vm = VmWithJobData();
        var restoreCalls = new List<int>();
        vm.RestorePaintModifications = saved => restoreCalls.Add(saved.Count);

        vm.ClearUserScene();

        Assert.Single(restoreCalls);
        Assert.Equal(0, restoreCalls[0]);
    }

    [Fact]
    public void Resetting_a_job_with_no_settings_attached_does_not_throw()
    {
        // AdditiveSettings is nullable and is null early in startup.
        var vm = new ViewportViewModel();
        vm.PaintModifications.Add(new PaintModificationListItem { Id = Guid.NewGuid() });

        vm.ClearUserScene();

        Assert.Empty(vm.PaintModifications);
    }
}
