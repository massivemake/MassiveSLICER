using MassiveSlicer.Core.Kinematics;

namespace MassiveSlicer.Tests;

public sealed class SolveRecipeTest
{
    [Fact]
    public void Mill_is_position_first_then_optional_6d()
    {
        var r = SolveRecipe.Mill;
        Assert.True(r.PositionFirst);
        Assert.True(r.ThenOrient);
        Assert.False(r.RequireWorkspace);
        Assert.Equal(400, r.PositionMaxIter);
        Assert.Equal(120, r.OrientMaxIter);
        Assert.True(r.PreferNamedHomeSeed);
    }

    [Fact]
    public void Print_is_6d_from_current_pose()
    {
        var r = SolveRecipe.Print;
        Assert.False(r.PositionFirst);
        Assert.True(r.ThenOrient);
        Assert.Equal(300, r.OrientMaxIter);
        Assert.False(r.PreferNamedHomeSeed);
    }
}
