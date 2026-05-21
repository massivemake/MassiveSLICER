using MassiveSlicer.Commands;
using MassiveSlicer.Viewport.Scene;
using MassiveSlicer.ViewModels.Base;
using System.Windows.Input;

namespace MassiveSlicer.ViewModels;

public sealed class OutlinerItemViewModel : ViewModelBase
{
    public SceneNode Node { get; }
    public string Name => Node.Name;
    public ICommand DeleteCommand { get; }

    internal OutlinerItemViewModel(SceneNode node, Action<OutlinerItemViewModel> onDelete)
    {
        Node = node;
        DeleteCommand = new RelayCommand(() => onDelete(this));
    }
}
