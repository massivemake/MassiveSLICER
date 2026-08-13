using System.Collections.ObjectModel;
using System.IO;
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
    // -- Cell selector ---------------------------------------------------------

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
            UpdateCellIcon();
            if ((uint)value < (uint)_cellPaths.Count)
                OnCellSelected?.Invoke(_cellPaths[value]);
        }
    }

    private Avalonia.Media.Imaging.Bitmap? _selectedCellIcon;

    /// <summary>Line-art icon for the active cell (assets/images/*<name>*.png), or null.</summary>
    public Avalonia.Media.Imaging.Bitmap? SelectedCellIcon
    {
        get => _selectedCellIcon;
        private set
        {
            var old = _selectedCellIcon;
            if (!SetField(ref _selectedCellIcon, value)) return;
            OnPropertyChanged(nameof(HasCellIcon));
            old?.Dispose();
        }
    }

    public bool HasCellIcon => _selectedCellIcon is not null;

    /// <summary>Finds the newest PNG in assets/images whose name contains the cell name.</summary>
    private void UpdateCellIcon()
    {
        Avalonia.Media.Imaging.Bitmap? bmp = null;
        try
        {
            if ((uint)_selectedCellIndex < (uint)_cellPaths.Count
                && _selectedCellIndex < CellNames.Count)
            {
                string name    = CellNames[_selectedCellIndex];
                string cellDir = Path.GetDirectoryName(Path.GetFullPath(_cellPaths[_selectedCellIndex]))!;
                string images  = Path.GetFullPath(Path.Combine(cellDir, "..", "..", "images"));
                if (Directory.Exists(images) && name.Length > 0)
                {
                    string compact = name.Replace(" ", "");
                    var match = Directory.EnumerateFiles(images, "*.png")
                        .Where(f =>
                        {
                            var stem = Path.GetFileNameWithoutExtension(f);
                            return stem.Contains(name, StringComparison.OrdinalIgnoreCase)
                                || stem.Replace(" ", "").Contains(compact, StringComparison.OrdinalIgnoreCase);
                        })
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (match is not null)
                        bmp = new Avalonia.Media.Imaging.Bitmap(match);
                }
            }
        }
        catch { /* missing/corrupt icon — just show nothing */ }
        SelectedCellIcon = bmp;
    }

    /// <summary>
    /// Callback invoked when the user picks a different cell. Wired by
    /// <c>MainWindow</c> to trigger an async cell load and scene swap.
    /// </summary>
    internal Action<string>? OnCellSelected { get; set; }

    /// <summary>Returns the cell JSON path at <paramref name="index"/>, or null.</summary>
    internal string? GetCellPath(int index)
        => (uint)index < (uint)_cellPaths.Count ? _cellPaths[index] : null;

    /// <summary>All discovered cell JSON paths (for workspace cell resolution).</summary>
    internal IReadOnlyList<string> DiscoveredCellPaths => _cellPaths;

    /// <summary>Finds the index of a cell by path (case-insensitive full-path match).</summary>
    internal int FindCellIndex(string cellPath)
    {
        string full = Path.GetFullPath(cellPath);
        for (int i = 0; i < _cellPaths.Count; i++)
        {
            if (string.Equals(Path.GetFullPath(_cellPaths[i]), full, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

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
    }


    // -- Outliner items --------------------------------------------------------

    private ObservableCollection<OutlinerItemViewModel>? _outlinerItems;

    /// <summary>User scene objects shown in the Outliner tab. Wired by MainWindowViewModel.</summary>
    public ObservableCollection<OutlinerItemViewModel>? OutlinerItems
    {
        get => _outlinerItems;
        internal set
        {
            if (!SetField(ref _outlinerItems, value)) return;
            HookOutlinerForFilter(value);
            RebuildFilteredToolpaths();
            OnPropertyChanged(nameof(VisibleOutlinerRows));
        }
    }

    // -- Toolpaths-only filter ---------------------------------------------------

    private bool _filterToolpathsOnly;

    /// <summary>Outliner filter: show a flat, curated list of toolpaths only —
    /// range shift+click then walks just the toolpaths.</summary>
    public bool FilterToolpathsOnly
    {
        get => _filterToolpathsOnly;
        set
        {
            if (!SetField(ref _filterToolpathsOnly, value)) return;
            RebuildFilteredToolpaths();
            OnPropertyChanged(nameof(VisibleOutlinerRows));
        }
    }

    /// <summary>Flat list of every model's toolpath rows (outliner order).</summary>
    public ObservableCollection<OutlinerItemViewModel> FilteredToolpaths { get; } = [];

    /// <summary>What the outliner list actually shows: full hierarchy or the toolpath filter.</summary>
    public System.Collections.IEnumerable? VisibleOutlinerRows
        => _filterToolpathsOnly ? FilteredToolpaths : _outlinerItems;

    private readonly HashSet<OutlinerItemViewModel> _filterHooked = [];

    private void HookOutlinerForFilter(ObservableCollection<OutlinerItemViewModel>? items)
    {
        if (items is null) return;
        items.CollectionChanged += (_, _) =>
        {
            foreach (var it in items) HookItemChildren(it);
            RebuildFilteredToolpaths();
        };
        foreach (var it in items) HookItemChildren(it);
    }

    private void HookItemChildren(OutlinerItemViewModel item)
    {
        if (!_filterHooked.Add(item)) return;
        item.Children.CollectionChanged += (_, _) => RebuildFilteredToolpaths();
    }

    private void RebuildFilteredToolpaths()
    {
        if (!_filterToolpathsOnly && FilteredToolpaths.Count == 0) return;
        FilteredToolpaths.Clear();
        if (_outlinerItems is null) return;
        foreach (var top in _outlinerItems)
            foreach (var child in top.Children)
                if (child.IsToolpath)
                    FilteredToolpaths.Add(child);
    }

    // -- Tab management --------------------------------------------------------

    private LeftPanelTab _activeTab = LeftPanelTab.Outliner;

    /// <summary>The currently visible tab in the left panel.</summary>
    public LeftPanelTab ActiveTab
    {
        get => _activeTab;
        set
        {
            if (!SetField(ref _activeTab, value)) return;
            OnPropertyChanged(nameof(IsOutlinerTabActive));
            OnPropertyChanged(nameof(IsViewportTabActive));
            OnPropertyChanged(nameof(IsRobotTabActive));
        }
    }

    public bool IsOutlinerTabActive  => _activeTab == LeftPanelTab.Outliner;
    public bool IsViewportTabActive  => _activeTab == LeftPanelTab.Viewport;
    public bool IsRobotTabActive     => _activeTab == LeftPanelTab.Robot;

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
