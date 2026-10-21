using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Reopen used to slice immediately and stamp the shop wipe recipe over the
/// mode, length, ramp, and speed just read from the .mass file.
/// </summary>
public sealed class ShopWipeRestoreTest
{
    [Fact]
    public void Fresh_import_claims_shop_wipe_once()
    {
        var vm = new ViewportViewModel();
        var node = new SceneNode { Name = "fresh" };
        vm.AddImportNode(node);
        var item = vm.FindOutlinerItem(node);

        Assert.NotNull(item);
        Assert.True(vm.TryClaimShopWipe(item));
        Assert.False(vm.TryClaimShopWipe(item));
    }

    [Fact]
    public void Restored_model_keeps_the_saved_wipe()
    {
        var vm = new ViewportViewModel();
        var node = new SceneNode { Name = "saved" };
        vm.AddImportNode(node);
        var item = vm.FindOutlinerItem(node);
        Assert.NotNull(item);

        vm.KeepSavedWipe(item);
        Assert.False(vm.TryClaimShopWipe(item));
    }

    [Fact]
    public void New_workspace_lets_the_next_import_snap_wipe()
    {
        var vm = new ViewportViewModel();
        var first = new SceneNode { Name = "first" };
        vm.AddImportNode(first);
        var firstItem = vm.FindOutlinerItem(first);
        Assert.NotNull(firstItem);
        vm.KeepSavedWipe(firstItem);

        vm.ClearUserScene();
        var next = new SceneNode { Name = "next" };
        vm.AddImportNode(next);
        var nextItem = vm.FindOutlinerItem(next);

        Assert.NotNull(nextItem);
        Assert.True(vm.TryClaimShopWipe(nextItem));
    }
}
