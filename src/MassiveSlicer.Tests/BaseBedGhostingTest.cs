using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public class BaseBedGhostingTest
{
    [Fact]
    public void Heated_base_ghosts_rotary_and_keeps_heated_solid()
    {
        var heated = new SceneNode { Name = "HeatedBed" };
        var rotary = new SceneNode { Name = "RotaryBed" };
        rotary.AddChild(new SceneNode { Name = "RotaryBed_Top" });

        BaseBedGhosting.Apply(heated, rotary, heatedActive: true);

        Assert.False(heated.EnvironmentGhost);
        Assert.False(heated.TranslucentPass);
        Assert.True(rotary.EnvironmentGhost);
        Assert.True(rotary.TranslucentPass);
        Assert.True(rotary.Children[0].EnvironmentGhost);
        Assert.True(BaseBedGhosting.IsGhosted(rotary.Children[0]));
        Assert.False(BaseBedGhosting.IsGhosted(heated));
    }

    [Fact]
    public void Rotary_base_ghosts_heated_and_keeps_rotary_solid()
    {
        var heated = new SceneNode { Name = "HeatedBed" };
        var rotary = new SceneNode { Name = "RotaryBed" };

        BaseBedGhosting.Apply(heated, rotary, heatedActive: false);

        Assert.True(heated.EnvironmentGhost);
        Assert.True(heated.TranslucentPass);
        Assert.False(rotary.EnvironmentGhost);
        Assert.False(rotary.TranslucentPass);
    }

    [Fact]
    public void Switching_base_clears_the_previous_ghost()
    {
        var heated = new SceneNode { Name = "HeatedBed" };
        var rotary = new SceneNode { Name = "RotaryBed" };

        BaseBedGhosting.Apply(heated, rotary, heatedActive: true);
        BaseBedGhosting.Apply(heated, rotary, heatedActive: false);

        Assert.True(heated.EnvironmentGhost);
        Assert.False(rotary.EnvironmentGhost);
    }
}
