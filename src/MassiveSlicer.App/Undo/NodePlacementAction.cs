using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.App.Undo;

/// <summary>Reverses a pivot move — Recenter Origin, or a Move Origin snap.</summary>
/// <remarks>
/// Needs its own action rather than reusing the matrix-based one: moving a pivot deliberately leaves
/// the composed transform <em>identical</em> (that is the whole point — the geometry must not move),
/// so a before/after matrix pair records nothing and undo silently does nothing. The pivot lives in
/// the placement, so the placement is what has to be captured.
/// </remarks>
public sealed class NodePlacementAction : IUndoAction
{
    private readonly SceneNode _node;
    private readonly NodePose  _before;
    private readonly NodePose  _after;
    private readonly Action?   _onApplied;

    public string Description { get; }

    public NodePlacementAction(SceneNode node, NodePose before, NodePose after,
                               string description, Action? onApplied = null)
    {
        _node       = node;
        _before     = before;
        _after      = after;
        Description = description;
        _onApplied  = onApplied;
    }

    public void Undo() => Apply(_before);
    public void Redo() => Apply(_after);

    private void Apply(NodePose pose)
    {
        pose.ApplyTo(_node);
        _onApplied?.Invoke();
    }
}
