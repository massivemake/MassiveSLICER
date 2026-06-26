namespace MassiveSlicer.App.Undo;

/// <summary>Contract for a single reversible edit (transform, settings, etc.).</summary>
public interface IUndoAction
{
    string Description { get; }
    void Undo();
    void Redo();
}

/// <summary>Classic undo/redo stack with a configurable depth limit.</summary>
public sealed class UndoRedoService
{
    private const int MaxDepth = 100;

    private readonly List<IUndoAction> _actions = [];
    private int _index = -1;

    public bool CanUndo => _index >= 0;
    public bool CanRedo => _index < _actions.Count - 1;

    public string? UndoDescription => CanUndo ? _actions[_index].Description : null;
    public string? RedoDescription => CanRedo ? _actions[_index + 1].Description : null;

    public event Action? StateChanged;

    public void Push(IUndoAction action)
    {
        if (_index < _actions.Count - 1)
            _actions.RemoveRange(_index + 1, _actions.Count - _index - 1);

        _actions.Add(action);
        _index++;

        if (_actions.Count > MaxDepth)
        {
            int trim = _actions.Count - MaxDepth;
            _actions.RemoveRange(0, trim);
            _index -= trim;
        }

        StateChanged?.Invoke();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _actions[_index].Undo();
        _index--;
        StateChanged?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _index++;
        _actions[_index].Redo();
        StateChanged?.Invoke();
    }

    public void Clear()
    {
        _actions.Clear();
        _index = -1;
        StateChanged?.Invoke();
    }
}