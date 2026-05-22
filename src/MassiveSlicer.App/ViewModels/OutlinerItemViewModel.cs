using MassiveSlicer.Commands;
using MassiveSlicer.Viewport.Scene;
using MassiveSlicer.ViewModels.Base;
using System.Windows.Input;

namespace MassiveSlicer.ViewModels;

public sealed class OutlinerItemViewModel : ViewModelBase
{
    private readonly Action _notifyRender;

    public SceneNode Node { get; }
    public string Name => Node.Name;
    public ICommand DeleteCommand { get; }

    public bool Visible
    {
        get => Node.Visible;
        set
        {
            if (Node.Visible == value) return;
            Node.Visible = value;
            OnPropertyChanged();
            _notifyRender();
        }
    }

    internal OutlinerItemViewModel(SceneNode node, Action notifyRender, Action<OutlinerItemViewModel> onDelete)
    {
        Node = node;
        _notifyRender = notifyRender;
        DeleteCommand = new RelayCommand(() => onDelete(this));
    }
}
