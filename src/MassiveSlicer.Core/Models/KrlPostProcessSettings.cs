namespace MassiveSlicer.Core.Models;

/// <summary>
/// User-editable KRL SRC post-processing options applied during export.
/// </summary>
public sealed class KrlPostProcessSettings
{
    /// <summary>
    /// When true, travel moves emit <c>$ANOUT[4] = 0</c> before the LIN so the extruder
    /// is off while crossing the print (instead of relying on a delayed TRIGGER).
    /// </summary>
    public bool TravelSetAnout4Zero { get; set; } = true;

    /// <summary>
    /// Header template inserted after <c>DEF program ()</c>. Supports
    /// <see cref="IO.KrlExporter"/> placeholders such as {{PROGRAM_NAME}}.
    /// Empty = built-in default.
    /// </summary>
    public string HeaderText { get; set; } = "";

    /// <summary>
    /// Footer template appended before file end. Empty = built-in default.</summary>
    public string FooterText { get; set; } = "";
}