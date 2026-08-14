using System.Collections.Generic;
using RxVerifyOverlay.OrderAssist.Decisions;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

using CatalogRowInput = RxVerifyOverlay.OrderAssist.Decisions.SubstitutionRecommender.CatalogRowInput;

/// <summary>
/// Unit tests for SubstitutionRecommender — the owner's McKesson-vs-
/// cheaper-secondary rule, including every edge case flagged as a
/// judgment call in the class's own doc (no McKesson option, no
/// secondary option, the 25% boundary exactly, ties, missing/unparseable
/// cost data, a zero-cost McKesson baseline).
/// </summary>
public class SubstitutionRecommenderTests
{
    [Fact]
    public void ExactlyTwentyFivePercentSavingsRecommendsTheSecondary()
    {
        // McKesson 4.00, secondary 3.00 -> (4-3)/4 = exactly 25%.
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "4.00"),
            new(1, "ANDA", "3.00"),
        };

        var result = SubstitutionRecommender.Evaluate(rows);

        Assert.Equal(SubstitutionRecommendation.RecommendSecondary, result.Recommendation);
        Assert.Equal(1, result.RecommendedRowIndex);
        Assert.Equal(25m, result.SavingsPercent);
        Assert.Equal("25% savings", result.SavingsDisplay);
    }

    [Fact]
    public void JustUnderTwentyFivePercentDoesNotRecommend()
    {
        // McKesson 4.00, secondary 3.01 -> 24.75% savings, below the bar.
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "4.00"),
            new(1, "ANDA", "3.01"),
        };

        var result = SubstitutionRecommender.Evaluate(rows);

        Assert.Equal(SubstitutionRecommendation.None, result.Recommendation);
        Assert.Null(result.RecommendedRowIndex);
    }

    [Fact]
    public void McKessonAlreadyCheapestNeverRecommendsASecondary()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "3.00"),
            new(1, "ANDA", "10.00"),
        };

        var result = SubstitutionRecommender.Evaluate(rows);

        Assert.Equal(SubstitutionRecommendation.None, result.Recommendation);
    }

    [Fact]
    public void NoMcKessonOptionStillRecommendsCheapestSecondaryWithNaSavings()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "ANDA", "10.00"),
            new(1, "IPC", "7.00"),
            new(2, "TopRx", "8.00"),
        };

        var result = SubstitutionRecommender.Evaluate(rows);

        Assert.Equal(SubstitutionRecommendation.RecommendSecondary, result.Recommendation);
        Assert.Equal(1, result.RecommendedRowIndex); // IPC, the cheapest of the three
        Assert.Null(result.SavingsPercent);
        Assert.Equal("n/a (no McKesson option)", result.SavingsDisplay);
    }

    [Fact]
    public void SingleSupplierCaseWithOnlyMcKessonRowsNeverRecommends()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "5.00"),
            new(1, "McKesson", "4.50"),
        };

        var result = SubstitutionRecommender.Evaluate(rows);

        Assert.Equal(SubstitutionRecommendation.None, result.Recommendation);
    }

    [Fact]
    public void TiedCheapestSecondariesResolveToTheLowestRowIndex()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "10.00"),
            new(1, "ANDA", "5.00"),
            new(2, "IPC", "5.00"), // tied with row 1, appears later
        };

        var result = SubstitutionRecommender.Evaluate(rows);

        Assert.Equal(SubstitutionRecommendation.RecommendSecondary, result.Recommendation);
        Assert.Equal(1, result.RecommendedRowIndex);
    }

    [Fact]
    public void RowsWithUnparseableCostAreExcludedNotTreatedAsFree()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "10.00"),
            new(1, "ANDA", ""), // blank cost -> excluded, not treated as $0
            new(2, "IPC", "8.00"), // 20% savings -> below the bar
        };

        var result = SubstitutionRecommender.Evaluate(rows);

        Assert.Equal(SubstitutionRecommendation.None, result.Recommendation);
    }

    [Fact]
    public void ZeroCostMcKessonBaselineNeverRecommends()
    {
        // Avoid a divide-by-zero / fabricated-percentage recommendation
        // off a $0 (almost certainly OCR noise) McKesson baseline.
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "0.00"),
            new(1, "ANDA", "1.00"),
        };

        var result = SubstitutionRecommender.Evaluate(rows);

        Assert.Equal(SubstitutionRecommendation.None, result.Recommendation);
    }

    [Fact]
    public void NoSecondaryOptionAtAllNeverRecommends()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "10.00"),
        };

        var result = SubstitutionRecommender.Evaluate(rows);

        Assert.Equal(SubstitutionRecommendation.None, result.Recommendation);
    }
}
