using MassiveSlicer.App.Enums;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public class MillRealtimeTest
{
    [Fact]
    public void Milling_tab_settings_request_a_remill()
    {
        Assert.True(SubtractiveSettingsViewModel.AffectsMillToolpath(nameof(SubtractiveSettingsViewModel.StepoverMm)));
        Assert.True(SubtractiveSettingsViewModel.AffectsMillToolpath(nameof(SubtractiveSettingsViewModel.ProjectionUseE1)));
        Assert.True(SubtractiveSettingsViewModel.AffectsMillToolpath(nameof(SubtractiveSettingsViewModel.ProjectionSliceDirection)));
        Assert.True(SubtractiveSettingsViewModel.AffectsMillToolpath(nameof(SubtractiveSettingsViewModel.PlanarTiltDeg)));
        Assert.True(SubtractiveSettingsViewModel.AffectsMillToolpath(nameof(SubtractiveSettingsViewModel.CuttingFeedMmS)));
        Assert.True(SubtractiveSettingsViewModel.AffectsMillToolpath(nameof(SubtractiveSettingsViewModel.SelectedOperation)));
    }

    [Fact]
    public void Expanders_and_toolhead_sliders_do_not_remill()
    {
        Assert.False(SubtractiveSettingsViewModel.AffectsMillToolpath(nameof(SubtractiveSettingsViewModel.StepOperationExpanded)));
        Assert.False(SubtractiveSettingsViewModel.AffectsMillToolpath(nameof(SubtractiveSettingsViewModel.ToolheadB)));
        Assert.False(SubtractiveSettingsViewModel.AffectsMillToolpath(nameof(SubtractiveSettingsViewModel.SelectedOperationDisplayName)));
        Assert.False(SubtractiveSettingsViewModel.AffectsMillToolpath(null));
    }

    [Fact]
    public void HasMillToolpath_sees_a_mill_child_and_ignores_print()
    {
        var vm = new ViewportViewModel();
        var import = new SceneNode { Name = "Part", PendingMesh = Box() };
        vm.AddImportNode(import);
        var importItem = vm.EnumerateUserModelItems().Single();
        Assert.False(vm.HasMillToolpath());

        vm.RegisterToolpathInOutliner(new SceneNode { Name = "Part" }, importItem, OutlinerToolpathKind.Print);
        Assert.False(vm.HasMillToolpath());

        vm.RegisterToolpathInOutliner(new SceneNode { Name = "Part Mill" }, importItem, OutlinerToolpathKind.Mill);
        Assert.True(vm.HasMillToolpath());
    }

    static MeshData Box()
    {
        Vector3[] positions =
        [
            new(0, 0, 0),
            new(1, 0, 0),
            new(1, 1, 0),
            new(0, 1, 0),
        ];
        uint[] indices = [0, 1, 2, 0, 2, 3];
        return new MeshData(positions, positions, indices, "box");
    }
}
