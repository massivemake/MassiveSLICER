using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.App.Undo;

/// <summary>
/// Reversible delete of a user mesh or toolpath (the X-key shortcut). Undo re-inserts
/// the captured outliner row and rebuilds the scene content: meshes re-upload from their
/// retained CPU data (<c>Mesh.PickingData</c> → <c>PendingMesh</c>), toolpaths re-enter
/// through the normal <c>PendingToolpath</c> pipeline from a snapshot, preserving pose.
/// </summary>
public sealed class NodeDeleteAction : IUndoAction
{
    /// <summary>Everything needed to rebuild one toolpath renderer entry.</summary>
    public readonly record struct ToolpathRestore(
        SceneNode Node,
        ToolpathSnapshot Snapshot,
        Matrix4 LocalTransform,
        System.Numerics.Vector3 Origin);

    private readonly ViewportViewModel _vm;
    private readonly OutlinerItemViewModel _item;
    private readonly OutlinerItemViewModel? _parent;
    private readonly int _index;
    private readonly SceneNode _node;
    private readonly bool _isToolpath;
    private readonly IReadOnlyList<ToolpathRestore> _toolpaths;

    public NodeDeleteAction(
        ViewportViewModel vm,
        OutlinerItemViewModel item,
        OutlinerItemViewModel? parent,
        int index,
        SceneNode node,
        bool isToolpath,
        IReadOnlyList<ToolpathRestore> toolpaths)
    {
        _vm         = vm;
        _item       = item;
        _parent     = parent;
        _index      = index;
        _node       = node;
        _isToolpath = isToolpath;
        _toolpaths  = toolpaths;
    }

    public string Description => $"Delete {_item.Name}";

    public void Undo()
    {
        _vm.RestoreOutlinerItem(_item, _parent, _index);

        if (!_isToolpath)
        {
            // Re-upload every mesh in the subtree from the retained CPU-side data.
            foreach (var n in _node.SelfAndDescendants())
                if (n.PendingMesh is null && n.Mesh?.PickingData is { } md)
                    n.PendingMesh = md;
            _vm.PendingNodes.Enqueue(_node);
        }

        foreach (var t in _toolpaths)
        {
            _vm.PendingToolpath.Enqueue(new PendingToolpathEntry
            {
                Toolpath                = t.Snapshot.Smoothed,
                RawToolpath             = t.Snapshot.Raw,
                Node                    = t.Node,
                BeadWidth               = t.Snapshot.BeadWidth,
                LayerHeight             = t.Snapshot.LayerHeight,
                MaterialColor           = t.Snapshot.MaterialColor,
                PreserveRelativePose    = true,
                PreservedLocalTransform = t.LocalTransform,
                PreservedOrigin         = t.Origin,
            });
        }

        _vm.NotifyRenderNeeded();
    }

    public void Redo() => _vm.RequestDeleteNode(_node);
}
