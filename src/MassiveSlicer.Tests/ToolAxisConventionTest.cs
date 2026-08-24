using System.Numerics;
using MassiveSlicer.Core.Kinematics;

namespace MassiveSlicer.Tests;

public sealed class ToolAxisConventionTest
{
    [Fact]
    public void Undefined_and_ZPlus_are_identity()
    {
        Assert.Equal(Matrix4x4.Identity, ToolAxisConventionMath.ExtraRotation(ToolAxisConvention.Undefined));
        Assert.Equal(Matrix4x4.Identity, ToolAxisConventionMath.ExtraRotation(ToolAxisConvention.ZPlus));
    }

    [Fact]
    public void ZMinus_flips_taught_Z()
    {
        var c = ToolAxisConventionMath.ExtraRotation(ToolAxisConvention.ZMinus);
        var z = Vector3.Transform(Vector3.UnitZ, c);
        Assert.True(z.Z < -0.99f, $"expected -Z, got {z}");
    }

    [Fact]
    public void XPlus_sends_taught_Z_to_plus_X()
    {
        var c = ToolAxisConventionMath.ExtraRotation(ToolAxisConvention.XPlus);
        var z = Vector3.Transform(Vector3.UnitZ, c);
        Assert.True(z.X > 0.99f, $"expected +X, got {z}");
    }

    [Fact]
    public void T12_abc_plus_every_convention_does_not_throw()
    {
        var taught = KukaIkSolver.AbcToMatrix(103.677f, -43.719f, 40.483f);
        foreach (ToolAxisConvention kind in Enum.GetValues<ToolAxisConvention>())
        {
            var shown = ToolAxisConventionMath.ExtraRotation(kind) * taught;
            var (a, b, c) = KukaIkSolver.MatrixToAbc(shown);
            Assert.False(float.IsNaN(a) || float.IsNaN(b) || float.IsNaN(c), $"{kind}: NaN ABC");
        }
    }

    [Fact]
    public void XMinus_sends_taught_Z_to_minus_X()
    {
        var c = ToolAxisConventionMath.ExtraRotation(ToolAxisConvention.XMinus);
        var z = Vector3.Transform(Vector3.UnitZ, c);
        Assert.True(z.X < -0.99f, $"expected -X, got {z}");
    }

    [Fact]
    public void Shop_default_is_ZMinus()
    {
        Assert.Equal(ToolAxisConvention.ZMinus, MassiveSlicer.ViewModels.ToolAxisConventionOption.Default.Kind);
    }

    [Fact]
    public void Flange_display_is_plus_90_about_Z()
    {
        var r = ToolAxisConventionMath.FlangeDisplayRotation;
        var x = Vector3.Transform(Vector3.UnitX, r);
        var y = Vector3.Transform(Vector3.UnitY, r);
        var z = Vector3.Transform(Vector3.UnitZ, r);
        Assert.True(x.Y > 0.99f, $"X should become +Y, got {x}");
        Assert.True(y.X < -0.99f, $"Y should become -X, got {y}");
        Assert.True(z.Z > 0.99f, $"Z stays +Z, got {z}");
    }
}
