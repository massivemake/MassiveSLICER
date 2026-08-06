namespace MassiveSlicer.Core.Models;

/// <summary>
/// User-editable KRL SRC post-processing options applied during export.
/// </summary>
public sealed class KrlPostProcessSettings
{
    /// <summary>
    /// Header template inserted after <c>DEF program ()</c>. Supports
    /// <see cref="IO.KrlExporter"/> placeholders such as {{PROGRAM_NAME}}.
    /// Empty = built-in default.
    /// </summary>
    public string HeaderText { get; set; } = "";

    /// <summary>
    /// Footer template appended before file end. Empty = built-in default.</summary>
    public string FooterText { get; set; } = "";

    /// <summary>
    /// Operator-saved header default restored by "Reset to default".
    /// Empty = fall back to the built-in <see cref="IO.KrlExporter.DefaultHeaderTemplate"/>.
    /// </summary>
    public string DefaultHeaderText { get; set; } = "";

    /// <summary>
    /// Operator-saved footer default restored by "Reset to default".
    /// Empty = fall back to the built-in <see cref="IO.KrlExporter.DefaultFooterTemplate"/>.
    /// </summary>
    public string DefaultFooterText { get; set; } = "";
}