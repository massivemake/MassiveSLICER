namespace MassiveSlicer.Core.Models;

/// <summary>Identifies one of the built-in navigation presets.</summary>
public enum NavigationPresetId
{
    /// <summary>Rhino-style: middle = pan, right = orbit, scroll = zoom.</summary>
    Rhino,
    /// <summary>Plasticity-style: middle = pan, right = orbit, scroll = zoom.</summary>
    Plasticity,
    /// <summary>Blender-style: middle = orbit, Shift+middle = pan, scroll = zoom.</summary>
    Blender,
    /// <summary>Maya-style: Alt+left = orbit, Alt+middle = pan, Alt+right = zoom.</summary>
    Maya,
    /// <summary>Mol3D-style: left = orbit, middle = pan, scroll = zoom.</summary>
    Mol3D,
    /// <summary>3ds Max-style: middle = pan, Alt+middle = orbit, scroll = zoom.</summary>
    Max3ds,
    /// <summary>Fusion 360-style: middle = pan, Shift+middle = orbit, scroll = zoom.</summary>
    Fusion360,
    /// <summary>Touchpad optimised: two-finger = pan, two-finger+Ctrl = orbit.</summary>
    Touchpad
}

/// <summary>
/// Describes a named navigation preset: what mouse actions drive orbit, pan, and zoom.
/// Used for display in the Preferences window; actual input routing is applied by the
/// viewport input handler.
/// </summary>
public sealed record NavigationPreset(
    NavigationPresetId Id,
    string Name,
    string OrbitBinding,
    string PanBinding,
    string ZoomBinding)
{
    /// <summary>All built-in presets in display order.</summary>
    public static readonly IReadOnlyList<NavigationPreset> All =
    [
        new(NavigationPresetId.Rhino,      "Rhino",      "Right drag",       "Middle drag",       "Scroll"),
        new(NavigationPresetId.Plasticity, "Plasticity", "Right drag",       "Middle drag",       "Scroll"),
        new(NavigationPresetId.Blender,    "Blender",    "Middle drag",      "Shift + Middle",    "Scroll"),
        new(NavigationPresetId.Maya,       "Maya",       "Alt + Left drag",  "Alt + Middle drag", "Scroll"),
        new(NavigationPresetId.Mol3D,      "Mol3D",      "Left drag",        "Middle drag",       "Scroll"),
        new(NavigationPresetId.Max3ds,     "3ds Max",    "Alt + Middle",     "Middle drag",       "Scroll"),
        new(NavigationPresetId.Fusion360,  "Fusion 360", "Shift + Middle",   "Middle drag",       "Scroll"),
        new(NavigationPresetId.Touchpad,   "Touchpad",   "Two-finger + Ctrl","Two-finger pan",    "Pinch"),
    ];
}
