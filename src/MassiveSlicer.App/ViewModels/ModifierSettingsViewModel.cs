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
            _viewport.SetVerticalRotationInPlace(Cut, value);
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

    /// <summary>1-based index of whichever real toolpath layer sits nearest the plane's current
    /// world height — an alternate way to place a Horizontal cut by layer instead of raw height,
    /// reusing the exact same Offset the height field writes to. Null (and the field disabled)
    /// for Vertical, whose Offset is a distance along the plane's own rotated facing direction,
    /// not a height at all — "layer" has no meaning there. Null also when the owner mesh hasn't
    /// been sliced yet (no layers to snap to). Always reflects the plane's real current position,
    /// same as Offset/RotationDegrees — including right after a gizmo drag (see
    /// ViewportViewModel.SyncModifierAfterGizmoEdit, which calls NotifyAllFieldsChanged below).</summary>
    public int? LayerNumber
    {
        get
        {
            if (Cut is null || Cut.Orientation != CutOrientation.Horizontal) return null;
            var layers = _viewport.GetOwnerToolpathLayers(Cut);
            if (layers is null || layers.Count == 0) return null;

            var worldZ = _viewport.ResolveBedCenterXYZ().Z + Cut.Offset;
            int nearest = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < layers.Count; i++)
            {
                var dist = Math.Abs(layers[i].Z - worldZ);
                if (dist < bestDist) { bestDist = dist; nearest = i; }
            }
            return nearest + 1;
        }
        set
        {
            if (Cut is null || Cut.Orientation != CutOrientation.Horizontal || value is not { } layerNumber) return;
            var layers = _viewport.GetOwnerToolpathLayers(Cut);
            if (layers is null || layers.Count == 0) return;

            var index = Math.Clamp(layerNumber - 1, 0, layers.Count - 1);
            var newOffset = layers[index].Z - _viewport.ResolveBedCenterXYZ().Z;
            if (Cut.Offset == newOffset) return;
            Cut.Offset = newOffset;
            _viewport.SyncModifierGizmoNodeFromFields(Cut);
            _viewport.NotifyRenderNeeded();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Offset));
        }
    }

    /// <summary>Re-raises every field a gizmo drag can change, so the panel stays live-accurate
    /// while dragging instead of only refreshing on the next selection change. Called by
    /// ViewportViewModel.SyncModifierAfterGizmoEdit whenever this settings VM is the one
    /// currently showing the cut being dragged.</summary>
    internal void NotifyAllFieldsChanged()
    {
        OnPropertyChanged(nameof(Offset));
        OnPropertyChanged(nameof(RotationDegrees));
        OnPropertyChanged(nameof(LayerNumber));
    }

    internal ModifierSettingsViewModel(IModifier modifier, ViewportViewModel viewport)
    {
        Modifier  = modifier;
        _viewport = viewport;
    }
}
