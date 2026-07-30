using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MassiveSlicer.Core.Utils;

namespace MassiveSlicer.Controls;

/// <summary>
/// The number entry used by the transform toolbar's position, rotation and scale rows.
/// </summary>
/// <remarks>
/// <para>
/// Replaces <see cref="NumericUpDown"/>, which caused four separate complaints:
/// </para>
/// <list type="bullet">
/// <item><description>
/// It commits on every keystroke, so typing "90" rotated the part to 9° on the way. Here nothing is
/// applied until Enter or focus leaves.
/// </description></item>
/// <item><description>
/// It never selected the field's contents, so the old value had to be deleted by hand. Here the first
/// click selects the whole value including the decimals, so typing replaces it outright; a second
/// click drops the selection and places the caret where it was clicked, which is how arithmetic gets
/// written against a value still on screen.
/// </description></item>
/// <item><description>
/// It is a <c>ButtonSpinner</c> wrapped around an inner text box, and only the inner box handles a
/// pointer press. A click on the surrounding border or padding bubbled out to the viewport, whose
/// handler calls <c>Focus()</c> on itself and then runs its own click logic — stealing focus and
/// deselecting the part. Deriving straight from <see cref="TextBox"/> makes the entire control the
/// text input, so there is no dead border to fall through, and the press is marked handled either way.
/// </description></item>
/// <item><description>
/// It could not evaluate arithmetic. Text goes through <see cref="NumericEntryExpression"/>, so
/// <c>45+90</c>, <c>90/2</c> and <c>30x3</c> all work.
/// </description></item>
/// </list>
/// <para>
/// <see cref="Value"/> is the bound property and <see cref="TextBox.Text"/> is scratch space, which is
/// what keeps a half-typed number from reaching the part. While the box is unfocused its text tracks
/// <see cref="Value"/>, so the numbers count along live as a gizmo is dragged in the viewport.
/// </para>
/// </remarks>
public class TransformNumberBox : TextBox
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<TransformNumberBox, double>(
            nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>How the committed value is rendered back into the box.</summary>
    public static readonly StyledProperty<string> FormatStringProperty =
        AvaloniaProperty.Register<TransformNumberBox, string>(nameof(FormatString), "F2");

    /// <summary>The committed number. Only changes on Enter or when focus leaves.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string FormatString
    {
        get => GetValue(FormatStringProperty);
        set => SetValue(FormatStringProperty, value);
    }

    public TransformNumberBox()
    {
        AcceptsReturn = false;
        TextWrapping  = Avalonia.Media.TextWrapping.NoWrap;

        // Subscribed rather than overridden: the focus-event signatures differ between Avalonia
        // versions, and nothing here needs to run before the base implementation.
        GotFocus  += (_, _) => SelectAll();   // covers arriving by Tab as well as by click
        LostFocus += (_, _) => Commit();
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    // -- Click behaviour -------------------------------------------------------

    /// <summary>Set while the opening click of a focus-in gesture is still in flight, so its
    /// release can be suppressed too.</summary>
    private bool _openingClick;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // First click into the box selects everything, so the next keystroke overwrites — the same
        // thing that happens in a text editor.
        //
        // base is deliberately NOT called for that gesture, on press OR release. Its press handler
        // places a caret and arms a drag-selection anchored at the click point; its release handler
        // then finishes that drag against the anchor. Either one collapses the SelectAll — with base
        // on press the selection shrank to the digits after the decimal, and suppressing only the
        // press left the release to clear it entirely.
        if (!IsFocused)
        {
            _openingClick = true;
            Focus();
            SelectAllDeferred();
            e.Handled = true;
            return;
        }

        // Already focused: base places the caret where clicked and drops the selection, which is
        // what makes arithmetic against the visible value possible.
        base.OnPointerPressed(e);

        // Never let a press in the transform toolbar reach the viewport behind it, whichever part of
        // the box was hit. Without this the viewport takes focus back and deselects the part.
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_openingClick)
        {
            _openingClick = false;
            SelectAllDeferred();
            e.Handled = true;
            return;
        }

        base.OnPointerReleased(e);
        e.Handled = true;
    }

    /// <summary>
    /// Selects everything once the current input pass has finished. Posting rather than calling
    /// directly means the selection is applied last, after any focus bookkeeping the base control
    /// does on its way in, which would otherwise reset it.
    /// </summary>
    private void SelectAllDeferred()
        => Dispatcher.UIThread.Post(SelectAll, DispatcherPriority.Input);

    // -- Commit / revert -------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Commit();
                SelectAll();     // ready to be overwritten again
                e.Handled = true;
                return;

            case Key.Escape:
                SyncTextFromValue();
                SelectAll();
                e.Handled = true;
                return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Evaluates what was typed and, if it makes sense, applies it. A malformed entry puts the
    /// previous number back rather than moving the part somewhere unintended.
    /// </summary>
    private void Commit()
    {
        if (NumericEntryExpression.TryEvaluate(Text, Value, out double evaluated)
            && Math.Abs(evaluated - Value) > 1e-9)
            Value = evaluated;

        // Always re-render, whether the value changed, stayed the same, or the entry was rejected.
        // The property-changed path deliberately skips a focused box so a drag cannot overwrite
        // something half-typed — which meant that after pressing Enter the box went on showing the
        // expression ("100+50") until focus left, even though the part had already moved to 150.
        SyncTextFromValue();
    }

    // -- Keeping the text in step ---------------------------------------------

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Only refresh while the user is not in the box, so a viewport drag can count the numbers
        // along live without overwriting something half-typed.
        if ((change.Property == ValueProperty || change.Property == FormatStringProperty)
            && !IsFocused)
            SyncTextFromValue();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncTextFromValue();
    }

    private void SyncTextFromValue()
    {
        var rendered = Value.ToString(FormatString, System.Globalization.CultureInfo.InvariantCulture);
        if (Text != rendered) Text = rendered;
    }
}
