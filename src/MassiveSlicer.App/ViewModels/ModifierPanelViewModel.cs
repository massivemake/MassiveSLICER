using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels.Base;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Backs the "5 MODIFIERS" step: the modifier stack for whatever model is currently
/// selected, plus the add/select/reorder/rename/delete actions on it. A pending modifier
/// lives only here (and as a viewport gizmo, once built) — it isn't mirrored into the object
/// outliner until Apply gives it a real relationship to resulting pieces. Selecting a row is
/// local to this panel and never changes the object selection other panels see.
/// </summary>
public sealed class ModifierPanelViewModel : ViewModelBase
{
    private ViewportViewModel? _viewport;

    /// <summary>Set once by MainWindowViewModel after both VMs exist.</summary>
    internal ViewportViewModel? Viewport
    {
        get => _viewport;
        set
        {
            if (ReferenceEquals(_viewport, value)) return;
            if (_viewport is not null) _viewport.PropertyChanged -= OnViewportPropertyChanged;
            _viewport = value;
            if (_viewport is not null) _viewport.PropertyChanged += OnViewportPropertyChanged;
            RefreshRows();
        }
    }

    public ObservableCollection<ModifierRowViewModel> Rows { get; } = [];

    private ModifierRowViewModel? _selectedModifier;
    /// <summary>The row whose settings show below the stack, or null if none selected.</summary>
    public ModifierRowViewModel? SelectedModifier
    {
        get => _selectedModifier;
        private set
        {
            if (ReferenceEquals(_selectedModifier, value)) return;
            if (_selectedModifier is not null) _selectedModifier.IsSelected = false;
            _selectedModifier = value;
            if (_selectedModifier is not null)
            {
                _selectedModifier.IsSelected = true;
                // Selecting a modifier hands it the move gizmo — moving it must never move
                // the underlying mesh. Default to Translate ("move tool") like the existing
                // Cut Tool does, unless the user already explicitly picked Rotate/Scale.
                if (_viewport is { } vp && vp.ActiveGizmoModeInternal is GizmoMode.None or GizmoMode.Scale)
                    vp.ActiveGizmoModeInternal = GizmoMode.Translate;
            }
            OnPropertyChanged();
            // The viewport's plane preview follows whichever modifier is selected here —
            // without this, nothing tells the GL canvas a new frame is needed, so a newly
            // selected/added modifier's plane would never actually get painted.
            _viewport?.NotifyRenderNeeded();
        }
    }

    /// <summary>True once a model is selected — gates the add-modifier buttons and Apply.</summary>
    public bool HasOwner => _viewport?.SelectedModifierOwner is not null;

    public ICommand AddCutModifierCommand { get; }
    public ICommand SelectCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand BeginRenameCommand { get; }
    public ICommand CommitRenameCommand { get; }

    public ModifierPanelViewModel()
    {
        AddCutModifierCommand = new RelayCommand(AddCutModifier, () => HasOwner);
        SelectCommand         = new RelayCommand<ModifierRowViewModel>(row => SelectedModifier = row);
        DeleteCommand         = new RelayCommand<ModifierRowViewModel>(Delete);
        BeginRenameCommand    = new RelayCommand<ModifierRowViewModel>(row => { if (row is not null) row.IsRenaming = true; });
        CommitRenameCommand   = new RelayCommand<ModifierRowViewModel>(row => { if (row is not null) row.IsRenaming = false; });
    }

    private void OnViewportPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewportViewModel.SelectedModifierOwner))
            RefreshRows();
    }

    private void AddCutModifier()
    {
        if (_viewport?.SelectedModifierOwner is not { } owner) return;
        var modifier = _viewport.AddCutModifier(owner.Node);
        modifier.Name = NextCutName(_viewport.GetModifiers(owner.Node));
        RefreshRows();
        // New modifier starts selected, per how you want it to behave.
        SelectedModifier = Rows.FirstOrDefault(r => ReferenceEquals(r.Modifier, modifier));
    }

    /// <summary>"Cut 01" if free, else the lowest "Cut NN" not already used by a sibling Cut modifier.</summary>
    private static string NextCutName(IReadOnlyList<IModifier> siblings)
    {
        var used = new HashSet<int>();
        foreach (var m in siblings)
            if (m is CutModifier && m.Name.StartsWith("Cut ", StringComparison.Ordinal)
                && int.TryParse(m.Name.AsSpan(4), out var n))
                used.Add(n);

        int next = 1;
        while (used.Contains(next)) next++;
        return $"Cut {next:D2}";
    }

    private void Delete(ModifierRowViewModel? row)
    {
        if (row is null || _viewport?.SelectedModifierOwner is not { } owner) return;
        _viewport.RemoveModifier(owner.Node, row.Modifier);
        row.PropertyChanged -= OnRowPropertyChanged;
        if (ReferenceEquals(SelectedModifier, row)) SelectedModifier = null; // also triggers the redraw
        Rows.Remove(row);
        _viewport.NotifyRenderNeeded();
    }

    /// <summary>Any change to a row (settings, enabled, name) can affect its viewport preview.</summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e) => _viewport?.NotifyRenderNeeded();

    // -- Drag-to-reorder ---------------------------------------------------------
    // A drag tracks a "gap index" — a position among the ORIGINAL (pre-removal) rows,
    // 0..Rows.Count, where 0 = before the first row and Rows.Count = after the last.
    // The drop-line indicator shows which gap is currently targeted; committing the
    // drag converts that gap into an actual list index (see GapToIndex).

    private ModifierRowViewModel? _draggingRow;

    internal void BeginDrag(ModifierRowViewModel row) => _draggingRow = row;

    /// <summary>Call while the pointer moves during a drag to update the drop-line indicator.</summary>
    internal void UpdateDragGap(int gapIndex)
    {
        ClearDropLines();
        if (Rows.Count == 0) return;
        gapIndex = Math.Clamp(gapIndex, 0, Rows.Count);
        if (gapIndex < Rows.Count) Rows[gapIndex].ShowDropLineAbove = true;
        else Rows[^1].ShowDropLineBelow = true;
    }

    /// <summary>Call on pointer release to commit the drag at the given gap.</summary>
    internal void EndDrag(int gapIndex)
    {
        ClearDropLines();
        var dragging = _draggingRow;
        _draggingRow = null;
        if (dragging is null || _viewport?.SelectedModifierOwner is not { } owner) return;

        int fromIndex = Rows.IndexOf(dragging);
        if (fromIndex < 0) return;
        int toIndex = GapToIndex(gapIndex, fromIndex, Rows.Count);
        if (toIndex == fromIndex) return;

        Rows.Move(fromIndex, toIndex);
        _viewport.MoveModifier(owner.Node, fromIndex, toIndex);
    }

    /// <summary>Call if a drag is abandoned (e.g. pointer capture lost) without a valid drop.</summary>
    internal void CancelDrag()
    {
        ClearDropLines();
        _draggingRow = null;
    }

    /// <summary>
    /// Converts a gap position in the original (pre-removal) list — 0..count — into the
    /// destination index once <paramref name="fromIndex"/> has been removed from the list.
    /// </summary>
    internal static int GapToIndex(int gapIndex, int fromIndex, int count)
    {
        int toIndex = Math.Clamp(gapIndex, 0, count);
        if (toIndex > fromIndex) toIndex--;
        return Math.Clamp(toIndex, 0, Math.Max(count - 1, 0));
    }

    private void ClearDropLines()
    {
        foreach (var r in Rows) { r.ShowDropLineAbove = false; r.ShowDropLineBelow = false; }
    }

    private void RefreshRows()
    {
        foreach (var r in Rows) r.PropertyChanged -= OnRowPropertyChanged;
        Rows.Clear();
        SelectedModifier = null;
        OnPropertyChanged(nameof(HasOwner));
        (AddCutModifierCommand as RelayCommand)?.RaiseCanExecuteChanged();

        if (_viewport?.SelectedModifierOwner is not { } owner) return;
        foreach (var modifier in _viewport.GetModifiers(owner.Node))
        {
            var row = new ModifierRowViewModel(modifier, owner.Node, _viewport);
            row.PropertyChanged += OnRowPropertyChanged;
            Rows.Add(row);
        }
    }
}
