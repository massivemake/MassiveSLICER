namespace MassiveSlicer.App;

/// <summary>What a pointer press on the keyframe lane does.</summary>
internal enum KeyframeLaneGesture
{
    None = 0,
    SelectDiamond,
    SelectTail,
    MoveKey,
    ResizeTail,
}

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

    /// <summary>
    /// Ctrl-drag moves the key. Shift-drag sizes a tail. A plain press selects.
    /// Ctrl wins when both are held. A plain press on a tail selects that key
    /// and does not resize it. Shift on the diamond, when the tail is not the hit,
    /// selects the key. <paramref name="ctrlTarget"/> is the wider key-move hit.
    /// </summary>
    public static KeyframeLaneGesture Gesture(bool ctrl, bool shift, bool onDiamond, bool onTail, bool ctrlTarget)
    {
        if (ctrl && ctrlTarget) return KeyframeLaneGesture.MoveKey;
        if (shift && onTail) return KeyframeLaneGesture.ResizeTail;
        if (onDiamond) return KeyframeLaneGesture.SelectDiamond;
        if (onTail) return KeyframeLaneGesture.SelectTail;
        return KeyframeLaneGesture.None;
    }

    /// <summary>
    /// No key is selected, so the Y/X/Z sliders are the global baseline.
    /// They are not pinned to a key and they do not show the falloff.
    /// </summary>
    public static bool ShowBlend(bool hasOrientKeys, bool keySelected)
        => hasOrientKeys && !keySelected;

    /// <summary>
    /// A translate drag of the TCP stores how far the tip moved, and that shift
    /// is applied to the cuts. An orientation change, including a new key for a
    /// tilted toolhead, leaves the cuts where they are.
    /// </summary>
    public static bool StoresPositionOffset(bool translatedTcp) => translatedTcp;

    /// <summary>
    /// A slider edit does not insert a key. With keys on the path and none selected,
    /// the edit changes the global baseline.
    /// </summary>
    public static bool InsertAtPlayhead(bool hasKeys, bool keySelected, bool playheadOccupied)
    {
        _ = hasKeys;
        _ = keySelected;
        _ = playheadOccupied;
        return false;
    }
}
