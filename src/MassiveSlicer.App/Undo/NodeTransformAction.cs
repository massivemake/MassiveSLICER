using OpenTK.Mathematics;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.App.Undo;

/// <summary>Reverses a single object transform (move, rotate, or scale).</summary>
public sealed class NodeTransformAction : IUndoAction
{
    private readonly SceneNode _node;
    private readonly Matrix4 _before;
    private readonly Matrix4 _after;
    private readonly Action? _onApplied;

    public string Description { get; }

    public NodeTransformAction(
        SceneNode node,
        Matrix4 before,
        Matrix4 after,
        string description,
        Action? onApplied = null)
    {
        _node      = node;
        _before    = before;
        _after     = after;
        Description = description;
        _onApplied = onApplied;
    }

    public void Undo() => Apply(_before);
    public void Redo() => Apply(_after);

    private void Apply(Matrix4 transform)
    {
        _node.LocalTransform = transform;
        _onApplied?.Invoke();
    }
}