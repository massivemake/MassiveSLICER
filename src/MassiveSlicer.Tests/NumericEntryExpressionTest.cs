using MassiveSlicer.Core.Utils;

namespace MassiveSlicer.Tests;

public class NumericEntryExpressionTest
{
    private static double Eval(string text, double current = 45d)
    {
        Assert.True(NumericEntryExpression.TryEvaluate(text, current, out double r),
            $"'{text}' should have evaluated");
        return r;
    }

    private static void Rejected(string? text, double current = 45d)
    {
        Assert.False(NumericEntryExpression.TryEvaluate(text, current, out double r),
            $"'{text}' should have been rejected");
        Assert.Equal(current, r);   // a bad entry must leave the part alone
    }

    [Theory]
    [InlineData("90", 90d)]
    [InlineData("0", 0d)]
    [InlineData("-90", -90d)]
    [InlineData("+90", 90d)]
    [InlineData("12.5", 12.5d)]
    [InlineData("-0.25", -0.25d)]
    public void Plain_values_pass_straight_through(string text, double expected)
        => Assert.Equal(expected, Eval(text), 6);

    [Theory]
    [InlineData("45+90", 135d)]
    [InlineData("45-90", -45d)]
    [InlineData("30x3", 90d)]
    [InlineData("30X3", 90d)]
    [InlineData("30*3", 90d)]
    [InlineData("90/2", 45d)]
    [InlineData("1300/2", 650d)]
    public void Arithmetic_works(string text, double expected)
        => Assert.Equal(expected, Eval(text), 6);

    [Fact]
    public void Whitespace_is_ignored()
        => Assert.Equal(135d, Eval("45 + 90"), 6);

    [Fact]
    public void Evaluates_left_to_right_with_no_precedence()
    {
        // Deliberate: these boxes are for arithmetic on one dimension, where reading in order is
        // what a person expects. 2+3x4 is 20, not 14.
        Assert.Equal(20d, Eval("2+3x4"), 6);
        Assert.Equal(10d, Eval("100/5/2"), 6);
    }

    [Fact]
    public void A_leading_sign_sets_the_value_and_never_reaches_for_the_old_one()
    {
        // Typing -90 into a field showing 45 means minus ninety. Making +90 mean "add 90" would have
        // forced -90 to mean "subtract 90" for consistency, contradicting the rule that a single
        // click selects the whole value so typing replaces it. Arithmetic is written against a value
        // the user can still see: 45+90, not a bare +90.
        Assert.Equal(-90d, Eval("-90", current: 45d), 6);
        Assert.Equal(90d,  Eval("+90", current: 45d), 6);
    }

    [Fact]
    public void A_bare_operator_with_no_number_is_rejected()
    {
        Rejected("/2", current: 90d);
        Rejected("x2", current: 90d);
    }

    [Theory]
    [InlineData("90+-2", 88d)]
    [InlineData("90++2", 92d)]
    public void A_sign_after_an_operator_is_accepted(string text, double expected)
    {
        // Same as a spreadsheet, and unambiguous either way.
        Assert.Equal(expected, Eval(text), 6);
    }

    [Fact]
    public void A_subtraction_after_a_value_still_subtracts()
        => Assert.Equal(-45d, Eval("45-90", current: 0d), 6);

    [Fact]
    public void A_comma_reads_as_a_decimal_point()
    {
        // A European keyboard habit that would otherwise silently truncate the value.
        Assert.Equal(12.5d, Eval("12,5"), 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("90+")]
    [InlineData("+")]
    [InlineData("90/0")]
    [InlineData("9 0 deg")]
    public void Bad_entries_are_rejected_and_change_nothing(string? text)
        => Rejected(text);

    [Fact]
    public void A_result_that_is_not_finite_is_rejected()
    {
        // Guards against a typed expression quietly producing infinity and moving a part to nowhere.
        Rejected("1/0");
        Assert.False(NumericEntryExpression.TryEvaluate("1e400", 45d, out _));
    }
}
