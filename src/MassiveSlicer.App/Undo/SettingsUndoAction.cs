namespace MassiveSlicer.App.Undo;

/// <summary>Reverses a batch of settings-panel / viewport preference changes.</summary>
public sealed class SettingsUndoAction : IUndoAction
{
    private readonly string _beforeJson;
    private readonly string _afterJson;
    private readonly Action<string> _applyJson;

    public string Description { get; }

    public SettingsUndoAction(
        string beforeJson,
        string afterJson,
        Action<string> applyJson,
        string description = "Settings")
    {
        _beforeJson  = beforeJson;
        _afterJson   = afterJson;
        _applyJson   = applyJson;
        Description  = description;
    }

    public void Undo()  => _applyJson(_beforeJson);
    public void Redo()  => _applyJson(_afterJson);
}