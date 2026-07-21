using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MassiveSlicer.App.Views;

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
            if (!tb.Classes.Contains(CommitClass)) return;
            // The click that focuses the field also places the caret at the clicked character —
            // that happens AFTER GotFocus in the same input pass, so a synchronous SelectAll()
            // here gets immediately overwritten. Deferring to the next dispatcher pass (same
            // trick SliderTypeIn already uses) runs after the click has finished, so the
            // selection sticks.
            Dispatcher.UIThread.Post(() => tb.SelectAll());
        });

        InputElement.KeyDownEvent.AddClassHandler<TextBox>((tb, e) =>
        {
            if (e.Key != Key.Enter || !tb.Classes.Contains(CommitClass)) return;
            // Focusing the TopLevel (Window) itself doesn't reliably move focus away from a
            // child in Avalonia — a Window isn't a real focus target. ViewportView is the one
            // control in this app already set Focusable=true specifically to be a safe place to
            // send keyboard focus back to (see its own `this.Focus()` calls); reuse that instead.
            var viewport = TopLevel.GetTopLevel(tb)?.GetVisualDescendants().OfType<ViewportView>().FirstOrDefault();
            viewport?.Focus();
            e.Handled = true;
        });
    }
}
