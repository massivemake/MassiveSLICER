using OpenTK.Mathematics;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.App.Undo;

/// <summary>Reverses a transform edit that carried one or more linked nodes along with it
/// (e.g. a model dragged together with its toolpath) — undo/redo restores every node in
/// the group atomically, so they can never end up desynced from each other.</summary>
public sealed class LinkedTransformAction : IUndoAction
{
    private readonly List<(SceneNode Node, Matrix4 Before, Matrix4 After)> _entries;
    private readonly Action? _onApplied;

    public string Description { get; }

    public LinkedTransformAction(
        List<(SceneNode Node, Matrix4 Before, Matrix4 After)> entries,
        string description,
        Action? onApplied = null)
    {
        _entries    = entries;
        Description = description;
        _onApplied  = onApplied;
    }

    public void Undo() => Apply(before: true);
    public void Redo() => Apply(before: false);

    private void Apply(bool before)
    {
        foreach (var entry in _entries)
            entry.Node.LocalTransform = before ? entry.Before : entry.After;
        _onApplied?.Invoke();
    }
}
