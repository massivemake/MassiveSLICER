namespace MassiveSlicer.App.Undo;

/// <summary>
/// Reverses a toolpath line selection in the Preview edit menu (sticky amber
/// highlight). Does not reverse paint marks — those re-slice via the edit menu.
/// </summary>
public sealed class PaintLineSelectionAction : IUndoAction
{
    private readonly Action _undo;
    private readonly Action _redo;

    public string Description { get; }

    public PaintLineSelectionAction(string description, Action undo, Action redo)
    {
        Description = description;
        _undo = undo;
        _redo = redo;
    }

    public void Undo() => _undo();
    public void Redo() => _redo();
}
