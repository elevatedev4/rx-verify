using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Scanning;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// END-TO-END pure pipeline tests for CatalogSubstitutionScanner.Analyze —
/// round 2's additions (sort-order badge, best-large/best-small package
/// markers) plus round 3's per-row savings-badge redesign (replacing round
/// 1/2's single green pick + yellow McKesson contrast row — see
/// SubstitutionRecommender's own doc), all from synthetic OCR word grids.
/// All NDCs/prices/quantities are made up, never copied from a real
/// screenshot. See CatalogSubstitutionScannerTests.cs for the decoy-column
/// trap + below-threshold coverage — this file only adds
/// package-marker/sort-badge/multi-badge scenarios.
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
    public void SavingsBadgesAndPackageMarkersAllCoexist()
    {
        var words = new List<OcrWord>(ThreeColumnHeader());
        words.AddRange(ThreeColumnRow(40, "ANDA", "30.0000", "6.00"));       // small, cheapest secondary -> above-threshold badge
        words.AddRange(ThreeColumnRow(60, "McKesson", "500.0000", "10.00")); // large, only/cheapest McKesson -> the baseline
        words.AddRange(ThreeColumnRow(80, "IPC", "1000.0000", "9.00"));      // large, cheaper than McKesson but below threshold -> yellow badge + best-large marker

        var annotations = CatalogSubstitutionScanner.Analyze(words);

        Assert.Equal(2, annotations.SavingsBadges.Count);

        var andaBadge = annotations.SavingsBadges.Single(b => b.RowIndex == 0);
        // ROUND 4 (Will: "Leave off the word savings from there").
        Assert.Equal("40%", andaBadge.SavingsDisplay); // (10-6)/10
        Assert.True(andaBadge.MeetsThreshold);

        var ipcBadge = annotations.SavingsBadges.Single(b => b.RowIndex == 2);
        Assert.Equal("10%", ipcBadge.SavingsDisplay); // (10-9)/10
        Assert.False(ipcBadge.MeetsThreshold);

        // ROUND 4 (Will: "also highlight the cheapest mckesson item that
        // is being compared in some intuitive color") — row 1 is the
        // only/cheapest McKesson row, so it's the baseline every badge's
        // percentage above was computed against.
        Assert.NotNull(annotations.McKessonBaselineMarker);
        Assert.Equal(1, annotations.McKessonBaselineMarker!.RowIndex);

        // Best SMALL is row 0 (30-count, cheapest/only small row) -- round
        // 3 no longer excludes it just because it also has a savings badge
        // (that exclusion existed only to avoid double-marking a full-row
        // green fill, which round 3 no longer draws at all).
        Assert.NotNull(annotations.BestSmallPackageMarker);
        Assert.Equal(0, annotations.BestSmallPackageMarker!.RowIndex);

        // Best LARGE is row 2 (IPC, $9.00) not row 1 (McKesson, $10.00) --
        // package-class bests are picked across ALL suppliers, independent
        // of the savings-badge split.
        Assert.NotNull(annotations.BestLargePackageMarker);
        Assert.Equal(2, annotations.BestLargePackageMarker!.RowIndex);
        Assert.Equal("best large pkg", annotations.BestLargePackageMarker.Label);
    }

    /// <summary>
    /// ROUND 4 REGRESSION (Will verbatim: "Just make the % indicator show
    /// the %, always at the right side (one is floating over due to AWP
    /// and AWP per unit being empty)"). Builds a 4-column table (Supplier,
    /// Rebate Cost Per Unit, then two TRAILING columns — "AWP"/"AWP Per
    /// Unit" — that exist in the header but have NO OCR'd words in any
    /// data row, exactly like the owner's screenshot) and proves the
    /// savings badge/fill's Right edge lands at the FULL table's
    /// rightmost resolved column (AWP Per Unit's own header extent), not
    /// wherever the row's own (here, truncated) OCR'd word extent
    /// happened to end -- which is what "floating over" the empty columns
    /// looked like before this fix.
    /// </summary>
    [Fact]
    public void SavingsBadgeRightEdgeIsPinnedToTheTablesFullWidthEvenWithEmptyTrailingColumns()
    {
        var words = new List<OcrWord>
        {
            Word("Supplier", 0, 0, 50),
            Word("Rebate", 200, 0, 40),
            Word("Cost", 243, 0, 30),
            Word("Per", 276, 0, 20),
            Word("Unit", 299, 0, 25),
            Word("AWP", 400, 0, 30),           // trailing column, header only (x=400-430)
            Word("AWP", 500, 0, 30),           // second trailing column (single line, 3 words: x=500-585)
            Word("Per", 535, 0, 20),
            Word("Unit", 560, 0, 25),

            // Row 0: McKesson baseline -- has AWP/AWP Per Unit text (full row).
            Word("McKesson", 5, 60, 60),
            Word("10.00", 200, 60, 30),
            Word("50.00", 400, 60, 30),
            Word("5.00", 500, 60, 30),

            // Row 1: ANDA, cheaper than McKesson -- AWP/AWP Per Unit columns are
            // BLANK (no OCR'd words at all), same shape as the owner's report.
            // The row's own OCR'd word extent therefore ends at x=230 (the
            // "6.00" cost cell), well short of the table's real rightmost
            // column (AWP Per Unit, extending to x=585).
            Word("ANDA", 5, 90, 30),
            Word("6.00", 200, 90, 30),
        };

        var annotations = CatalogSubstitutionScanner.Analyze(words);

        var badge = Assert.Single(annotations.SavingsBadges);
        Assert.Equal(1, badge.RowIndex);

        // The table's own rightmost header band (AWP Per Unit) ends at
        // x=585 (560 + 25 width) -- the badge/fill must reach that edge
        // regardless of row 1's own blank AWP/AWP Per Unit cells.
        Assert.Equal(585, badge.Right);
        Assert.True(badge.Right > 230, "badge must not stop at the row's own truncated OCR extent (230)");
    }

    /// <summary>Same shape as ThreeColumnRow but with a caller-chosen word height, to simulate a row whose OCR happened to catch a taller/shorter cell than its neighbors.</summary>
    private static List<OcrWord> ThreeColumnRowWithHeight(double y, double h, string supplier, string packageQuantity, string cost)
    {
        return new List<OcrWord>
        {
            Word(supplier, 10, y, 30, h),
            Word("1", 110, y, 8, h),
            Word("Stock", 125, y, 35, h),
            Word("Package", 165, y, 50, h),
            Word("with", 220, y, 25, h),
            Word(packageQuantity, 250, y, 40, h),
            Word(cost, 370, y, 30, h),
        };
    }

    [Fact]
    public void PackageMarkerRowRectsShareTheSameUniformHeightEvenWhenRawOcrHeightsDiffer()
    {
        // ROUND 3 (Will: "Make the height match the height of the row too
        // (which should be the same for all rows)"). Rows below have
        // DELIBERATELY different raw word heights (8 / 16 / 24 -- as if
        // one row's OCR caught a taller cell than another's) at a fixed
        // 60px row pitch; the canonical height is derived from that PITCH
        // (row-to-row center spacing), never from an individual row's own
        // word height, so every resulting badge/marker still ends up the
        // exact same height despite the differing raw input.
        var words = new List<OcrWord>(ThreeColumnHeader());
        words.AddRange(ThreeColumnRowWithHeight(40, 8, "ANDA", "30.0000", "6.00"));
        words.AddRange(ThreeColumnRowWithHeight(100, 16, "McKesson", "500.0000", "10.00"));
        words.AddRange(ThreeColumnRowWithHeight(160, 24, "IPC", "1000.0000", "9.00"));

        var annotations = CatalogSubstitutionScanner.Analyze(words);

        var badgeHeights = annotations.SavingsBadges.Select(b => b.Bottom - b.Top).Distinct().ToList();
        Assert.Single(badgeHeights);

        Assert.NotNull(annotations.BestLargePackageMarker);
        Assert.NotNull(annotations.BestSmallPackageMarker);
        var largeHeight = annotations.BestLargePackageMarker!.Bottom - annotations.BestLargePackageMarker.Top;
        var smallHeight = annotations.BestSmallPackageMarker!.Bottom - annotations.BestSmallPackageMarker.Top;
        Assert.Equal(badgeHeights[0], largeHeight);
        Assert.Equal(badgeHeights[0], smallHeight);

        // The canonical height is the row-to-row CENTER pitch (64: rows
        // start 60px apart at y=40/100/160, but each row's own increasing
        // word height (8/16/24) shifts its center down by an extra 4px
        // each step) -- not any individual row's own raw word height (8,
        // 16, or 24), proving this isn't coincidentally equal to one of
        // the raw per-row heights.
        Assert.Equal(64, badgeHeights[0]);
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
        // Same shape as CatalogSubstitutionScannerTests' fixture -- no
        // Shipping Size column at all -- proving Analyze degrades
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

        var badge = Assert.Single(annotations.SavingsBadges);
        Assert.Equal(0, badge.RowIndex);
        Assert.True(badge.MeetsThreshold);

        Assert.Null(annotations.BestLargePackageMarker);
        Assert.Null(annotations.BestSmallPackageMarker);
    }

    [Fact]
    public void AnalyzeReturnsAllEmptyAnnotationsWhenColumnsCannotBeResolved()
    {
        var words = new List<OcrWord> { Word("999.95", 0, 0, 40) }; // no header at all

        var annotations = CatalogSubstitutionScanner.Analyze(words);

        Assert.Empty(annotations.SavingsBadges);
        Assert.Null(annotations.BestLargePackageMarker);
        Assert.Null(annotations.BestSmallPackageMarker);
        Assert.Null(annotations.SortIndicatorBadge);
        Assert.Null(annotations.CostColumnHeaderAnchor);
    }
}
