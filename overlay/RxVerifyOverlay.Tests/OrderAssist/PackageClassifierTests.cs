using System.Collections.Generic;
using RxVerifyOverlay.OrderAssist.Decisions;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

using PackageRowInput = RxVerifyOverlay.OrderAssist.Decisions.PackageClassifier.PackageRowInput;

/// <summary>
/// Unit tests for PackageClassifier — Will's round-2 "best large vs best
/// small package" rule. All NDCs/prices/quantities below are synthetic.
/// </summary>
public class PackageClassifierTests
{
    [Theory]
    [InlineData(499.9999, PackageClass.Small)]
    [InlineData(500, PackageClass.Large)] // inclusive boundary per the owner's spec
    [InlineData(500.0001, PackageClass.Large)]
    [InlineData(30, PackageClass.Small)]
    [InlineData(1000, PackageClass.Large)]
    [InlineData(0, PackageClass.Small)]
    public void ClassifiesAgainstTheFiveHundredThreshold(double quantity, PackageClass expected)
    {
        Assert.Equal(expected, PackageClassifier.Classify((decimal)quantity));
    }

    [Fact]
    public void NullQuantityClassifiesAsUnknownNeverAsEitherBucket()
    {
        Assert.Equal(PackageClass.Unknown, PackageClassifier.Classify(null));
    }

    [Fact]
    public void FindsTheCheapestRowIndependentlyWithinEachClass()
    {
        var rows = new List<PackageRowInput>
        {
            new(0, "1 Stock Package with 30.0000", "8.00"),  // small, not cheapest small
            new(1, "1 Stock Package with 500.0000", "5.00"), // large, cheapest large
            new(2, "1 Stock Package with 100.0000", "3.00"), // small, cheapest small
            new(3, "1 Stock Package with 1000.0000", "6.00"), // large, more expensive
        };

        var picks = PackageClassifier.FindBestPerClass(rows);

        Assert.Equal(1, picks.BestLargeRowIndex);
        Assert.Equal(2, picks.BestSmallRowIndex);
    }

    [Fact]
    public void AllRowsOneClassLeavesTheOtherClassNull()
    {
        var rows = new List<PackageRowInput>
        {
            new(0, "1 Stock Package with 30.0000", "8.00"),
            new(1, "1 Stock Package with 100.0000", "3.00"),
        };

        var picks = PackageClassifier.FindBestPerClass(rows);

        Assert.Null(picks.BestLargeRowIndex);
        Assert.Equal(1, picks.BestSmallRowIndex);
    }

    [Fact]
    public void RowsWithUnreadablePackageSizeAreExcludedFromBothBuckets()
    {
        var rows = new List<PackageRowInput>
        {
            new(0, "Stock Package", "1.00"), // unparseable size -> Unknown class, excluded
            new(1, "1 Stock Package with 30.0000", "8.00"),
        };

        var picks = PackageClassifier.FindBestPerClass(rows);

        Assert.Null(picks.BestLargeRowIndex);
        Assert.Equal(1, picks.BestSmallRowIndex); // never row 0, despite its lower cost
    }

    [Fact]
    public void RowsWithUnreadableCostAreExcludedNotTreatedAsFree()
    {
        var rows = new List<PackageRowInput>
        {
            new(0, "1 Stock Package with 30.0000", ""), // blank cost -> excluded
            new(1, "1 Stock Package with 100.0000", "9.00"),
        };

        var picks = PackageClassifier.FindBestPerClass(rows);

        Assert.Equal(1, picks.BestSmallRowIndex);
    }

    [Fact]
    public void TiedCheapestWithinAClassResolvesToTheLowestRowIndex()
    {
        var rows = new List<PackageRowInput>
        {
            new(0, "1 Stock Package with 500.0000", "4.00"),
            new(1, "1 Stock Package with 1000.0000", "4.00"), // tied, appears later
        };

        var picks = PackageClassifier.FindBestPerClass(rows);

        Assert.Equal(0, picks.BestLargeRowIndex);
    }

    [Fact]
    public void NoRowsAtAllProducesBothPicksNull()
    {
        var picks = PackageClassifier.FindBestPerClass(new List<PackageRowInput>());

        Assert.Null(picks.BestLargeRowIndex);
        Assert.Null(picks.BestSmallRowIndex);
    }
}
