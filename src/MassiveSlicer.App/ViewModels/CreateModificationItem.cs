using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// One operation in the edit-mode CREATE MODIFICATION catalog (searchable list).
/// </summary>
public sealed class CreateModificationItem : ViewModelBase
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Icon { get; init; }

    /// <summary>False for catalog stubs that are not implemented yet.</summary>
    public bool IsAvailable { get; init; } = true;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}
