using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Geometry;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Unit tests for ColumnResolver — most importantly THE SUBSTRING TRAP
/// the owner's spec calls out explicitly: an "Order Quantity" column
/// sitting immediately next to a 2-line-wrapped "Suggested"/"Order Qty"
/// column must resolve as two DIFFERENT columns, never conflated by a
/// Contains/StartsWith-style match. Word coordinates below are synthetic,
/// shaped to mimic the owner's reference screenshot's column layout
/// (adjacent "Cost Per Unit" / "Order Quantity" / "Suggested Order Qty"
/// columns, the last wrapped across two OCR'd lines) — no real pricing or
/// patient data is used anywhere in this file.
/// </summary>
public class ColumnResolverTests
{
    private static OcrWord Word(string text, double x, double y, double w, double h = 12) =>
        new() { Text = text, X = x, Y = y, W = w, H = h };

    /// <summary>Header rows for a 3-column table: "Cost Per Unit" | "Order Quantity" | "Suggested Order Qty" (the last wrapped onto row 1 as "Suggested" over "Order Qty") — see class doc for the real-screenshot shape this mimics.</summary>
    private static IReadOnlyList<IReadOnlyList<OcrWord>> HeaderRowsWithTheSubstringTrap()
    {
        var row0 = new List<OcrWord>
        {
            Word("Cost", 0, 0, 20),
            Word("Per", 24, 0, 15),
            Word("Unit", 41, 0, 20),
            Word("Order", 90, 0, 30),
            Word("Quantity", 124, 0, 50),
            Word("Suggested", 205, 0, 55),
        };
        var row1 = new List<OcrWord>
        {
            Word("Order", 203, 14, 30),
            Word("Qty", 236, 14, 25),
        };

        return new List<IReadOnlyList<OcrWord>> { row0, row1 };
    }

    [Fact]
    public void BuildsThreeDistinctColumnsFromTheHeaderRows()
    {
        var bands = ColumnResolver.BuildPartitionedColumnBands(HeaderRowsWithTheSubstringTrap());

        Assert.Equal(3, bands.Count);
        Assert.Equal(new[] { "Cost Per Unit", "Order Quantity", "Suggested Order Qty" }, bands.Select(b => b.Label).ToArray());
    }

    [Fact]
    public void ResolvesOrderQuantityExactlyNotItsWrappedNeighbor()
    {
        var bands = ColumnResolver.BuildPartitionedColumnBands(HeaderRowsWithTheSubstringTrap());

        var column = ColumnResolver.ResolveExact(bands, "Order Quantity");

        Assert.NotNull(column);
        Assert.Equal("Order Quantity", column!.Label);
        Assert.Equal(90, column.Left);
        Assert.Equal(174, column.Right);
    }

    [Fact]
    public void ResolvesTheWrappedSuggestedColumnSeparately()
    {
        var bands = ColumnResolver.BuildPartitionedColumnBands(HeaderRowsWithTheSubstringTrap());

        var column = ColumnResolver.ResolveExact(bands, "Suggested Order Qty");

        Assert.NotNull(column);
        Assert.Equal("Suggested Order Qty", column!.Label);
        Assert.Equal(203, column.Left);
    }

    [Fact]
    public void ResolveExactIsCaseInsensitiveAndWhitespaceNormalized()
    {
        var bands = ColumnResolver.BuildPartitionedColumnBands(HeaderRowsWithTheSubstringTrap());

        Assert.NotNull(ColumnResolver.ResolveExact(bands, "  ORDER   quantity  "));
    }

    [Fact]
    public void NeverResolvesAPartialOrSubstringLabel()
    {
        var bands = ColumnResolver.BuildPartitionedColumnBands(HeaderRowsWithTheSubstringTrap());

        // "Order" alone and "Suggested" alone are substrings/prefixes of
        // real column labels, but neither is itself a real column label
        // -- must never resolve.
        Assert.Null(ColumnResolver.ResolveExact(bands, "Order"));
        Assert.Null(ColumnResolver.ResolveExact(bands, "Suggested"));
        Assert.Null(ColumnResolver.ResolveExact(bands, "Order Qty"));
    }

    [Fact]
    public void EmptyHeaderRowsProduceNoColumns()
    {
        Assert.Empty(ColumnResolver.BuildPartitionedColumnBands(new List<IReadOnlyList<OcrWord>>()));
    }
}
