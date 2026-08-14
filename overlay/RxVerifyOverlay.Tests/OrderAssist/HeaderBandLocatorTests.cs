using System.Collections.Generic;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Geometry;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>Unit tests for HeaderBandLocator — the "does a literal decimal-point number appear anywhere in this row" data-row heuristic, and the leading-header-row count it drives.</summary>
public class HeaderBandLocatorTests
{
    private static OcrWord Word(string text) => new() { Text = text, X = 0, Y = 0, W = 10, H = 10 };

    private static List<OcrWord> Row(params string[] texts)
    {
        var row = new List<OcrWord>();
        foreach (var t in texts) row.Add(Word(t));
        return row;
    }

    [Fact]
    public void RowWithNoDecimalPointNumberIsNotADataRow()
    {
        Assert.False(HeaderBandLocator.IsDataRow(Row("Order", "Quantity")));
    }

    [Fact]
    public void RowWithAPlainIntegerIsNotADataRowByItself()
    {
        // A plain "1" (no decimal point) alone shouldn't flip a header row
        // into "data" — real Order Quantity headers never contain any
        // number at all, but this guards the heuristic's own boundary.
        Assert.False(HeaderBandLocator.IsDataRow(Row("Order", "Quantity", "1")));
    }

    [Theory]
    [InlineData("999.95")]
    [InlineData("-40.0000")]
    [InlineData("0.5550000")]
    [InlineData("$6.25")]
    public void RowWithADecimalFormattedNumberIsADataRow(string numericToken)
    {
        Assert.True(HeaderBandLocator.IsDataRow(Row("Sample", "Drug", numericToken)));
    }

    [Fact]
    public void CountsBothWrappedHeaderLinesWhenNeitherLooksLikeData()
    {
        var rows = new List<IReadOnlyList<OcrWord>>
        {
            Row("Suggested"),
            Row("Order", "Qty"),
            Row("Sample", "999.95"),
        };

        Assert.Equal(2, HeaderBandLocator.CountHeaderRows(rows));
    }

    [Fact]
    public void ReturnsZeroWhenTheVeryFirstRowAlreadyLooksLikeData()
    {
        var rows = new List<IReadOnlyList<OcrWord>>
        {
            Row("Sample", "999.95"),
        };

        Assert.Equal(0, HeaderBandLocator.CountHeaderRows(rows));
    }

    [Fact]
    public void NeverCountsMoreThanMaxHeaderRows()
    {
        var rows = new List<IReadOnlyList<OcrWord>>
        {
            Row("A"),
            Row("B"),
            Row("C"), // no decimal numbers anywhere -- would otherwise keep counting
        };

        Assert.Equal(2, HeaderBandLocator.CountHeaderRows(rows, maxHeaderRows: 2));
    }
}
