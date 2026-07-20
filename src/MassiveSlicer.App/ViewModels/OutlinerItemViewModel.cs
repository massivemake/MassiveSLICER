using System.Collections.ObjectModel;
using MassiveSlicer.Commands;
using MassiveSlicer.Viewport.Scene;
using MassiveSlicer.ViewModels.Base;
using System.Windows.Input;

namespace MassiveSlicer.ViewModels;

public sealed class OutlinerItemViewModel : ViewModelBase
{
    private readonly Action _notifyRender;
    private readonly Action? _onHide;
    private string? _displayName;

    public SceneNode Node { get; }
    public string Name => _displayName ?? Node.Name;

    // -- Type badge -----------------------------------------------------------
    public string TypeIcon =>
        IsPiecesGroup ? "mdi-source-fork"
        : IsModifiersGroup ? "mdi-layers-outline"
        : IsModifier ? "mdi-crop"
        : IsEffector ? "mdi-white-balance-sunny"
        : IsToolpath ? "mdi-reorder-horizontal"
        : MassiveSlicer.App.OutlinerModelOps.IsScanItem(this) ? "mdi-cube-scan"
        : Name is "Robot Root" or "Robot Arm" ? "mdi-robot-industrial"
        : Name.Contains("Bed", StringComparison.OrdinalIgnoreCase) ? "mdi-view-grid-outline"
        : "mdi-cube-outline";

    public string TypeColor =>
        IsPiecesGroup ? "#4FC3B0"
        : IsModifiersGroup ? "#E8A33D"
        : IsModifier ? "#E8A33D"
        : IsEffector ? "#A6DE38"
        : IsToolpath ? "#37C871"
        : MassiveSlicer.App.OutlinerModelOps.IsScanItem(this) ? "#B07BF7"
        : Name is "Robot Root" or "Robot Arm" ? "#8B93A1"
        : Name.Contains("Bed", StringComparison.OrdinalIgnoreCase) ? "#8B93A1"
        : "#4AA3FF";

    public string TypeTip =>
        IsPiecesGroup ? "Applied pieces — result of running the modifier stack, from an Apply press"
        : IsModifiersGroup ? "Modifier stack — select to move/rotate all together, or Apply"
        : IsModifier ? "Cut modifier"
        : IsEffector ? "Effector" : IsToolpath ? "Toolpath"
        : MassiveSlicer.App.OutlinerModelOps.IsScanItem(this) ? "Scan"
        : Name is "Robot Root" or "Robot Arm" ? "Robot"
        : Name.Contains("Bed", StringComparison.OrdinalIgnoreCase) ? "Print bed"
        : "3D model";

    // -- Rename (double-click or context menu) ---------------------------------
    private bool _isRenaming;
    public bool IsRenaming
    {
        get => _isRenaming;
        private set { if (_isRenaming != value) { _isRenaming = value; OnPropertyChanged(); } }
    }

    private string _editName = "";
    public string EditName
    {
        get => _editName;
        set { if (_editName != value) { _editName = value; OnPropertyChanged(); } }
    }

    public void BeginRename() { EditName = Name; IsRenaming = true; }

    public void CommitRename()
    {
        var t = _editName?.Trim();
        if (!string.IsNullOrWhiteSpace(t) && t != Name)
        {
            Node.Name    = t;
            _displayName = null;   // display follows the node name after a rename
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(TypeIcon));
            OnPropertyChanged(nameof(TypeColor));
            OnPropertyChanged(nameof(TypeTip));
        }
        IsRenaming = false;
    }

    public void CancelRename() => IsRenaming = false;
    /// <summary>When false the outliner hides the delete control (robot, beds, stands, etc.).</summary>
    public bool CanDelete { get; }
    /// <summary>Row click controls visibility (LFAM 3 toolheads); hides the eye toggle.</summary>
    public bool UsesExclusiveVisibility { get; }
    public bool ShowVisibilityToggle => !UsesExclusiveVisibility;
    public ICommand DeleteCommand { get; }
    public ICommand ToggleVisibleCommand { get; }
    public ICommand ReloadModelCommand { get; }
    public ICommand ReplaceModelCommand { get; }

    /// <summary>True for toolpath entries (set by RegisterToolpathInOutliner).</summary>
    public bool IsToolpath { get; set; }

    /// <summary>True for live-effector handles — excluded from slicing/model resolution.</summary>
    public bool IsEffector { get; set; }

    /// <summary>True for a modifier's own row, nested under its owning mesh's Modifiers group.</summary>
    public bool IsModifier { get; set; }

    /// <summary>True for a mesh's "Modifiers" group row itself — selecting it arms the gizmo for
    /// the whole stack at once (real SceneNode parent, so children move/rotate together for free)
    /// and is where the Apply action lives.</summary>
    public bool IsModifiersGroup { get; set; }

    /// <summary>True for a fresh, top-level sibling group of Apply results — same name as the
    /// master (auto-numbered on repeat Apply), distinguished only by icon. Purely an outliner
    /// grouping — pieces inside are NOT real scene children of it, so each stays independently
    /// movable afterward.</summary>
    public bool IsPiecesGroup { get; set; }

    private bool _isLocked;
    /// <summary>Locked rows can't be selected (outliner or viewport); toggled via the padlock.</summary>
    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (_isLocked == value) return;
            _isLocked = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LockIcon));
            OnPropertyChanged(nameof(NameOpacity));
        }
    }

    public string LockIcon    => _isLocked ? "mdi-lock" : "mdi-lock-open-variant-outline";
    public double NameOpacity => _isLocked ? 0.5 : 1.0;
    public ICommand ToggleLockCommand { get; private set; } = null!;

    private bool _isExpanded = true;
    /// <summary>Whether this row's children are shown (chevron toggle).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExpandGlyph));
            OnPropertyChanged(nameof(ShowChildren));
        }
    }

    public string ExpandGlyph  => _isExpanded ? "mdi-chevron-down" : "mdi-chevron-right";
    public bool   ShowChildren => HasChildren && _isExpanded;
    public ICommand ToggleExpandCommand { get; private set; } = null!;

    public bool Visible
    {
        get => Node.Visible;
        set
        {
            if (Node.Visible == value) return;
            Node.Visible = value;
            OnPropertyChanged();
            if (!value) _onHide?.Invoke();
            _notifyRender();
        }
    }

    /// <summary>
    /// Scene node visibility was changed outside the outliner (e.g. 2D slice view
    /// hides the robot). Refresh the bound eye-icon without firing hide callbacks.
    /// </summary>
    public void NotifyVisibilityFromScene() => OnPropertyChanged(nameof(Visible));

    public ObservableCollection<OutlinerItemViewModel> Children { get; } = [];
    public bool HasChildren => Children.Count > 0;

    private bool _isScanMultiSelected;
    private bool _isOutlinerSelected;
    private bool _isRowHighlighted;

    /// <summary>True when this scan row is part of a multi-selected merge set.</summary>
    public bool IsScanMultiSelected
    {
        get => _isScanMultiSelected;
        internal set
        {
            if (_isScanMultiSelected == value) return;
            _isScanMultiSelected = value;
            OnPropertyChanged();
            SyncRowHighlighted();
        }
    }

    /// <summary>True when this row matches the active viewport / outliner selection.</summary>
    public bool IsOutlinerSelected
    {
        get => _isOutlinerSelected;
        internal set
        {
            if (_isOutlinerSelected == value) return;
            _isOutlinerSelected = value;
            OnPropertyChanged();
            SyncRowHighlighted();
        }
    }

    /// <summary>Bound to the outliner row highlight brushes (scan or import selection).</summary>
    public bool IsRowHighlighted
    {
        get => _isRowHighlighted;
        private set
        {
            if (_isRowHighlighted == value) return;
            _isRowHighlighted = value;
            OnPropertyChanged();
        }
    }

    private bool _isSequenceSelected;

    /// <summary>Row is part of the shift+click toolpath sequence selection.</summary>
    public bool IsSequenceSelected
    {
        get => _isSequenceSelected;
        set
        {
            if (_isSequenceSelected == value) return;
            _isSequenceSelected = value;
            OnPropertyChanged();
            SyncRowHighlighted();
        }
    }

    void SyncRowHighlighted()
        => IsRowHighlighted = _isScanMultiSelected || _isOutlinerSelected || _isSequenceSelected;

    public void AddChild(OutlinerItemViewModel child)
    {
        Children.Add(child);
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(ShowChildren));
    }

    public void InsertChild(OutlinerItemViewModel child, int index)
    {
        Children.Insert(Math.Clamp(index, 0, Children.Count), child);
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(ShowChildren));
    }

    public void RemoveChild(OutlinerItemViewModel child)
    {
        Children.Remove(child);
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(ShowChildren));
    }

    internal void RefreshModelCommands()
    {
        (ReloadModelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ReplaceModelCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    internal OutlinerItemViewModel(
        SceneNode node,
        Action notifyRender,
        Action<OutlinerItemViewModel> onDelete,
        Action? onHide = null,
        string? displayName = null,
        bool canDelete = true,
        bool usesExclusiveVisibility = false,
        Func<OutlinerItemViewModel, bool>? canReloadModel = null,
        Func<OutlinerItemViewModel, bool>? canReplaceModel = null,
        Action<OutlinerItemViewModel>? onReloadModel = null,
        Action<OutlinerItemViewModel>? onReplaceModel = null)
    {
        Node          = node;
        _notifyRender = notifyRender;
        _onHide       = onHide;
        _displayName  = displayName;
        CanDelete                 = canDelete;
        UsesExclusiveVisibility   = usesExclusiveVisibility;
        DeleteCommand             = new RelayCommand(() => onDelete(this), () => canDelete);
        ToggleVisibleCommand = new RelayCommand(() => Visible = !Visible);
        ToggleLockCommand    = new RelayCommand(() => IsLocked = !IsLocked);
        ToggleExpandCommand  = new RelayCommand(() => IsExpanded = !IsExpanded);
        ReloadModelCommand   = new RelayCommand(
            () => onReloadModel?.Invoke(this),
            () => onReloadModel is not null && (canReloadModel?.Invoke(this) ?? false));
        ReplaceModelCommand  = new RelayCommand(
            () => onReplaceModel?.Invoke(this),
            () => onReplaceModel is not null && (canReplaceModel?.Invoke(this) ?? false));
    }
}
