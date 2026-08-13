using System.Collections.Generic;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Geometry;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>Unit tests for CellValueBucketizer — reading one resolved column's per-row value out of body rows, bucketing by word-CENTER falling inside the column's wider partition.</summary>
public class CellValueBucketizerTests
{
    private static OcrWord Word(string text, double x, double y = 30, double w = 10, double h = 12) =>
        new() { Text = text, X = x, Y = y, W = w, H = h };

    // A column whose own header text spans [90,174] but whose PARTITION
    // (what actually gets used for bucketing) is wider: [75.5, 188.5] —
    // mirrors ColumnResolverTests' "Order Quantity" band exactly.
    private static readonly ColumnBand OrderQuantityBand = new("Order Quantity", 90, 174, 75.5, 188.5);

    [Fact]
    public void ReadsAValueWordCenteredInsideThePartition()
    {
        var bodyRows = new List<IReadOnlyList<OcrWord>> { new List<OcrWord> { Word("0", x: 130) } };

        var cells = CellValueBucketizer.BucketColumn(bodyRows, OrderQuantityBand);

        Assert.Single(cells);
        Assert.Equal("0", cells[0].Text);
        Assert.NotNull(cells[0].Bounds);
    }

    [Fact]
    public void IgnoresAValueWordBelongingToTheNeighboringColumn()
    {
        // x=250 falls inside the NEXT column's partition (e.g. "Suggested
        // Order Qty" starting at 188.5), not this one's.
        var bodyRows = new List<IReadOnlyList<OcrWord>> { new List<OcrWord> { Word("0", x: 250) } };

        var cells = CellValueBucketizer.BucketColumn(bodyRows, OrderQuantityBand);

        Assert.Single(cells);
        Assert.Equal("", cells[0].Text);
        Assert.Null(cells[0].Bounds);
    }

    [Fact]
    public void RowWithNoMatchingWordsGetsBlankTextAndNullBounds()
    {
        var bodyRows = new List<IReadOnlyList<OcrWord>> { new List<OcrWord>() };

        var cells = CellValueBucketizer.BucketColumn(bodyRows, OrderQuantityBand);

        Assert.Equal("", cells[0].Text);
        Assert.Null(cells[0].Bounds);
    }

    [Fact]
    public void RowIndexMatchesPositionInBodyRowsRegardlessOfMatches()
    {
        var bodyRows = new List<IReadOnlyList<OcrWord>>
        {
            new List<OcrWord> { Word("1", x: 130) },
            new List<OcrWord> { Word("0", x: 250) }, // no match for this column
            new List<OcrWord> { Word("2", x: 130) },
        };

        var cells = CellValueBucketizer.BucketColumn(bodyRows, OrderQuantityBand);

        Assert.Equal(3, cells.Count);
        Assert.Equal(0, cells[0].RowIndex);
        Assert.Equal(1, cells[1].RowIndex);
        Assert.Equal(2, cells[2].RowIndex);
        Assert.Equal("1", cells[0].Text);
        Assert.Equal("", cells[1].Text);
        Assert.Equal("2", cells[2].Text);
    }
}
