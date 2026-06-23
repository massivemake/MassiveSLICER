using System.Linq;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

/// <summary>
/// Guards the rotary-bed E1 sync fix: when a cell payload is cloned, the rotary pivot must be the
/// node *inside* the cloned scene graph (so rotating it on E1 sync turns the visible top) — not a
/// separately deep-cloned orphan, which is what left the upper bed static after a cached cell load.
/// </summary>
public class RotaryPivotCloneTest(ITestOutputHelper output)
{
    private static SceneNode BuildRotaryRoot()
    {
        var top   = new SceneNode { Name = "top", LocalTransform = Matrix4.CreateTranslation(10, 0, 0) };
        var pivot = new SceneNode { Name = "RotaryBed_Top" };
        pivot.AddChild(top);
        var root = new SceneNode { Name = "RotaryBed" };
        root.AddChild(pivot);
        return root;
    }

    [Fact]
    public void PivotResolvedInClone_RotatesTop_OrphanDoesNot()
    {
        var clone = SceneNodeClone.DeepClone(BuildRotaryRoot());

        // The fix: find the pivot inside the cloned graph (the rendered instance).
        var pivotInClone = clone.SelfAndDescendants().First(n => n.Name == "RotaryBed_Top");
        var topInClone   = pivotInClone.Children.Single();

        var before = topInClone.WorldTransform.Row3.Xyz;
        pivotInClone.LocalTransform = Matrix4.CreateRotationZ(MathHelper.PiOver2);   // E1 = 90deg
        var afterPivot = topInClone.WorldTransform.Row3.Xyz;
        output.WriteLine($"top: before={before}  afterPivot={afterPivot}");
        Assert.True((afterPivot - before).Length > 1f, "rotating the in-graph pivot must move the top");

        // The old bug: a separately deep-cloned pivot is an orphan, absent from the clone graph,
        // so rotating it (what _rotaryBedPivot used to point at) does nothing to the visible top.
        var orphan = SceneNodeClone.DeepClone(pivotInClone);
        Assert.DoesNotContain(orphan, clone.SelfAndDescendants());
        orphan.LocalTransform = Matrix4.CreateRotationZ(MathHelper.Pi);              // 180deg
        var afterOrphan = topInClone.WorldTransform.Row3.Xyz;
        Assert.True((afterOrphan - afterPivot).Length < 0.01f, "orphan pivot must NOT move the cloned top");
    }
}
