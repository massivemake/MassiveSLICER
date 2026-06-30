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
    private readonly string? _displayName;

    public SceneNode Node { get; }
    public string Name => _displayName ?? Node.Name;
    /// <summary>When false the outliner hides the delete control (robot, beds, stands, etc.).</summary>
    public bool CanDelete { get; }
    /// <summary>Row click controls visibility (LFAM 3 toolheads); hides the eye toggle.</summary>
    public bool UsesExclusiveVisibility { get; }
    public bool ShowVisibilityToggle => !UsesExclusiveVisibility;
    public ICommand DeleteCommand { get; }
    public ICommand ToggleVisibleCommand { get; }
    public ICommand ReloadModelCommand { get; }
    public ICommand ReplaceModelCommand { get; }

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

    void SyncRowHighlighted() => IsRowHighlighted = _isScanMultiSelected || _isOutlinerSelected;

    public void AddChild(OutlinerItemViewModel child)
    {
        Children.Add(child);
        OnPropertyChanged(nameof(HasChildren));
    }

    public void RemoveChild(OutlinerItemViewModel child)
    {
        Children.Remove(child);
        OnPropertyChanged(nameof(HasChildren));
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
        ReloadModelCommand   = new RelayCommand(
            () => onReloadModel?.Invoke(this),
            () => onReloadModel is not null && (canReloadModel?.Invoke(this) ?? false));
        ReplaceModelCommand  = new RelayCommand(
            () => onReplaceModel?.Invoke(this),
            () => onReplaceModel is not null && (canReplaceModel?.Invoke(this) ?? false));
    }
}
