using System.Windows.Input;
using MassiveSlicer.Commands;

namespace MassiveSlicer.ViewModels;

/// <summary>A recall button in the SCAN tab Quick Positions list.</summary>
public sealed class ScanQuickPositionItem
{
    public string Label { get; }
    public string WaypointName { get; }
    public ICommand MoveCommand { get; }

    public ScanQuickPositionItem(string label, string waypointName, Action<string> onMove)
    {
        Label        = label;
        WaypointName = waypointName;
        MoveCommand  = new RelayCommand(() => onMove(waypointName));
    }
}