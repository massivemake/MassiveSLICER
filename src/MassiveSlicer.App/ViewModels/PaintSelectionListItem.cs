using System.Windows.Input;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// One row in the edit-mode selection popup (layer / span under the selection count button).
/// </summary>
public sealed class PaintSelectionListItem
{
    /// <summary>Toolpath layer index (0-based, matches <see cref="Core.Models.ToolpathLayer.Index"/>).</summary>
    public int LayerIndex { get; init; }

    /// <summary>1-based layer number for display.</summary>
    public int LayerNumber => LayerIndex + 1;

    public int MoveStart { get; init; }
    public int MoveCount { get; init; }

    /// <summary>Layer Z in mm.</summary>
    public float LayerZ { get; init; }

    /// <summary>True when this row is a single-bead (Point mode) pick.</summary>
    public bool IsPoint { get; init; }

    /// <summary>Primary line, e.g. "Layer 12 · Path" or "Layer 12 · Point".</summary>
    public string Title { get; init; } = "";

    /// <summary>Secondary line, e.g. "Z 36 · m420+18".</summary>
    public string Detail { get; init; } = "";

    /// <summary>Deselect this row only (wired when the list is built).</summary>
    public ICommand? RemoveCommand { get; init; }
}
