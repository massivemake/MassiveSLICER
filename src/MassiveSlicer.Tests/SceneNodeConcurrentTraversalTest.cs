using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

/// <summary>
/// Opening a workspace builds the scene graph on the UI thread while the GL thread walks it every
/// frame. <see cref="SceneNode.SelfAndDescendants"/> is a foreach over live <see cref="List{T}"/>s,
/// so that combination threw "Collection was modified" and killed the app outright — roughly one
/// open in three on a large .mass. These tests pin the fix: the render-path traversals survive a
/// graph being mutated underneath them.
/// </summary>
public sealed class SceneNodeConcurrentTraversalTest
{
    /// <summary>
    /// CONTROL. Proves the hazard is real and that the harness below actually reproduces it —
    /// without this, a green result from the next test would prove nothing (the mutation might
    /// simply never have landed during the walk).
    /// </summary>
    [Fact]
    public void SelfAndDescendants_throws_when_the_graph_is_mutated_mid_walk()
    {
        var caught = RunRace(root =>
        {
            foreach (var n in root.SelfAndDescendants())
                Touch(n);
        });

        Assert.True(caught is InvalidOperationException,
            "Expected the live-list traversal to throw 'Collection was modified'. It did not, so " +
            $"this harness is no longer reproducing the race and the sibling test is vacuous. Got: {Describe(caught)}");
    }

    [Fact]
    public void SelfAndDescendantsForRender_survives_the_graph_being_mutated_mid_walk()
    {
        var caught = RunRace(root =>
        {
            foreach (var n in root.SelfAndDescendantsForRender())
                Touch(n);
        });

        Assert.True(caught is null,
            $"The render-path traversal must not throw while the graph is mutated. Got: {Describe(caught)}");
    }

    [Fact]
    public void ChildrenForRender_survives_the_graph_being_mutated_mid_walk()
    {
        var caught = RunRace(root =>
        {
            foreach (var child in root.ChildrenForRender())
                Touch(child);
        });

        Assert.True(caught is null,
            $"ChildrenForRender must not throw while the graph is mutated. Got: {Describe(caught)}");
    }

    [Fact]
    public void Draw_child_recursion_survives_the_graph_being_mutated_mid_walk()
    {
        // Draw() itself needs a GL context, but its child loop is the part that races. Walking
        // the same structure the same way is what this pins; the loop shape is shared.
        var caught = RunRace(root =>
        {
            var stack = new Stack<SceneNode>();
            stack.Push(root);
            while (stack.Count > 0)
                foreach (var child in stack.Pop().ChildrenForRender())
                {
                    Touch(child);
                    stack.Push(child);
                }
        });

        Assert.True(caught is null, $"Expected no throw. Got: {Describe(caught)}");
    }

    /// <summary>
    /// Every node present for the whole walk must be returned — the tolerance is only allowed to
    /// drop nodes that were being attached or detached at that instant, not stable ones.
    /// </summary>
    [Fact]
    public void SelfAndDescendantsForRender_returns_the_whole_tree_when_nothing_is_mutating()
    {
        var root = BuildTree(depth: 4, breadth: 4);
        var expected = root.SelfAndDescendants().ToList();
        var actual   = root.SelfAndDescendantsForRender().ToList();

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.OrderBy(n => n.Name).Select(n => n.Name),
                     actual  .OrderBy(n => n.Name).Select(n => n.Name));
        // Depth-first, parents before their own children — same contract as the original.
        Assert.Equal(expected.Select(n => n.Name), actual.Select(n => n.Name));
    }

    // -- harness ---------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="walk"/> repeatedly on one thread while another thread adds and removes
    /// children, mirroring the UI thread building the scene under the GL thread. Returns the first
    /// exception the walking thread saw, or null.
    /// </summary>
    private static Exception? RunRace(Action<SceneNode> walk)
    {
        var root = BuildTree(depth: 3, breadth: 6);
        var targets = root.SelfAndDescendants().Where(n => n.Children.Count > 0).ToList();

        Exception? caught = null;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var mutationsLanded = 0;

        var mutator = Task.Run(() =>
        {
            int i = 0;
            while (!stop.IsCancellationRequested)
            {
                var target = targets[i++ % targets.Count];
                var added = new SceneNode { Name = $"Churn {i}" };
                target.AddChild(added);
                Interlocked.Increment(ref mutationsLanded);
                target.RemoveChild(added);
            }
        });

        var walker = Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                    walk(root);
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                stop.Cancel();   // stop the mutator as soon as we have a verdict
            }
        });

        Task.WaitAll([mutator, walker], TimeSpan.FromSeconds(10));

        Assert.True(mutationsLanded > 0,
            "The mutating thread never ran, so nothing was actually raced — this test proves nothing.");
        return caught;
    }

    private static SceneNode BuildTree(int depth, int breadth)
    {
        var root = new SceneNode { Name = "Root" };
        Grow(root, depth, breadth);
        return root;

        static void Grow(SceneNode parent, int depth, int breadth)
        {
            if (depth <= 0) return;
            for (int i = 0; i < breadth; i++)
            {
                var child = new SceneNode { Name = $"{parent.Name}.{i}" };
                parent.AddChild(child);
                Grow(child, depth - 1, breadth);
            }
        }
    }

    /// <summary>Reads the node the way the real render walks do, so the loop cannot be optimised away.</summary>
    private static void Touch(SceneNode n)
    {
        if (n.Name.Length < 0) throw new InvalidOperationException("unreachable");
    }

    private static string Describe(Exception? ex)
        => ex is null ? "no exception" : $"{ex.GetType().Name}: {ex.Message}";
}
