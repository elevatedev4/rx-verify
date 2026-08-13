using RxVerifyOverlay.OrderAssist.Parsing;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>Unit tests for CurrencyParser (OrderAssist/Parsing/CurrencyParser.cs) — currency-tolerant decimal parsing shared by ZeroQuantityDetector and SubstitutionRecommender.</summary>
public class CurrencyParserTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("0.00", 0)]
    [InlineData("0.0000", 0)]
    [InlineData("5", 5)]
    [InlineData("$1,234.56", 1234.56)]
    [InlineData(" 5.00 ", 5.00)]
    [InlineData("1,234", 1234)]
    [InlineData("-12.0000", -12.0)]
    [InlineData("0.1230000", 0.123)]
    public void ParsesRecognizedNumericFormats(string text, double expected)
    {
        Assert.Equal((decimal)expected, CurrencyParser.Parse(text));
    }

    [Fact]
    public void ParsesParenthesizedAmountsAsNegative()
    {
        Assert.Equal(-12.50m, CurrencyParser.Parse("(12.50)"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("N/A")]
    [InlineData("abc")]
    [InlineData("16714-0626-01")] // NDC-style code — must never be misread as a number
    public void ReturnsNullForBlankOrNonNumericText(string? text)
    {
        Assert.Null(CurrencyParser.Parse(text));
    }
}
