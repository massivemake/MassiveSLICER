using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

public sealed class TravelWipeDefaultsTest
{
    [Fact]
    public void HasTravelMoves_is_true_only_for_travel_hops()
    {
        var none = new Toolpath();
        var layer = new ToolpathLayer(0, 3f);
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 3), new Vector3(10, 0, 3), MoveKind.Extrude));
        none.Layers.Add(layer);
        Assert.False(none.HasTravelMoves());

        var with = new Toolpath();
        var l2 = new ToolpathLayer(0, 3f);
        l2.Moves.Add(new ToolpathMove(new Vector3(0, 0, 3), new Vector3(10, 0, 3), MoveKind.Extrude));
        l2.Moves.Add(new ToolpathMove(new Vector3(10, 0, 3), new Vector3(20, 5, 3), MoveKind.Travel));
        with.Layers.Add(l2);
        Assert.True(with.HasTravelMoves());
    }

    [Fact]
    public void ApplyShopWipeForTravels_sets_same_direction_35_smash_600()
    {
        var add = new AdditiveSettingsViewModel
        {
            WipeModeDisplay = "Off",
            WipeLengthMm = 12,
            WipeRampMm = 4,
            WipeSpeed = 120,
        };

        Assert.True(add.ApplyShopWipeForTravels());
        Assert.Equal("Same-Direction", add.WipeModeDisplay);
        Assert.Equal(35, add.WipeLengthMm);
        Assert.Equal(-1, add.WipeRampMm);
        Assert.Equal(600, add.WipeSpeed);
        Assert.False(add.ApplyShopWipeForTravels());
    }
}
