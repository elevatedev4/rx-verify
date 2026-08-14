using RxVerifyOverlay.OrderAssist.Parsing;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Unit tests for PackageQuantityParser (OrderAssist/Parsing/PackageQuantityParser.cs)
/// — extracting the per-package quantity out of a Shipping Size cell's
/// free-text OCR value. All values below are synthetic (made-up
/// quantities), not copied from any real screenshot.
/// </summary>
public class PackageQuantityParserTests
{
    [Theory]
    [InlineData("1 Stock Package with 30.0000 Tablets", 30)]
    [InlineData("1 Stock Package with 500.0000 EA", 500)]
    [InlineData("1 Stock Package WITH 90 EA", 90)] // case-insensitive "with"
    [InlineData("1 Stock Package with 1,000.0000 EA", 1000)] // thousands separator
    public void ExtractsTheQuantityAfterWithNotTheLeadingPackageCount(string text, double expected)
    {
        Assert.Equal((decimal)expected, PackageQuantityParser.Parse(text));
    }

    [Fact]
    public void FallsBackToTheFirstNumberWhenNoWithKeywordIsPresent()
    {
        // A different cell shape than the reference screenshot's "with"
        // phrasing -- no leading package-count number to be confused with,
        // so searching the whole string is safe here.
        Assert.Equal(500m, PackageQuantityParser.Parse("500 EA"));
    }

    [Fact]
    public void WithKeywordPresentButNoTrailingNumberFailsClosedRatherThanPickingTheLeadingCount()
    {
        // Simulates a UI-truncated OCR capture cut off right after "with"
        // -- must NOT fall back to the leading package-count "1", which
        // would silently misclassify a large package as small.
        Assert.Null(PackageQuantityParser.Parse("1 Stock Package with"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Stock Package")] // no digits anywhere
    public void ReturnsNullForBlankOrNonNumericText(string? text)
    {
        Assert.Null(PackageQuantityParser.Parse(text));
    }
}
