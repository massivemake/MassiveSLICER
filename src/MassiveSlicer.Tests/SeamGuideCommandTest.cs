using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

public class SeamGuideCommandTest
{
    private static (ViewportViewModel Vm, AdditiveSettingsViewModel Add) Make()
    {
        var add = new AdditiveSettingsViewModel();
        var vm = new ViewportViewModel { AdditiveSettings = add };
        vm.GetSelectedPartWorldBounds = () => (new Vector3(100f, -80f, 130f), new Vector3(400f, 120f, 330f));
        return (vm, add);
    }

    [Fact]
    public void Corner_Snaps_To_The_Chosen_Corner_Of_The_Part_At_Its_Base()
    {
        var (vm, add) = Make();

        vm.SeamGuideCommand("corner -x -y");
        Assert.Equal(new SeamGuidePoint(100f, -80f, 130f), Assert.Single(add.SeamGuides));

        vm.SeamGuideCommand("corner +x +y");
        Assert.Equal(new SeamGuidePoint(400f, 120f, 130f), Assert.Single(add.SeamGuides));
    }

    [Fact]
    public void Corner_Replaces_A_Leftover_Guide_Instead_Of_Adding_To_It()
    {
        var (vm, add) = Make();
        add.SetSeamGuides([new SeamGuidePoint(2757f, 60f, 0f)]);

        vm.SeamGuideCommand("corner -x +y");

        Assert.Equal(new SeamGuidePoint(100f, 120f, 130f), Assert.Single(add.SeamGuides));
    }

    [Fact]
    public void Set_Add_And_Clear()
    {
        var (vm, add) = Make();

        vm.SeamGuideCommand("set 10 20");
        vm.SeamGuideCommand("add 30 40 5");
        Assert.Equal([new SeamGuidePoint(10f, 20f, 0f), new SeamGuidePoint(30f, 40f, 5f)], add.SeamGuides);

        vm.SeamGuideCommand("clear");
        Assert.Empty(add.SeamGuides);
    }

    [Fact]
    public void Bad_Input_Changes_Nothing()
    {
        var (vm, add) = Make();
        add.SetSeamGuides([new SeamGuidePoint(1f, 2f, 3f)]);

        Assert.Contains("usage", vm.SeamGuideCommand("corner left bottom"));
        Assert.Contains("usage", vm.SeamGuideCommand("set 10"));
        Assert.Equal(new SeamGuidePoint(1f, 2f, 3f), Assert.Single(add.SeamGuides));
    }

    [Fact]
    public void Corner_Without_A_Selection_Says_So_And_Keeps_The_Guide()
    {
        var (vm, add) = Make();
        add.SetSeamGuides([new SeamGuidePoint(1f, 2f, 3f)]);
        vm.GetSelectedPartWorldBounds = () => null;

        Assert.Contains("select a part", vm.SeamGuideCommand("corner -x -y"));
        Assert.Single(add.SeamGuides);
    }
}
