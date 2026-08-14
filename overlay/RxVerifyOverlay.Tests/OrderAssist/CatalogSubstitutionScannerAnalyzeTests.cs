using System.Collections.Generic;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Scanning;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// END-TO-END pure pipeline tests for CatalogSubstitutionScanner.Analyze —
/// Will's round-2 additions (sort-order badge, best-large/best-small
/// package markers, yellow McKesson contrast row) layered on top of the
/// round-1 green pick, all from synthetic OCR word grids. All NDCs/
/// prices/quantities are made up, never copied from a real screenshot.
/// See CatalogSubstitutionScannerTests.cs for the round-1 green-pick-only
/// coverage (decoy column trap, no-recommendation case, unresolved
/// columns) — this file only adds round-2-specific scenarios.
/// </summary>
public class CatalogSubstitutionScannerAnalyzeTests
{
    private static OcrWord Word(string text, double x, double y, double w, double h = 12) =>
        new() { Text = text, X = x, Y = y, W = w, H = h };

    /// <summary>Header words for a 3-column table: Supplier, Shipping Size (2-word label), Rebate Cost Per Unit (4-word label) — see class doc for the column-band math this geometry relies on.</summary>
    private static List<OcrWord> ThreeColumnHeader() => new()
    {
        Word("Supplier", 0, 0, 50),
        Word("Shipping", 150, 0, 60),
        Word("Size", 215, 0, 35),
        Word("Rebate", 350, 0, 45),
        Word("Cost", 400, 0, 35),
        Word("Per", 440, 0, 25),
        Word("Unit", 470, 0, 35),
    };

    /// <summary>One data row: Supplier cell, a tokenized Shipping Size cell ("1 Stock Package with {quantity}"), and a single-word Rebate Cost Per Unit cell — all positioned to land in the right column partition (see ThreeColumnHeader's resulting bands).</summary>
    private static List<OcrWord> ThreeColumnRow(double y, string supplier, string packageQuantity, string cost)
    {
        return new List<OcrWord>
        {
            Word(supplier, 10, y, 30),
            Word("1", 110, y, 8),
            Word("Stock", 125, y, 35),
            Word("Package", 165, y, 50),
            Word("with", 220, y, 25),
            Word(packageQuantity, 250, y, 40),
            Word(cost, 370, y, 30),
        };
    }

    [Fact]
    public void GreenYellowAndPackageMarkersAllCoexistWithoutCollidingRows()
    {
        var words = new List<OcrWord>(ThreeColumnHeader());
        words.AddRange(ThreeColumnRow(40, "ANDA", "30.0000", "6.00"));       // small, cheapest secondary -> green
        words.AddRange(ThreeColumnRow(60, "McKesson", "500.0000", "10.00")); // large, cheapest McKesson -> yellow
        words.AddRange(ThreeColumnRow(80, "IPC", "1000.0000", "9.00"));      // large, cheaper than the McKesson large row -> best-large marker

        var annotations = CatalogSubstitutionScanner.Analyze(words);

        Assert.NotNull(annotations.GreenHighlight);
        Assert.Equal(0, annotations.GreenHighlight!.RowIndex); // ANDA
        Assert.Equal("40% savings", annotations.GreenHighlight.SavingsDisplay); // (10-6)/10

        Assert.NotNull(annotations.YellowHighlight);
        Assert.Equal(1, annotations.YellowHighlight!.RowIndex); // McKesson

        // Best SMALL is row 0 (30-count, cheapest small) -- but that's
        // already the green pick, so no separate marker is drawn for it.
        Assert.Null(annotations.BestSmallPackageMarker);

        // Best LARGE is row 2 (IPC, $9.00) not row 1 (McKesson, $10.00) --
        // package-class bests are picked across ALL suppliers, independent
        // of the McKesson/secondary split.
        Assert.NotNull(annotations.BestLargePackageMarker);
        Assert.Equal(2, annotations.BestLargePackageMarker!.RowIndex);
        Assert.Equal("best large pkg", annotations.BestLargePackageMarker.Label);
    }

    [Fact]
    public void SortIndicatorReadsSortedWhenRebateCostsAreNonDecreasing()
    {
        var words = new List<OcrWord>(ThreeColumnHeader());
        words.AddRange(ThreeColumnRow(40, "IPC", "30.0000", "3.00"));
        words.AddRange(ThreeColumnRow(60, "IPC", "30.0000", "5.00"));
        words.AddRange(ThreeColumnRow(80, "IPC", "30.0000", "5.00"));
        words.AddRange(ThreeColumnRow(100, "IPC", "30.0000", "10.00"));

        var annotations = CatalogSubstitutionScanner.Analyze(words);

        Assert.NotNull(annotations.SortIndicatorBadge);
        Assert.True(annotations.SortIndicatorBadge!.IsSorted);
        Assert.Contains("sorted", annotations.SortIndicatorBadge.Text);
    }

    [Fact]
    public void SortIndicatorReadsNotSortedWhenARowBreaksAscendingOrder()
    {
        var words = new List<OcrWord>(ThreeColumnHeader());
        words.AddRange(ThreeColumnRow(40, "IPC", "30.0000", "5.00"));
        words.AddRange(ThreeColumnRow(60, "IPC", "30.0000", "3.00"));
        words.AddRange(ThreeColumnRow(80, "IPC", "30.0000", "8.00"));

        var annotations = CatalogSubstitutionScanner.Analyze(words);

        Assert.NotNull(annotations.SortIndicatorBadge);
        Assert.False(annotations.SortIndicatorBadge!.IsSorted);
        Assert.Contains("not sorted", annotations.SortIndicatorBadge.Text);
    }

    [Fact]
    public void SortIndicatorAbsentWithFewerThanTwoReadableRebateCosts()
    {
        var words = new List<OcrWord>(ThreeColumnHeader());
        words.AddRange(ThreeColumnRow(40, "IPC", "30.0000", "5.00"));
        words.AddRange(ThreeColumnRow(60, "IPC", "30.0000", "N/A"));

        var annotations = CatalogSubstitutionScanner.Analyze(words);

        Assert.Null(annotations.SortIndicatorBadge);
    }

    [Fact]
    public void PackageMarkersAreAbsentWhenShippingSizeColumnCannotBeResolvedButOtherAnnotationsStillWork()
    {
        // Same shape as CatalogSubstitutionScannerTests' round-1 fixture --
        // no Shipping Size column at all -- proving Analyze degrades
        // gracefully rather than losing the whole tick over one missing
        // column.
        var words = new List<OcrWord>
        {
            Word("Supplier", 0, 0, 50),
            Word("Rebate", 320, 0, 40),
            Word("Cost", 363, 0, 30),
            Word("Per", 396, 0, 20),
            Word("Unit", 419, 0, 25),

            Word("ANDA", 10, 40, 30),
            Word("7.00", 370, 40, 30),

            Word("McKesson", 5, 60, 60),
            Word("10.00", 370, 60, 30),
        };

        var annotations = CatalogSubstitutionScanner.Analyze(words);

        Assert.NotNull(annotations.GreenHighlight);
        Assert.Equal(0, annotations.GreenHighlight!.RowIndex);
        Assert.NotNull(annotations.YellowHighlight);
        Assert.Equal(1, annotations.YellowHighlight!.RowIndex);

        Assert.Null(annotations.BestLargePackageMarker);
        Assert.Null(annotations.BestSmallPackageMarker);
    }

    [Fact]
    public void AnalyzeReturnsAllNullAnnotationsWhenColumnsCannotBeResolved()
    {
        var words = new List<OcrWord> { Word("999.95", 0, 0, 40) }; // no header at all

        var annotations = CatalogSubstitutionScanner.Analyze(words);

        Assert.Null(annotations.GreenHighlight);
        Assert.Null(annotations.YellowHighlight);
        Assert.Null(annotations.BestLargePackageMarker);
        Assert.Null(annotations.BestSmallPackageMarker);
        Assert.Null(annotations.SortIndicatorBadge);
    }

    [Fact]
    public void FindRecommendationRemainsEquivalentToAnalyzesGreenHighlight()
    {
        var words = new List<OcrWord>(ThreeColumnHeader());
        words.AddRange(ThreeColumnRow(40, "ANDA", "30.0000", "6.00"));
        words.AddRange(ThreeColumnRow(60, "McKesson", "500.0000", "10.00"));

        var viaFindRecommendation = CatalogSubstitutionScanner.FindRecommendation(words);
        var viaAnalyze = CatalogSubstitutionScanner.Analyze(words).GreenHighlight;

        Assert.NotNull(viaFindRecommendation);
        Assert.Equal(viaAnalyze!.RowIndex, viaFindRecommendation!.RowIndex);
        Assert.Equal(viaAnalyze.SavingsDisplay, viaFindRecommendation.SavingsDisplay);
    }
}
