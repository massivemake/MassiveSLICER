using MassiveSlicer.Core.Units;

namespace MassiveSlicer.Tests;

/// <summary>Typing lengths with units into measurement fields.</summary>
public sealed class LengthInputTest
{
    [Theory]
    // No unit = millimetres, as always — including fractions.
    [InlineData("254", 254)]
    [InlineData("12.5", 12.5)]
    [InlineData(".5", 0.5)]
    [InlineData("-40", -40)]
    [InlineData("5 1/2", 5.5)]
    [InlineData("5-1/2", 5.5)]
    [InlineData("250mm", 250)]
    // Inches.
    [InlineData("10in", 254)]
    [InlineData("10 in", 254)]
    [InlineData("10inch", 254)]
    [InlineData("10 inches", 254)]
    [InlineData("10\"", 254)]
    [InlineData("10''", 254)]
    [InlineData("5 1/2\"", 139.7)]
    [InlineData("5-1/2 in", 139.7)]
    [InlineData("5 - 1/2in", 139.7)]
    [InlineData("3/4\"", 19.05)]
    [InlineData("-2in", -50.8)]
    [InlineData("10”", 254)]          // curly quote from a phone
    // Feet, and feet + inches.
    [InlineData("2ft", 609.6)]
    [InlineData("2'", 609.6)]
    [InlineData("2 feet", 609.6)]
    [InlineData("1'6\"", 457.2)]
    [InlineData("1' 6\"", 457.2)]
    [InlineData("1' 6", 457.2)]            // inches implied after feet
    [InlineData("1ft 6in", 457.2)]
    [InlineData("1'-6 1/2\"", 469.9)]
    // Metric.
    [InlineData("12.5cm", 125)]
    [InlineData("1.2m", 1200)]
    [InlineData("1.2 M", 1200)]
    public void Reads_lengths_in_millimetres(string text, double mm)
    {
        var r = LengthInput.Parse(text);
        Assert.Equal(LengthInput.Kind.Length, r.Kind);
        Assert.Equal(mm, r.Millimetres, 6);
    }

    [Theory]
    [InlineData("51/2in")]      // ambiguous: 25.5 or 5 1/2?
    [InlineData("51/2")]
    [InlineData("5 1/0in")]
    [InlineData("10 inx")]
    [InlineData("2ft 3cm")]
    public void Refuses_ambiguous_or_malformed_lengths_with_a_reason(string text)
    {
        var r = LengthInput.Parse(text);
        Assert.Equal(LengthInput.Kind.Invalid, r.Kind);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("100+50")]     // left for fields that evaluate sums (move/scale boxes)
    public void Leaves_non_lengths_to_the_field(string text)
        => Assert.Equal(LengthInput.Kind.NotALength, LengthInput.Parse(text).Kind);

    [Fact]
    public void Reports_whether_a_unit_was_typed()
    {
        Assert.False(LengthInput.Parse("254").HadUnit);
        Assert.True(LengthInput.Parse("10in").HadUnit);
        Assert.True(LengthInput.Parse("1' 6").HadUnit);
    }
}
