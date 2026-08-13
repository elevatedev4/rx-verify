using System.Collections.Generic;
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
/// the McKesson-vs-secondary recommendation reads the RIGHT column, not
/// the decoy. All costs are small synthetic round numbers, not real
/// pricing.
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
    public void RecommendsTheCheapestSecondaryUsingTheRealColumnNotTheDecoy()
    {
        var highlight = CatalogSubstitutionScanner.FindRecommendation(BuildWords());

        Assert.NotNull(highlight);
        Assert.Equal(0, highlight!.RowIndex); // the ANDA row
        Assert.Equal("30% savings", highlight.SavingsDisplay); // (10.00-7.00)/10.00 = 30%
    }

    [Fact]
    public void RecommendedHighlightSpansTheFullRowNotJustTheCostCell()
    {
        var highlight = CatalogSubstitutionScanner.FindRecommendation(BuildWords());

        Assert.NotNull(highlight);
        // Row 0's leftmost word is "ANDA" at x=10, rightmost is the real
        // Rebate Cost Per Unit value ending at x=400 (370+30) -- the
        // decoy cost cell (ending at 230) is NOT the rightmost.
        Assert.Equal(10, highlight!.Left);
        Assert.Equal(400, highlight.Right);
    }

    [Fact]
    public void ReturnsNullWhenNoSecondaryMeetsTheSavingsBar()
    {
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

        Assert.Null(CatalogSubstitutionScanner.FindRecommendation(words));
    }

    [Fact]
    public void ReturnsNullWhenTheColumnsCannotBeResolved()
    {
        var words = new List<OcrWord> { Word("999.95", 0, 0, 40) }; // no header at all
        Assert.Null(CatalogSubstitutionScanner.FindRecommendation(words));
    }
}
