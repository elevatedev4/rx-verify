using System.Collections.Generic;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Geometry;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>Unit tests for TableRowGrouper — bucketing flat OCR words into top-to-bottom rows by vertical overlap.</summary>
public class TableRowGrouperTests
{
    private static OcrWord Word(string text, double x, double y, double w, double h) =>
        new() { Text = text, X = x, Y = y, W = w, H = h };

    [Fact]
    public void GroupsWordsOnTheSameBaselineIntoOneRowOrderedLeftToRight()
    {
        var words = new List<OcrWord>
        {
            Word("Quantity", 50, 0, 40, 12),
            Word("Order", 0, 0, 40, 12),
        };

        var rows = TableRowGrouper.GroupIntoRows(words);

        Assert.Single(rows);
        Assert.Equal(new[] { "Order", "Quantity" }, new[] { rows[0][0].Text, rows[0][1].Text });
    }

    [Fact]
    public void SeparatesRowsWithAClearVerticalGap()
    {
        var words = new List<OcrWord>
        {
            Word("Header", 0, 0, 40, 12),
            Word("0", 0, 30, 10, 12), // starts well below row 0's bottom (12)
        };

        var rows = TableRowGrouper.GroupIntoRows(words);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Header", rows[0][0].Text);
        Assert.Equal("0", rows[1][0].Text);
    }

    [Fact]
    public void DropsBlankOrWhitespaceOnlyWords()
    {
        var words = new List<OcrWord>
        {
            Word("Real", 0, 0, 20, 12),
            Word("  ", 25, 0, 20, 12),
            Word("", 50, 0, 20, 12),
        };

        var rows = TableRowGrouper.GroupIntoRows(words);

        Assert.Single(rows);
        Assert.Single(rows[0]);
        Assert.Equal("Real", rows[0][0].Text);
    }

    [Fact]
    public void EmptyInputProducesNoRows()
    {
        Assert.Empty(TableRowGrouper.GroupIntoRows(new List<OcrWord>()));
    }
}
