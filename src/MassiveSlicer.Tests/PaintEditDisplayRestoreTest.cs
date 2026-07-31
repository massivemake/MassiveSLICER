using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

/// <summary>
/// Toolpath edit mode borrows the display toggles (seam dots, travel/wipe moves, …)
/// and must hand them back on exit. It used to stamp them one-way, so seam dots
/// stayed invisible for the rest of the session and only an app restart brought
/// them back.
/// </summary>
public class PaintEditDisplayRestoreTest
{
    private static ViewportViewModel EditableVm()
        => new() { ViewMode = "Preview" };

    [Fact]
    public void Edit_mode_hands_back_the_display_toggles_it_borrowed()
    {
        var vm = EditableVm();
        vm.ShowSeam = true;
        vm.ShowTravelMoves = true;
        vm.ShowWipeMoves = true;

        vm.IsPaintEditOpen = true;

        // Vacuity guard: if edit mode ever stops stamping these off, the restore
        // below would pass without proving anything.
        Assert.False(vm.ShowSeam);
        Assert.False(vm.ShowTravelMoves);
        Assert.False(vm.ShowWipeMoves);

        vm.IsPaintEditOpen = false;

        Assert.True(vm.ShowSeam);
        Assert.True(vm.ShowTravelMoves);
        Assert.True(vm.ShowWipeMoves);
    }

    [Fact]
    public void Toggles_the_user_had_off_before_editing_stay_off_afterwards()
    {
        var vm = EditableVm();
        vm.ShowSeam = false;
        vm.ShowTravelMoves = false;

        vm.IsPaintEditOpen = true;
        vm.IsPaintEditOpen = false;

        // Restore means "put back what was there", not "turn everything on".
        Assert.False(vm.ShowSeam);
        Assert.False(vm.ShowTravelMoves);
    }

    [Fact]
    public void A_mid_edit_granularity_flip_does_not_overwrite_the_pre_edit_snapshot()
    {
        var vm = EditableVm();
        vm.ShowSeam = true;

        vm.IsPaintEditOpen = true;
        // Each flip re-stamps the display mode; the snapshot must survive them.
        vm.PaintSelectGranularity = "Point";
        vm.PaintSelectGranularity = "Path";
        vm.IsPaintEditOpen = false;

        Assert.True(vm.ShowSeam);
    }

    [Fact]
    public void A_second_edit_session_restores_the_second_sessions_starting_state()
    {
        var vm = EditableVm();
        vm.ShowSeam = true;
        vm.IsPaintEditOpen = true;
        vm.IsPaintEditOpen = false;

        // User turns seam dots off themselves, then edits again.
        vm.ShowSeam = false;
        vm.IsPaintEditOpen = true;
        vm.IsPaintEditOpen = false;

        // A stale snapshot from session one would wrongly switch them back on.
        Assert.False(vm.ShowSeam);
    }
}
