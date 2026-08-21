using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Scanning;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// END-TO-END pure pipeline test for the Catalog Item Substitution
/// Selection window: a synthetic OCR word list with a decoy "Rebate
/// Cost" column immediately before the real "Rebate Cost Per Unit"
/// column (the same prefix-sharing shape as the Order Quantity trap,
/// present on this window too — see ColumnResolver's class doc), proving
/// the savings-badge analysis (round 3) reads the RIGHT column, not the
/// decoy. All costs are small synthetic round numbers, not real pricing.
/// </summary>
public class CatalogSubstitutionScannerTests
{
    private static OcrWord Word(string text, double x, double y, double w, double h = 12) =>
        new() { Text = text, X = x, Y = y, W = w, H = h };

    private static List<OcrWord> BuildWords()
    {
        return new List<OcrWord>
        {
            // Header row: "Supplier" ... decoy "Rebate Cost" ... real "Rebate Cost Per Unit"
            Word("Supplier", 0, 0, 50),
            Word("Rebate", 150, 0, 40),
            Word("Cost", 193, 0, 30),
            Word("Rebate", 320, 0, 40),
            Word("Cost", 363, 0, 30),
            Word("Per", 396, 0, 20),
            Word("Unit", 419, 0, 25),

            // Row 0: secondary (ANDA), decoy cost 9.00 (irrelevant), real
            // Rebate Cost Per Unit 7.00 -- the cheapest overall.
            Word("ANDA", 10, 40, 30),
            Word("9.00", 200, 40, 30),
            Word("7.00", 370, 40, 30),

            // Row 1: McKesson, real Rebate Cost Per Unit 10.00 -- cheapest McKesson.
            Word("McKesson", 5, 60, 60),
            Word("10.00", 370, 60, 30),

            // Row 2: McKesson, real Rebate Cost Per Unit 12.00 -- more expensive.
            Word("McKesson", 5, 80, 60),
            Word("12.00", 370, 80, 30),
        };
    }

    [Fact]
    public void SavingsBadgeUsesTheRealColumnNotTheDecoy()
    {
        var badges = CatalogSubstitutionScanner.Analyze(BuildWords()).SavingsBadges;

        var badge = Assert.Single(badges);
        Assert.Equal(0, badge.RowIndex); // the ANDA row
        // ROUND 4 (Will: "Leave off the word savings from there").
        Assert.Equal("30%", badge.SavingsDisplay); // (10.00-7.00)/10.00 = 30%
        Assert.True(badge.MeetsThreshold);
    }

    /// <summary>
    /// ROUND 4 (Will verbatim: "make sure the green highlight covers the
    /// whole row" + "the % indicator show the %, always at the right
    /// side (one is floating over due to AWP and AWP per unit being
    /// empty)") — REPLACES this test's round-3 assumption. Round 3
    /// anchored a badge to the ROW's own OCR'd word extent (here, "ANDA"
    /// at x=10 through the real cost cell ending at x=400); round 4
    /// anchors it to the TABLE's own resolved column bands instead (see
    /// CatalogSubstitutionScanner's own round-4 doc) so a row with blank
    /// trailing cells still gets a full-width fill and a consistently
    /// right-anchored badge. Left/Right below are the table's own
    /// leftmost (Supplier, x=0) and rightmost (the REAL Rebate Cost Per
    /// Unit column, x=444 = 419+25 — not the decoy, which ends at 223)
    /// resolved header bands, independent of which words row 0 itself
    /// happened to have.
    /// </summary>
    [Fact]
    public void SavingsBadgeAnchorSpansTheTablesFullColumnWidthNotJustTheRowsOwnCells()
    {
        var badges = CatalogSubstitutionScanner.Analyze(BuildWords()).SavingsBadges;

        var badge = Assert.Single(badges);
        Assert.Equal(0, badge.Left);
        Assert.Equal(444, badge.Right);
    }

    [Fact]
    public void BelowThresholdSavingsStillProducesABadgeButNotMeetingThreshold()
    {
        // ROUND 3 (Will: "if it is less than our savings threshold, it
        // should still show the analysis ... and still show the %") --
        // round 1/2 would have returned nothing here at all.
        var words = new List<OcrWord>
        {
            Word("Supplier", 0, 0, 50),
            Word("Rebate", 320, 0, 40),
            Word("Cost", 363, 0, 30),
            Word("Per", 396, 0, 20),
            Word("Unit", 419, 0, 25),

            Word("McKesson", 5, 40, 60),
            Word("10.00", 370, 40, 30),

            Word("ANDA", 10, 60, 30),
            Word("9.00", 370, 60, 30), // only 10% cheaper -- below the 25% bar
        };

        var badges = CatalogSubstitutionScanner.Analyze(words).SavingsBadges;

        var badge = Assert.Single(badges);
        Assert.Equal(1, badge.RowIndex);
        Assert.False(badge.MeetsThreshold);
        Assert.Contains("%", badge.SavingsDisplay);
    }

    [Fact]
    public void ReturnsNoBadgesWhenTheColumnsCannotBeResolved()
    {
        var words = new List<OcrWord> { Word("999.95", 0, 0, 40) }; // no header at all
        Assert.Empty(CatalogSubstitutionScanner.Analyze(words).SavingsBadges);
        Assert.Null(CatalogSubstitutionScanner.Analyze(words).CostColumnHeaderAnchor);
    }

    [Fact]
    public void CostColumnHeaderAnchorResolvesEvenAloneWhenSortBadgeCannotYet()
    {
        // Only one readable row -- not enough for SortOrderChecker (needs
        // >= 2), but the column itself resolves fine, so the anchor round 3
        // needs for the "Processing" indicator must still be present.
        var words = new List<OcrWord>
        {
            Word("Supplier", 0, 0, 50),
            Word("Rebate", 320, 0, 40),
            Word("Cost", 363, 0, 30),
            Word("Per", 396, 0, 20),
            Word("Unit", 419, 0, 25),

            Word("ANDA", 10, 40, 30),
            Word("9.00", 370, 40, 30),
        };

        var annotations = CatalogSubstitutionScanner.Analyze(words);

        Assert.Null(annotations.SortIndicatorBadge);
        Assert.NotNull(annotations.CostColumnHeaderAnchor);
    }
}
