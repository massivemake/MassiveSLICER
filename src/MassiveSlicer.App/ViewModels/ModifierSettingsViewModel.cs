using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// The settings inspector for whichever modifier is currently selected (see
/// <see cref="ModifierPanelViewModel.SelectedCut"/>) — wraps one <see cref="IModifier"/>.
/// Stack order/membership/rename all live on the modifier's own outliner row now (nested under
/// its owning mesh's Modifiers group); this class only owns the numeric/settings fields below
/// the "SETTINGS" header. The wrapped modifier already has a real, independent plane object by
/// the time this exists — that's created once, at actual creation time (see
/// ViewportViewModel.AddCutModifier), never here.
/// </summary>
public sealed class ModifierSettingsViewModel : ViewModelBase
{
    private readonly ViewportViewModel _viewport;

    public IModifier Modifier { get; }

    /// <summary>Non-null when this wraps a Cut modifier — exposes its settings for binding.</summary>
    public CutModifier? Cut => Modifier as CutModifier;

    public string Name => Modifier.Name;

    public bool Enabled
    {
        get => Modifier.Enabled;
        set
        {
            if (Modifier.Enabled == value) return;
            Modifier.Enabled = value;
            OnPropertyChanged();
        }
    }

    // -- Cut settings (only meaningful when Cut is not null; UI gates on IsCutModifier) --
    // Offset/RotationDegrees/Orientation changes push into the gizmo node's transform
    // immediately, so a numeric edit here and a gizmo drag always agree with each other.

    public bool IsCutModifier => Cut is not null;

    public bool IsHorizontal
    {
        get => Cut?.Orientation == CutOrientation.Horizontal;
        set
        {
            if (Cut is null) return;
            var orientation = value ? CutOrientation.Horizontal : CutOrientation.Vertical;
            if (Cut.Orientation == orientation) return;
            Cut.Orientation = orientation;
            _viewport.SyncModifierGizmoNodeFromFields(Cut);
            _viewport.RebuildModifierPlaneMesh(Cut);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsVertical));
        }
    }

    public bool IsVertical
    {
        get => Cut?.Orientation == CutOrientation.Vertical;
        set => IsHorizontal = !value;
    }

    /// <summary>Which way a Vertical plane faces, in degrees (0 = +X, 90 = +Y). Ignored for Horizontal.</summary>
    public float RotationDegrees
    {
        get => Cut?.RotationDegrees ?? 0f;
        set
        {
            if (Cut is null || Cut.RotationDegrees == value) return;
            Cut.RotationDegrees = value;
            _viewport.SyncModifierGizmoNodeFromFields(Cut);
            _viewport.NotifyRenderNeeded();
            OnPropertyChanged();
        }
    }

    /// <summary>Whether this plane actually cuts (unchecked = reference-only marker).</summary>
    public bool WillCut
    {
        get => Cut?.Cut ?? false;
        set
        {
            if (Cut is null || Cut.Cut == value) return;
            Cut.Cut = value;
            OnPropertyChanged();
        }
    }

    public float Offset
    {
        get => Cut?.Offset ?? 0f;
        set
        {
            if (Cut is null || Cut.Offset == value) return;
            Cut.Offset = value;
            _viewport.SyncModifierGizmoNodeFromFields(Cut);
            _viewport.NotifyRenderNeeded();
            OnPropertyChanged();
        }
    }

    public bool Infinite
    {
        get => Cut?.Infinite ?? true;
        set
        {
            if (Cut is null || Cut.Infinite == value) return;
            Cut.Infinite = value;
            _viewport.RebuildModifierPlaneMesh(Cut);
            OnPropertyChanged();
        }
    }

    public float SizeX
    {
        get => Cut?.SizeX ?? 0f;
        set
        {
            if (Cut is null || Cut.SizeX == value) return;
            Cut.SizeX = value;
            if (!Cut.Infinite) _viewport.RebuildModifierPlaneMesh(Cut);
            OnPropertyChanged();
        }
    }

    public float SizeY
    {
        get => Cut?.SizeY ?? 0f;
        set
        {
            if (Cut is null || Cut.SizeY == value) return;
            Cut.SizeY = value;
            if (!Cut.Infinite) _viewport.RebuildModifierPlaneMesh(Cut);
            OnPropertyChanged();
        }
    }

    internal ModifierSettingsViewModel(IModifier modifier, ViewportViewModel viewport)
    {
        Modifier  = modifier;
        _viewport = viewport;
    }
}
