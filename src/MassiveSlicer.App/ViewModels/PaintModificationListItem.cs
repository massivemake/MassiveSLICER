using System.Windows.Input;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// One applied paint modification in the right-panel MODIFICATIONS list.
/// Click expands options (bridge target, scaffold summary); trash removes marks.
/// </summary>
public sealed class PaintModificationListItem : ViewModelBase
{
    public Guid Id { get; init; }

    /// <summary>"Support" or "Remove".</summary>
    public string KindLabel { get; init; } = "Support";

    public bool IsSupport { get; init; }

    private string _title = "";
    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    private string _detail = "";
    public string Detail
    {
        get => _detail;
        set => SetField(ref _detail, value);
    }

    /// <summary>Accent colour for the kind badge.</summary>
    public string KindColor => IsSupport ? "#33D6FF" : "#FF7340";

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetField(ref _isExpanded, value)) return;
            OnPropertyChanged(nameof(ExpandChevron));
        }
    }

    public string ExpandChevron => IsExpanded ? "mdi-chevron-up" : "mdi-chevron-down";

    // ── Anchor (original selection) ──────────────────────────────────────────
    private string _anchorSummary = "";
    public string AnchorSummary
    {
        get => _anchorSummary;
        set => SetField(ref _anchorSummary, value);
    }

    /// <summary>Formbound style for this mod — revise anytime via the dropdown.</summary>
    public string[] SupportTypeOptions { get; } =
        ["Formbound Buttress", "Formbound Bridge"];

    private string _supportType = "Formbound Buttress";
    public string SupportType
    {
        get => _supportType;
        set
        {
            var v = value is "Formbound Bridge" ? "Formbound Bridge" : "Formbound Buttress";
            if (!SetField(ref _supportType, v)) return;
            SupportTypeChanged?.Invoke(Id, v);
        }
    }

    /// <summary>Viewport applies infill pattern when the user revises Support type.</summary>
    public Action<Guid, string>? SupportTypeChanged { get; set; }

    // ── Bridge target (optional second pick) ─────────────────────────────────
    private bool _hasBridgeTarget;
    public bool HasBridgeTarget
    {
        get => _hasBridgeTarget;
        set
        {
            if (!SetField(ref _hasBridgeTarget, value)) return;
            OnPropertyChanged(nameof(ShowBridgeEmpty));
            OnPropertyChanged(nameof(BridgeTargetLabel));
        }
    }

    private string _bridgeTargetSummary = "";
    public string BridgeTargetSummary
    {
        get => _bridgeTargetSummary;
        set => SetField(ref _bridgeTargetSummary, value);
    }

    public string BridgeTargetLabel =>
        HasBridgeTarget ? BridgeTargetSummary : "None — pick a path or point on another layer";

    public bool ShowBridgeEmpty => IsSupport && !HasBridgeTarget;

    private int _scaffoldLayerCount;
    public int ScaffoldLayerCount
    {
        get => _scaffoldLayerCount;
        set
        {
            if (!SetField(ref _scaffoldLayerCount, value)) return;
            OnPropertyChanged(nameof(ScaffoldSummary));
            OnPropertyChanged(nameof(HasScaffold));
        }
    }

    private int _scaffoldMarkCount;
    public int ScaffoldMarkCount
    {
        get => _scaffoldMarkCount;
        set
        {
            if (!SetField(ref _scaffoldMarkCount, value)) return;
            OnPropertyChanged(nameof(ScaffoldSummary));
            OnPropertyChanged(nameof(HasScaffold));
        }
    }

    public bool HasScaffold => ScaffoldLayerCount > 0 || ScaffoldMarkCount > 0;

    public string ScaffoldSummary
    {
        get
        {
            if (!IsSupport) return "";
            if (!HasBridgeTarget)
                return "Pick a bridge target — column starts at target mid, T opens at the anchor.";
            if (ScaffoldLayerCount <= 0)
                return "Column inherits between layers — Reslice to bake Formbound.";
            return $"{ScaffoldLayerCount} intermediate layer(s) · {ScaffoldMarkCount} scaffold mark(s). Reslice to bake.";
        }
    }

    private bool _isPickingBridgeTarget;
    public bool IsPickingBridgeTarget
    {
        get => _isPickingBridgeTarget;
        set
        {
            if (!SetField(ref _isPickingBridgeTarget, value)) return;
            OnPropertyChanged(nameof(PickBridgeButtonLabel));
        }
    }

    public string PickBridgeButtonLabel =>
        IsPickingBridgeTarget ? "Cancel pick…" : (HasBridgeTarget ? "Re-pick bridge target" : "Pick bridge target…");

    public ICommand? ToggleExpandCommand { get; init; }
    public ICommand? SelectCommand { get; init; }
    public ICommand? DeleteCommand { get; init; }
    public ICommand? PickBridgeTargetCommand { get; init; }
    public ICommand? ClearBridgeTargetCommand { get; init; }
}
