using System.Globalization;

namespace MassiveSlicer.Core.Utils;

/// <summary>
/// Evaluates what a user typed into one of the transform tool's number boxes, allowing simple
/// arithmetic as well as a plain value.
/// </summary>
/// <remarks>
/// <para>
/// Accepts <c>+ - x /</c> (and <c>*</c>, since anyone who has used a spreadsheet will reach for it),
/// so <c>45+90</c>, <c>90/2</c> and <c>30x3</c> all work in a field that otherwise just takes a
/// number. Left-to-right, no operator precedence: the boxes are for arithmetic on a single dimension,
/// where reading in order is what a person expects, not for algebra.
/// </para>
/// <para>
/// A leading <c>+</c> or <c>-</c> is a <em>sign</em>, never an operation against the field's previous
/// contents: <c>-90</c> means minus ninety. Deliberately so. Clicking a second time to place the
/// cursor leaves the existing text in the field, so arithmetic is written against a value the user
/// can see — <c>45+90</c>, not a bare <c>+90</c> that silently reaches for a number no longer on
/// screen. Making <c>+90</c> relative would also have forced <c>-90</c> to be relative for
/// consistency, which contradicts a single click selecting the whole value so that typing replaces it.
/// </para>
/// <para>
/// <c>x</c> is treated as multiplication rather than as a unit or axis label. That is safe here
/// because these fields only ever hold plain numbers, never expressions like <c>3x</c> meaning
/// three of something.
/// </para>
/// </remarks>
public static class NumericEntryExpression
{
    /// <summary>
    /// Evaluates <paramref name="text"/>. Returns <c>false</c> and leaves <paramref name="result"/>
    /// at <paramref name="current"/> when the text is empty, malformed, or works out to something not
    /// finite — so a fumbled entry leaves the part alone instead of moving it somewhere nonsensical.
    /// </summary>
    public static bool TryEvaluate(string? text, double current, out double result)
    {
        result = current;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Strip all whitespace so "45 + 90" behaves like "45+90".
        var s = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (s.Length == 0) return false;

        if (!ReadNumber(s, 0, out double acc, out int i)) return false;

        while (i < s.Length)
        {
            char op = s[i];
            if (!IsOperator(op)) return false;
            i++;
            if (!ReadNumber(s, i, out double rhs, out int next)) return false;
            i = next;

            switch (op)
            {
                case '+': acc += rhs; break;
                case '-': acc -= rhs; break;
                case 'x':
                case 'X':
                case '*': acc *= rhs; break;
                case '/':
                    // Division by zero would produce infinity and then be rejected below, but
                    // rejecting it here says "bad entry" rather than "bad result".
                    if (rhs == 0d) return false;
                    acc /= rhs;
                    break;
                default: return false;
            }
        }

        if (double.IsNaN(acc) || double.IsInfinity(acc)) return false;
        result = acc;
        return true;
    }

    private static bool IsOperator(char c)
        => c is '+' or '-' or '/' or 'x' or 'X' or '*';

    /// <remarks>
    /// A sign directly after an operator is accepted, so <c>90+-2</c> is 88 and a fumbled double
    /// keypress <c>90++2</c> is 92 — the same as a spreadsheet, and unambiguous either way.
    /// </remarks>
    private static bool ReadNumber(string s, int start, out double value, out int next)
    {
        value = 0d;
        next  = start;
        int i = start;

        if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;

        int digitsStart = i;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.' || s[i] == ',')) i++;
        if (i == digitsStart) return false;

        // Accept a comma as a decimal separator — a European keyboard habit that would otherwise
        // silently truncate a value.
        var token = s[start..i].Replace(',', '.');
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;

        next = i;
        return true;
    }
}
