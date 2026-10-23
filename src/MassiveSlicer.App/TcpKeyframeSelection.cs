namespace MassiveSlicer.App;

/// <summary>
/// A toolhead key stays selected only while the scene selection is that key's
/// toolpath or the current tool. Any other item releases it, so Delete acts
/// on the item the user just picked.
/// </summary>
internal static class TcpKeyframeSelection
{
    public static bool Keep(bool selectedIsActiveToolpath, bool selectedIsCurrentTool)
        => selectedIsActiveToolpath || selectedIsCurrentTool;

    /// <summary>A click on the diamond that is already selected clears that selection.</summary>
    public static bool ClickClearsSelection(int selectedIndex, int clickedIndex)
        => selectedIndex >= 0 && selectedIndex == clickedIndex;

    /// <summary>The Y/X/Z sliders follow the eased angle while no key is selected.</summary>
    public static bool ShowBlend(bool hasOrientKeys, bool keySelected)
        => hasOrientKeys && !keySelected;

    /// <summary>
    /// A slider edit with keys on the path and none selected inserts a key at the
    /// playhead when that move is empty. An occupied move updates the key there.
    /// </summary>
    public static bool InsertAtPlayhead(bool hasKeys, bool keySelected, bool playheadOccupied)
        => hasKeys && !keySelected && !playheadOccupied;
}
