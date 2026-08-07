using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

/// <summary>
/// Pins the scale tool's arithmetic — the chain's ratio rule, and what "100%" is a percentage of.
/// </summary>
/// <remarks>
/// These exercise <see cref="NodeTransform"/> and <see cref="SceneNode.ImportScale"/> directly
/// rather than the view model, which needs a live renderer and selection. The parts worth pinning
/// are the arithmetic and the captured baseline, and both live here.
/// </remarks>
public sealed class ScaleToolTest
{
    private const float Tol = 1e-4f;

    private static SceneNode NodeScaledBy(float factor)
    {
        var node = new SceneNode { Name = "part" };
        node.LocalTransform = Matrix4.CreateScale(factor);
        node.EnsurePlacement(Vector3.Zero);
        return node;
    }

    [Fact]
    public void Import_scale_is_captured_when_the_placement_is_first_taken()
    {
        // A metres-as-millimetres import is corrected x1000 into the matrix before the placement is
        // decomposed, so the raw scale is 1000 while the part is, to the user, at 100% of the file.
        var node = NodeScaledBy(1000f);

        Assert.NotNull(node.ImportScale);
        Assert.Equal(1000f, node.ImportScale!.Value.X, 3);
        Assert.Equal(1000f, node.Placement!.Value.Scale.X, 3);
    }

    [Fact]
    public void Import_scale_is_captured_once_and_does_not_follow_later_resizing()
    {
        var node = NodeScaledBy(1000f);

        var t = node.Placement!.Value;
        t.Scale *= 2f;
        node.SetPlacement(t);

        // Still the imported baseline, so the part now reads as 200% rather than falling back to 100.
        Assert.Equal(1000f, node.ImportScale!.Value.X, 3);
        Assert.Equal(2000f, node.Placement!.Value.Scale.X, 3);
    }

    [Fact]
    public void A_plain_import_has_an_import_scale_of_one()
    {
        var node = NodeScaledBy(1f);
        Assert.Equal(1f, node.ImportScale!.Value.X, 3);
    }

    [Fact]
    public void Chaining_scales_by_ratio_not_by_amount()
    {
        // Jeff's own example: taking a 100 down to 50 is x0.5, so a 1300 on another axis must land
        // on 650. Setting every axis to the typed number instead would turn any part into a cube,
        // which is the mistake this test exists to catch.
        var t = new NodeTransform(Vector3.Zero, Quaternion.Identity, Vector3.One, Vector3.Zero);
        var baseSize = new Vector3(100f, 1300f, 700f);

        // Editing X from 100 to 50.
        float wanted  = 50f / baseSize.X;
        float current = t.Scale.X;
        t.Scale *= wanted / current;

        Assert.Equal(50f,  baseSize.X * t.Scale.X, 3);
        Assert.Equal(650f, baseSize.Y * t.Scale.Y, 3);
        Assert.Equal(350f, baseSize.Z * t.Scale.Z, 3);
    }

    [Fact]
    public void Unchained_editing_leaves_the_other_axes_alone()
    {
        var t = new NodeTransform(Vector3.Zero, Quaternion.Identity, Vector3.One, Vector3.Zero);
        var s = t.Scale;
        s[1] = 2f;
        t.Scale = s;

        Assert.Equal(1f, t.Scale.X, 3);
        Assert.Equal(2f, t.Scale.Y, 3);
        Assert.Equal(1f, t.Scale.Z, 3);
    }

    [Fact]
    public void Percent_is_measured_against_the_imported_size_not_against_one()
    {
        // The case that makes ImportScale necessary: a metres import sitting at its natural size is
        // 100%, not 100,000%.
        var node = NodeScaledBy(1000f);
        var import = node.ImportScale!.Value;
        var scale  = node.Placement!.Value.Scale;

        float percentX = scale.X / import.X * 100f;
        Assert.Equal(100f, percentX, 2);
    }

    [Fact]
    public void Resetting_returns_to_the_imported_scale()
    {
        var node = NodeScaledBy(1000f);
        var t = node.Placement!.Value;
        t.Scale *= 3.7f;
        node.SetPlacement(t);

        var reset = node.Placement!.Value;
        reset.Scale = node.ImportScale!.Value;
        node.SetPlacement(reset);

        Assert.Equal(1000f, node.Placement!.Value.Scale.X, 3);
    }

    [Fact]
    public void A_uniform_fit_keeps_the_part_in_proportion()
    {
        // Fit to Cell is always uniform: the limiting axis sets one ratio and all three take it, so
        // the aspect ratio out matches the aspect ratio in.
        var baseSize = new Vector3(4000f, 1000f, 2000f);
        var t = new NodeTransform(Vector3.Zero, Quaternion.Identity, Vector3.One, Vector3.Zero);

        const float allowedX = 2700f;
        const float allowedY = 2700f;
        float ratio = MathF.Min(allowedX / baseSize.X, allowedY / baseSize.Y);
        t.Scale *= ratio;

        var fitted = new Vector3(
            baseSize.X * t.Scale.X, baseSize.Y * t.Scale.Y, baseSize.Z * t.Scale.Z);

        Assert.Equal(2700f, fitted.X, 2);                    // X was the limiting axis
        Assert.True(fitted.Y <= allowedY + Tol);
        // Proportion preserved: 4:1:2 in, 4:1:2 out.
        Assert.Equal(baseSize.X / baseSize.Y, fitted.X / fitted.Y, 3);
        Assert.Equal(baseSize.X / baseSize.Z, fitted.X / fitted.Z, 3);
    }

    [Fact]
    public void Clamping_blocks_a_scale_of_zero()
    {
        // The fields reject zero and negatives up front, but ClampScale is the last line of defence
        // for anything that reaches the transform another way.
        var t = new NodeTransform(Vector3.Zero, Quaternion.Identity, new Vector3(0f, 1f, 1f), Vector3.Zero);
        t.ClampScale();
        Assert.True(t.Scale.X >= NodeTransform.MinScale);
    }
}
