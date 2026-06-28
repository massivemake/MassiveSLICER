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
        Func<OutlinerItemViewModel, bool>? canReloadModel = null,
        Func<OutlinerItemViewModel, bool>? canReplaceModel = null,
        Action<OutlinerItemViewModel>? onReloadModel = null,
        Action<OutlinerItemViewModel>? onReplaceModel = null)
    {
        Node          = node;
        _notifyRender = notifyRender;
        _onHide       = onHide;
        _displayName  = displayName;
        CanDelete            = canDelete;
        DeleteCommand        = new RelayCommand(() => onDelete(this), () => canDelete);
        ToggleVisibleCommand = new RelayCommand(() => Visible = !Visible);
        ReloadModelCommand   = new RelayCommand(
            () => onReloadModel?.Invoke(this),
            () => onReloadModel is not null && (canReloadModel?.Invoke(this) ?? false));
        ReplaceModelCommand  = new RelayCommand(
            () => onReplaceModel?.Invoke(this),
            () => onReplaceModel is not null && (canReplaceModel?.Invoke(this) ?? false));
    }
}
