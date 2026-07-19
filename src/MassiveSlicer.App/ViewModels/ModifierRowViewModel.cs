using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Scene;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// A single row in the modifier panel's stack — wraps one <see cref="IModifier"/>, plus the
/// selection/rename UI state around it. Select/delete/reorder are panel-level commands
/// (see <see cref="ModifierPanelViewModel"/>), parameterized by the row. Selecting a row is
/// local to the panel (see <see cref="ModifierPanelViewModel.SelectedModifier"/>) — it never
/// changes which object the rest of the app's panels consider selected.
/// </summary>
public sealed class ModifierRowViewModel : ViewModelBase
{
    private readonly ViewportViewModel _viewport;

    public IModifier Modifier { get; }

    /// <summary>The mesh this modifier is attached to — needed to keep its gizmo node in sync
    /// (Horizontal parents to it; Vertical measures against bed center instead).</summary>
    internal SceneNode Owner { get; }

    /// <summary>Non-null when this row wraps a Cut modifier — exposes its settings for binding.</summary>
    public CutModifier? Cut => Modifier as CutModifier;

    public string Name
    {
        get => Modifier.Name;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? Modifier.Name : value.Trim();
            if (Modifier.Name == trimmed) return;
            Modifier.Name = trimmed;
            OnPropertyChanged();
        }
    }

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

    private bool _isSelected;
    /// <summary>Whether this row's settings are the ones shown below the stack.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetField(ref _isSelected, value);
    }

    private bool _isRenaming;
    public bool IsRenaming
    {
        get => _isRenaming;
        set => SetField(ref _isRenaming, value);
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
            _viewport.SyncModifierGizmoNodeFromFields(Cut, Owner);
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
            _viewport.SyncModifierGizmoNodeFromFields(Cut, Owner);
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
            _viewport.SyncModifierGizmoNodeFromFields(Cut, Owner);
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
            OnPropertyChanged();
        }
    }

    // -- Drag-reorder visual state (set by ModifierPanelViewModel while a drag is in progress) --

    private bool _showDropLineAbove;
    public bool ShowDropLineAbove
    {
        get => _showDropLineAbove;
        internal set => SetField(ref _showDropLineAbove, value);
    }

    private bool _showDropLineBelow;
    public bool ShowDropLineBelow
    {
        get => _showDropLineBelow;
        internal set => SetField(ref _showDropLineBelow, value);
    }

    internal ModifierRowViewModel(IModifier modifier, SceneNode owner, ViewportViewModel viewport)
    {
        Modifier  = modifier;
        Owner     = owner;
        _viewport = viewport;
        if (Cut is not null) _viewport.GetOrCreateModifierGizmoNode(Cut, Owner);
    }
}
