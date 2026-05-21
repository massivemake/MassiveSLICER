using System.Collections.ObjectModel;
using System.Windows.Input;
using MassiveSlicer.Commands;
using MassiveSlicer.Viewport.Scene;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Manages state for the left workspace panel, which contains the robot cell
/// selector and a tab bar switching between the scene Outliner and the Assets browser.
/// </summary>
public sealed class LeftPanelViewModel : ViewModelBase
{
    // ── Cell selector ─────────────────────────────────────────────────────────

    private readonly List<string> _cellPaths = [];

    /// <summary>Display names for all discovered cells, bound to the cell ComboBox.</summary>
    public ObservableCollection<string> CellNames { get; } = [];

    private int _selectedCellIndex = -1;

    /// <summary>Index of the active cell in <see cref="CellNames"/>.</summary>
    public int SelectedCellIndex
    {
        get => _selectedCellIndex;
        set
        {
            if (!SetField(ref _selectedCellIndex, value)) return;
            if ((uint)value < (uint)_cellPaths.Count)
                OnCellSelected?.Invoke(_cellPaths[value]);
        }
    }

    /// <summary>
    /// Callback invoked when the user picks a different cell. Wired by
    /// <c>MainWindow</c> to trigger an async cell load and scene swap.
    /// </summary>
    internal Action<string>? OnCellSelected { get; set; }

    /// <summary>
    /// Populates the cell selector. Selecting index 0 immediately fires
    /// <see cref="OnCellSelected"/> so the first cell is loaded automatically.
    /// Wire <see cref="OnCellSelected"/> before calling this method.
    /// </summary>
    public void SetCells(IEnumerable<(string Name, string Path)> cells)
    {
        _cellPaths.Clear();
        CellNames.Clear();
        foreach (var (name, path) in cells)
        {
            _cellPaths.Add(path);
            CellNames.Add(name);
        }
        if (_cellPaths.Count > 0)
            SelectedCellIndex = 0;
    }


    // ── Outliner items ────────────────────────────────────────────────────────

    private ObservableCollection<OutlinerItemViewModel>? _outlinerItems;

    /// <summary>User scene objects shown in the Outliner tab. Wired by MainWindowViewModel.</summary>
    public ObservableCollection<OutlinerItemViewModel>? OutlinerItems
    {
        get => _outlinerItems;
        internal set => SetField(ref _outlinerItems, value);
    }

    // ── Tab management ────────────────────────────────────────────────────────

    private LeftPanelTab _activeTab = LeftPanelTab.Outliner;

    /// <summary>The currently visible tab in the left panel.</summary>
    public LeftPanelTab ActiveTab
    {
        get => _activeTab;
        set
        {
            if (!SetField(ref _activeTab, value)) return;
            OnPropertyChanged(nameof(IsOutlinerTabActive));
            OnPropertyChanged(nameof(IsAssetsTabActive));
            OnPropertyChanged(nameof(IsViewportTabActive));
        }
    }

    public bool IsOutlinerTabActive  => _activeTab == LeftPanelTab.Outliner;
    public bool IsAssetsTabActive    => _activeTab == LeftPanelTab.Assets;
    public bool IsViewportTabActive  => _activeTab == LeftPanelTab.Viewport;

    private bool _isCollapsed;

    /// <summary>
    /// When <c>true</c> the panel is collapsed to a thin strip; the splitter
    /// and child content are hidden.
    /// </summary>
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set => SetField(ref _isCollapsed, value);
    }

    /// <summary>Switches the active tab. Parameter is the string name of a <see cref="LeftPanelTab"/> value.</summary>
    public ICommand SetTabCommand { get; }

    /// <summary>Initialises commands.</summary>
    public LeftPanelViewModel()
    {
        SetTabCommand = new RelayCommand<string>(name =>
        {
            if (Enum.TryParse<LeftPanelTab>(name, out var tab))
                ActiveTab = tab;
        });
    }
}
