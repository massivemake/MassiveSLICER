using MassiveSlicer.Core.Kinematics;

namespace MassiveSlicer.Tests;

public sealed class KukaStatusTurnTest
{
    [Fact]
    public void Turn_bits_are_one_per_negative_axis()
    {
        // Example from KSS: {91, -45, 37, 1, -1, 0} → T = 18
        var (_, t) = KukaStatusTurn.FromJoints([91f, -45f, 37f, 1f, -1f, 0f]);
        Assert.Equal(18, t);
    }

    [Fact]
    public void Status_bit2_set_when_a5_positive()
    {
        var (s, t) = KukaStatusTurn.FromJoints(
            [-0.220f, -79.080f, 115.260f, 179.690f, 22.580f, -179.830f]);
        Assert.Equal(4, s);   // A5 > 0
        Assert.Equal(35, t);  // A1, A2, A6 negative
    }

    [Fact]
    public void Status_bit2_clear_when_a5_not_positive()
    {
        var (s, _) = KukaStatusTurn.FromJoints([0f, -90f, 90f, 0f, 0f, 0f]);
        Assert.Equal(0, s);
        var (sNeg, _) = KukaStatusTurn.FromJoints([0f, -90f, 90f, 0f, -15f, 0f]);
        Assert.Equal(0, sNeg);
    }

    [Fact]
    public void Default_cell_home_matches_export()
    {
        var (s, t) = KukaStatusTurn.FromJoints([0f, -90f, 90f, 0f, 15f, 0f]);
        Assert.Equal(4, s);
        Assert.Equal(2, t); // only A2 < 0
    }
}
