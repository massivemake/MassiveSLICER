using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

/// <summary>
/// The toolpath display layers (seam dots, beads, travel/wipe moves) are
/// per-view settings. There used to be one shared switch behind every eye menu,
/// so entering toolpath edit mode once turned the 3D view's seam dots off for
/// the rest of the session, and switching view pills threw away your overrides.
/// </summary>
public class ViewDisplayLayerProfileTest
{
    [Fact]
    public void Edit_mode_seam_setting_does_not_follow_you_back_to_the_3d_view()
    {
        var vm = new ViewportViewModel { ViewMode = "Preview" };
        vm.ShowSeam = true;

        vm.IsPaintEditOpen = true;
        vm.ShowSeam = false;          // user declutters *inside* edit mode
        vm.IsPaintEditOpen = false;

        Assert.True(vm.ShowSeam);     // Preview keeps what Preview had
    }

    [Fact]
    public void Edit_mode_remembers_its_own_layers_between_visits()
    {
        var vm = new ViewportViewModel { ViewMode = "Preview" };

        vm.IsPaintEditOpen = true;
        vm.ShowTravelMoves = true;    // edit mode starts with travels off
        vm.IsPaintEditOpen = false;

        Assert.False(vm.ShowTravelMoves);   // Preview is unaffected...
        vm.IsPaintEditOpen = true;
        Assert.True(vm.ShowTravelMoves);    // ...and edit mode kept the change
    }

    [Fact]
    public void Entering_edit_mode_no_longer_overrides_what_you_set()
    {
        var vm = new ViewportViewModel { ViewMode = "Preview" };

        vm.IsPaintEditOpen = true;
        vm.ShowSeam = false;
        vm.ShowBead = true;
        vm.IsPaintEditOpen = false;
        vm.IsPaintEditOpen = true;

        // Re-entry used to stamp seam off and beads from PaintShowBeads.
        Assert.False(vm.ShowSeam);
        Assert.True(vm.ShowBead);
    }

    [Fact]
    public void Each_view_pill_keeps_its_own_layers_across_switches()
    {
        var vm = new ViewportViewModel { ViewMode = "Preview" };
        vm.ShowTravelMoves = true;    // an override Preview never had by default

        vm.ViewMode = "Toolpath";
        vm.ShowTravelMoves = false;   // and the opposite override on Toolpath

        vm.ViewMode = "Preview";
        Assert.True(vm.ShowTravelMoves);
        vm.ViewMode = "Toolpath";
        Assert.False(vm.ShowTravelMoves);
    }

    [Fact]
    public void Out_of_the_box_each_view_still_looks_the_way_it_always_did()
    {
        var vm = new ViewportViewModel();

        // Preview = printed-part look: bead surface, no lines.
        vm.ViewMode = "Preview";
        Assert.True(vm.ShowBead);
        Assert.False(vm.ShowExtrusionMoves);
        Assert.False(vm.ShowTravelMoves);
        Assert.False(vm.ShowWipeMoves);

        // Toolpath = classic extrusion + travel lines, no beads.
        vm.ViewMode = "Toolpath";
        Assert.False(vm.ShowBead);
        Assert.True(vm.ShowExtrusionMoves);
        Assert.True(vm.ShowTravelMoves);

        // Speed = clean gradient lines, travels suppressed.
        vm.ViewMode = "Speed";
        Assert.False(vm.ShowBead);
        Assert.True(vm.ShowExtrusionMoves);
        Assert.False(vm.ShowTravelMoves);
    }

    [Fact]
    public void Edit_mode_opens_decluttered_but_keeps_seam_dots()
    {
        var vm = new ViewportViewModel { ViewMode = "Preview" };
        vm.IsPaintEditOpen = true;

        Assert.True(vm.ShowExtrusionMoves);   // you need the lines to click them
        Assert.False(vm.ShowTravelMoves);
        Assert.False(vm.ShowWipeMoves);
        Assert.False(vm.ShowBead);
        Assert.True(vm.ShowSeam);             // the dots Jeff went looking for
    }

    [Fact]
    public void Profiles_saved_before_this_change_do_not_clutter_the_preview()
    {
        // A prefs blob from an older build: no display-layer properties at all.
        const string oldJson = """
        {
          "Preview": { "ShowGrid": true, "ShowAxes": true, "DarkBackground": false },
          "Toolpath": { "ShowGrid": false, "ShowAxes": false, "DarkBackground": true }
        }
        """;

        var vm = new ViewportViewModel { ViewMode = "Preview" };
        vm.LoadViewProfiles(oldJson);

        // Naive deserialisation gives every layer the class default (true), which
        // would drop extrusion lines and travels on top of Preview's bead view.
        vm.ViewMode = "Preview";
        Assert.True(vm.ShowBead);
        Assert.False(vm.ShowExtrusionMoves);
        Assert.False(vm.ShowTravelMoves);

        vm.ViewMode = "Toolpath";
        Assert.False(vm.ShowBead);
        Assert.True(vm.ShowExtrusionMoves);
    }

    [Fact]
    public void Layer_choices_survive_a_save_load_round_trip()
    {
        var vm = new ViewportViewModel { ViewMode = "Preview" };
        vm.ShowSeam = true;
        vm.IsPaintEditOpen = true;
        vm.ShowSeam = false;
        vm.IsPaintEditOpen = false;

        var restored = new ViewportViewModel { ViewMode = "Preview" };
        restored.LoadViewProfiles(vm.SerializeViewProfiles());

        restored.ViewMode = "Preview";
        Assert.True(restored.ShowSeam);
        restored.IsPaintEditOpen = true;
        Assert.False(restored.ShowSeam);
    }
}
