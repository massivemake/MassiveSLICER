using System.Windows.Input;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// One applied paint modification in the right-panel MODIFICATIONS list.
/// Reselect restores the original path/point selection; Delete removes its marks.
/// </summary>
public sealed class PaintModificationListItem
{
    public Guid Id { get; init; }

    /// <summary>"Support" or "Remove".</summary>
    public string KindLabel { get; init; } = "Support";

    public bool IsSupport { get; init; }

    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";

    /// <summary>Accent colour for the kind badge (hex-friendly brush set in XAML).</summary>
    public string KindColor => IsSupport ? "#33D6FF" : "#FF7340";

    public ICommand? SelectCommand { get; init; }
    public ICommand? DeleteCommand { get; init; }
}
