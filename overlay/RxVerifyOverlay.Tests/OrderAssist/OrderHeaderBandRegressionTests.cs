using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Geometry;
using RxVerifyOverlay.OrderAssist.Scanning;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// END-TO-END regression tests reproducing W-T76/78/81 ("nothing
/// highlights") from Will's own real diagnostic log paste (2026-08-18,
/// %TEMP% OcrLogger on the order screens):
///
///   OrderAssist[CatalogSubstitution]: column resolution failed for
///   Supplier, Rebate Cost Per Unit. Resolved header bands this tick:
///   [Order Catalog Substitution Recommended Item Selection - Catalog
///   Items Filter: All | x a Choose]
///
///   OrderAssist[CreateRecommendedOrders]: column resolution failed for
///   Order Quantity. Resolved header bands this tick: [Create Recommended
///   Orders | Actions | Tools | Search | Reports | Analysis â€¢]
///
/// Both logs show the OLD code's "resolved header bands" made entirely of
/// window TITLE and MENU/TOOLBAR chrome, with the real grid header
/// (Supplier / Rebate Cost Per Unit / Order Quantity) never appearing at
/// all — confirming the root cause: both target windows have MORE than 2
/// leading non-data rows of chrome (title bar + menu bar, plus a
/// filter/toolbar row on Catalog Substitution) above the real header row,
/// which exceeded the old HeaderBandLocator.CountHeaderRows default cap of
/// 2 — so the real header was never even looked at, let alone resolved.
///
/// These tests build a synthetic word layout with that SAME shape (2-3
/// leading chrome rows, then the real header, then data) and prove (a) the
/// OLD approach (HeaderBandLocator + ColumnResolver straight off the
/// capped rows) genuinely fails on it, and (b) the actual production
/// scanners (now backed by HeaderRowWindowSelector) succeed. No real
/// screen text — chrome/title wording below is illustrative, not copied
/// from a live capture; all prices/quantities are synthetic.
/// </summary>
public class OrderHeaderBandRegressionTests
{
    private static OcrWord Word(string text, double x, double y, double w, double h = 12) =>
        new() { Text = text, X = x, Y = y, W = w, H = h };

    // ---- Create Recommended Orders: 2 leading chrome rows -------------

    private static List<OcrWord> CreateRecommendedOrdersWordsWithChromeAboveHeader()
    {
        return new List<OcrWord>
        {
            // Row 0 (y=0): title bar text merged with the app's own menu
            // bar — mirrors Will's log split into 6 separate bands
            // ("Create Recommended Orders | Actions | Tools | Search |
            // Reports | Analysis").
            Word("Create", 0, 0, 60), Word("Recommended", 65, 0, 110), Word("Orders", 180, 0, 55),
            Word("Actions", 400, 0, 50), Word("Tools", 460, 0, 40), Word("Search", 510, 0, 50),
            Word("Reports", 570, 0, 55), Word("Analysis", 635, 0, 60),

            // Row 1 (y=20): a toolbar/filter row -- more chrome, still no
            // real column labels.
            Word("Filter", 0, 20, 40), Word("Type", 50, 20, 40), Word("All", 100, 20, 30),

            // Row 2 (y=40): the REAL grid header -- two rows of chrome
            // above it, one more than the old code's fixed cap of 2 could
            // ever reach.
            Word("Cost", 0, 40, 35), Word("Per", 40, 40, 25), Word("Unit", 70, 40, 35),
            Word("Order", 150, 40, 40), Word("Quantity", 195, 40, 60),

            // Row 3 (y=60): data row, Order Quantity = 0 (should highlight).
            Word("5.00", 10, 60, 30), Word("0", 200, 60, 15),

            // Row 4 (y=80): data row, Order Quantity = 2 (should NOT highlight).
            Word("6.00", 10, 80, 30), Word("2", 200, 80, 15),
        };
    }

    [Fact]
    public void OldApproachNeverEvenSeesTheRealHeaderRow()
    {
        var words = CreateRecommendedOrdersWordsWithChromeAboveHeader();
        var rows = TableRowGrouper.GroupIntoRows(words);

        var oldHeaderRowCount = HeaderBandLocator.CountHeaderRows(rows);
        var oldBands = ColumnResolver.BuildPartitionedColumnBands(rows.Take(oldHeaderRowCount).ToList());

        Assert.Equal(2, oldHeaderRowCount); // exhausted on the 2 chrome rows
        Assert.Null(ColumnResolver.ResolveExact(oldBands, CreateRecommendedOrdersScanner.OrderQuantityHeaderLabel));
    }

    [Fact]
    public void FixedScannerHighlightsTheGenuineZeroDespiteTwoLeadingChromeRows()
    {
        var highlights = CreateRecommendedOrdersScanner.FindZeroQuantityHighlights(CreateRecommendedOrdersWordsWithChromeAboveHeader());

        var highlight = Assert.Single(highlights);
        Assert.Equal(200, highlight.Left);
        Assert.Equal(215, highlight.Right);
        Assert.Equal(60, highlight.Top);
        Assert.Equal(72, highlight.Bottom);
    }

    // ---- Catalog Substitution: 2 leading chrome rows (title+menu, then
    // a filter/toolbar row) before the real header ------------------------

    private static List<OcrWord> CatalogSubstitutionWordsWithChromeAboveHeader()
    {
        return new List<OcrWord>
        {
            // Row 0 (y=0): scrambled title-bar-shaped chrome -- mirrors
            // Will's log showing a shuffled window title ("Order Catalog
            // Substitution Recommended Item Selection - Catalog Items
            // Filter: All"), reconstructed here as several adjacent words
            // that legitimately cluster together the same way.
            Word("Order", 0, 0, 40), Word("Catalog", 45, 0, 60), Word("Substitution", 110, 0, 90),
            Word("Recommended", 205, 0, 100), Word("Item", 310, 0, 35), Word("Selection", 350, 0, 70),
            Word("Filter", 500, 0, 40), Word("All", 545, 0, 25),

            // Row 1 (y=20): another chrome row (e.g. dialog button labels).
            Word("x", 0, 20, 10), Word("a", 15, 20, 10), Word("Choose", 30, 20, 50),

            // Row 2 (y=40): the REAL grid header.
            Word("Supplier", 0, 40, 50), Word("Rebate", 150, 40, 40), Word("Cost", 193, 40, 30),
            Word("Per", 226, 40, 20), Word("Unit", 249, 40, 25),

            // Row 3 (y=60): data row (ANDA, cheapest -> green pick).
            Word("ANDA", 10, 60, 30), Word("7.00", 370, 60, 30),

            // Row 4 (y=80): data row (McKesson).
            Word("McKesson", 5, 80, 60), Word("10.00", 370, 80, 30),
        };
    }

    [Fact]
    public void OldApproachNeverEvenSeesTheRealHeaderRowOnCatalogSubstitution()
    {
        var words = CatalogSubstitutionWordsWithChromeAboveHeader();
        var rows = TableRowGrouper.GroupIntoRows(words);

        var oldHeaderRowCount = HeaderBandLocator.CountHeaderRows(rows);
        var oldBands = ColumnResolver.BuildPartitionedColumnBands(rows.Take(oldHeaderRowCount).ToList());

        Assert.Equal(2, oldHeaderRowCount);
        Assert.Null(ColumnResolver.ResolveExact(oldBands, CatalogSubstitutionScanner.SupplierHeaderLabel));
        Assert.Null(ColumnResolver.ResolveExact(oldBands, CatalogSubstitutionScanner.RebateCostPerUnitHeaderLabel));
    }

    [Fact]
    public void FixedScannerResolvesSupplierAndCostDespiteTwoLeadingChromeRows()
    {
        var annotations = CatalogSubstitutionScanner.Analyze(CatalogSubstitutionWordsWithChromeAboveHeader());

        Assert.NotNull(annotations.GreenHighlight);
        Assert.Equal(0, annotations.GreenHighlight!.RowIndex); // ANDA row, cheapest
    }

    // ---- ROUND 2 (W-T85 bug 1): a FILTER-ROW chrome line that itself
    // misclassifies as "data" (Will's real screenshot: "Order Date:
    // 8/18/2026 [calendar] Inventory Group: <All> Supplier: <All>" — a
    // date/spinner glyph OCR can turn into, or that genuinely reads as, a
    // decimal-point token) must not kill the whole header search before it
    // ever reaches the real header, further down. -----------------------

    private static List<OcrWord> CreateRecommendedOrdersWordsWithADateLikeFilterRowAboveHeader()
    {
        return new List<OcrWord>
        {
            // Row 0 (y=0): title/menu chrome -- ordinary, non-data.
            Word("Create", 0, 0, 60), Word("Recommended", 65, 0, 110), Word("Orders", 180, 0, 55),

            // Row 1 (y=20): the FILTER row -- contains a decimal-shaped
            // token (see class doc) that HeaderBandLocator.IsDataRow
            // classifies as "data", even though it's still chrome.
            Word("Order", 0, 20, 40), Word("Date", 45, 20, 35), Word("8.18", 90, 20, 40),
            Word("Inventory", 200, 20, 60), Word("Group", 265, 20, 45),

            // Row 2 (y=40): the REAL grid header, past the false-positive
            // "data" row above it.
            Word("Cost", 0, 40, 35), Word("Per", 40, 40, 25), Word("Unit", 70, 40, 35),
            Word("Order", 150, 40, 40), Word("Quantity", 195, 40, 60),

            // Row 3 (y=60): data row, Order Quantity = 0 (should highlight).
            Word("5.00", 10, 60, 30), Word("0", 200, 60, 15),

            // Row 4 (y=80): data row, Order Quantity = 2 (should NOT highlight).
            Word("6.00", 10, 80, 30), Word("2", 200, 80, 15),
        };
    }

    [Fact]
    public void OldApproachStopsAtTheFalsePositiveFilterRowAndNeverSeesTheRealHeader()
    {
        var words = CreateRecommendedOrdersWordsWithADateLikeFilterRowAboveHeader();
        var rows = TableRowGrouper.GroupIntoRows(words);

        // The OLD (round-1) CountHeaderRows stops the instant ANY row
        // looks like data -- here that happens at row 1 (the filter row
        // itself), not because of the 2-row cap this time.
        var oldHeaderRowCount = HeaderBandLocator.CountHeaderRows(rows);
        var oldBands = ColumnResolver.BuildPartitionedColumnBands(rows.Take(oldHeaderRowCount).ToList());

        Assert.Equal(1, oldHeaderRowCount);
        Assert.Null(ColumnResolver.ResolveExact(oldBands, CreateRecommendedOrdersScanner.OrderQuantityHeaderLabel));
    }

    [Fact]
    public void FixedScannerSkipsThePseudoDataFilterRowAndHighlightsTheGenuineZero()
    {
        var highlights = CreateRecommendedOrdersScanner.FindZeroQuantityHighlights(CreateRecommendedOrdersWordsWithADateLikeFilterRowAboveHeader());

        var highlight = Assert.Single(highlights);
        Assert.Equal(200, highlight.Left);
        Assert.Equal(215, highlight.Right);
    }
}
