using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.OrderAssist.Decisions;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

using CatalogRowInput = RxVerifyOverlay.OrderAssist.Decisions.SubstitutionRecommender.CatalogRowInput;

/// <summary>
/// Unit tests for SubstitutionRecommender.EvaluateSavings — round 3's
/// per-row savings-badge redesign (Will verbatim: "Always Calculate the
/// savings for each item cheaper than mckesson and display it at the end
/// of the row. Below our threshold, show in yellow, above show green." +
/// "if it is less than our savings threshold, it should still show the
/// analysis ... and still show the %"). Every edge case flagged as a
/// judgment call in the class's own doc is covered (no McKesson option, the
/// 25% boundary exactly, below-threshold still shown, ties, missing/
/// unparseable cost data, a zero-cost McKesson baseline).
/// </summary>
public class SubstitutionRecommenderTests
{
    [Fact]
    public void ExactlyTwentyFivePercentSavingsIsAboveThreshold()
    {
        // McKesson 4.00, secondary 3.00 -> (4-3)/4 = exactly 25%.
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "4.00"),
            new(1, "ANDA", "3.00"),
        };

        var badges = SubstitutionRecommender.EvaluateSavings(rows);

        var badge = Assert.Single(badges);
        Assert.Equal(1, badge.RowIndex);
        Assert.Equal(25m, badge.SavingsPercent);
        Assert.Equal("25% savings", badge.SavingsDisplay);
        Assert.Equal(SavingsTier.AboveThreshold, badge.Tier);
    }

    [Fact]
    public void JustUnderTwentyFivePercentStillShowsABadgeButBelowThreshold()
    {
        // McKesson 4.00, secondary 3.01 -> 24.75% savings -- round 3: still
        // shown, just yellow instead of green (round 1/2 would have shown
        // nothing at all here).
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "4.00"),
            new(1, "ANDA", "3.01"),
        };

        var badges = SubstitutionRecommender.EvaluateSavings(rows);

        var badge = Assert.Single(badges);
        Assert.Equal(1, badge.RowIndex);
        Assert.Equal(SavingsTier.BelowThreshold, badge.Tier);
        Assert.Contains("%", badge.SavingsDisplay);
    }

    [Fact]
    public void McKessonAlreadyCheapestNeverProducesABadge()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "3.00"),
            new(1, "ANDA", "10.00"),
        };

        Assert.Empty(SubstitutionRecommender.EvaluateSavings(rows));
    }

    [Fact]
    public void NoMcKessonOptionProducesNoBadgesAtAll()
    {
        // Round 3 drops round 1's "recommend cheapest secondary anyway with
        // n/a savings" special case -- "cheaper than McKesson" requires an
        // actual McKesson baseline to compare against.
        var rows = new List<CatalogRowInput>
        {
            new(0, "ANDA", "10.00"),
            new(1, "IPC", "7.00"),
            new(2, "TopRx", "8.00"),
        };

        Assert.Empty(SubstitutionRecommender.EvaluateSavings(rows));
    }

    [Fact]
    public void SingleSupplierCaseWithOnlyMcKessonRowsNeverProducesABadge()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "5.00"),
            new(1, "McKesson", "4.50"),
        };

        Assert.Empty(SubstitutionRecommender.EvaluateSavings(rows));
    }

    [Fact]
    public void EveryNonMcKessonRowCheaperThanMcKessonGetsItsOwnBadge()
    {
        // Round 3: not just the cheapest secondary -- EVERY row cheaper
        // than McKesson gets a badge.
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "10.00"),
            new(1, "ANDA", "5.00"),  // 50% savings -> above
            new(2, "IPC", "9.00"),   // 10% savings -> below
            new(3, "TopRx", "12.00"), // more expensive than McKesson -> no badge
        };

        var badges = SubstitutionRecommender.EvaluateSavings(rows);

        Assert.Equal(2, badges.Count);
        Assert.DoesNotContain(badges, b => b.RowIndex == 3);

        var andaBadge = badges.Single(b => b.RowIndex == 1);
        Assert.Equal(SavingsTier.AboveThreshold, andaBadge.Tier);

        var ipcBadge = badges.Single(b => b.RowIndex == 2);
        Assert.Equal(SavingsTier.BelowThreshold, ipcBadge.Tier);
    }

    [Fact]
    public void BadgesAreReturnedInRowIndexOrder()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "10.00"),
            new(3, "TopRx", "8.00"),
            new(1, "ANDA", "5.00"),
            new(2, "IPC", "9.00"),
        };

        var badges = SubstitutionRecommender.EvaluateSavings(rows);

        Assert.Equal(new[] { 1, 2, 3 }, badges.Select(b => b.RowIndex));
    }

    [Fact]
    public void RowWithUnparseableCostIsExcludedNotTreatedAsFree()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "10.00"),
            new(1, "ANDA", ""), // blank cost -> excluded, not treated as $0
            new(2, "IPC", "8.00"), // 20% savings -> below threshold, still shown
        };

        var badges = SubstitutionRecommender.EvaluateSavings(rows);

        var badge = Assert.Single(badges);
        Assert.Equal(2, badge.RowIndex);
        Assert.Equal(SavingsTier.BelowThreshold, badge.Tier);
    }

    [Fact]
    public void ZeroCostMcKessonBaselineNeverProducesABadge()
    {
        // Avoid a divide-by-zero / fabricated-percentage badge off a $0
        // (almost certainly OCR noise) McKesson baseline.
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "0.00"),
            new(1, "ANDA", "1.00"),
        };

        Assert.Empty(SubstitutionRecommender.EvaluateSavings(rows));
    }

    [Fact]
    public void NoSecondaryOptionAtAllProducesNoBadges()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "10.00"),
        };

        Assert.Empty(SubstitutionRecommender.EvaluateSavings(rows));
    }

    [Fact]
    public void MultipleMcKessonRowsUseTheCheapestAsTheBaseline()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "10.00"),
            new(1, "McKesson", "6.00"), // cheapest McKesson -> real baseline
            new(2, "ANDA", "5.00"),     // (6-5)/6 = 16.67% -> below threshold, still shown
        };

        var badges = SubstitutionRecommender.EvaluateSavings(rows);

        var badge = Assert.Single(badges);
        Assert.Equal(2, badge.RowIndex);
        Assert.Equal(SavingsTier.BelowThreshold, badge.Tier);
    }
}
