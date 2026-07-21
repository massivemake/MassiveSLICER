using Avalonia.Controls;
using Avalonia.Input;

namespace MassiveSlicer.App;

/// <summary>
/// Opt-in UX fix for numeric-style <see cref="TextBox"/> fields (tag with the "NumericCommit"
/// style class): click-to-focus selects the existing text for full replacement instead of
/// dropping the caret mid-string, and Enter commits (the field's binding should use
/// <c>UpdateSourceTrigger=LostFocus</c>) and releases focus, instead of the value re-formatting
/// itself on every keystroke and fighting the caret. Scoped to the opt-in class rather than every
/// <see cref="TextBox"/> app-wide — some existing multi-line template editors reuse the same base
/// "PanelTextBox" style and must keep normal caret/Enter behavior.
/// </summary>
internal static class NumericFieldUx
{
    private const string CommitClass = "NumericCommit";

    /// <summary>Installs the global class handlers. Call once at startup.</summary>
    public static void Install()
    {
        InputElement.GotFocusEvent.AddClassHandler<TextBox>((tb, _) =>
        {
            if (tb.Classes.Contains(CommitClass))
                tb.SelectAll();
        });

        InputElement.KeyDownEvent.AddClassHandler<TextBox>((tb, e) =>
        {
            if (e.Key != Key.Enter || !tb.Classes.Contains(CommitClass)) return;
            (TopLevel.GetTopLevel(tb) as IInputElement)?.Focus();
            e.Handled = true;
        });
    }
}
