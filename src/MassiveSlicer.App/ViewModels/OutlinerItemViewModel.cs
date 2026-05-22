using System.Collections.ObjectModel;
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

    internal OutlinerItemViewModel(SceneNode node, Action notifyRender, Action<OutlinerItemViewModel> onDelete)
    {
        Node = node;
        _notifyRender = notifyRender;
        DeleteCommand = new RelayCommand(() => onDelete(this));
    }
}
