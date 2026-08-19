using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Geometry;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Unit tests for HeaderRowWindowSelector (W-T76/78/81 fix, 2026-08-18) —
/// the pure header-row search/scoring behind CreateRecommendedOrdersScanner
/// and CatalogSubstitutionScanner. See its own class doc for the confirmed
/// root cause (window chrome — title bar, menu bar, on Catalog Substitution
/// also a filter/toolbar row — reliably exhausted the OLD fixed 2-row
/// header search before it ever reached the real grid header row). See
/// OrderHeaderBandRegressionTests.cs for full end-to-end scanner-level
/// reproductions of Will's real diagnostic log shapes; these tests exercise
/// the selector itself in isolation with simpler geometry.
/// </summary>
public class HeaderRowWindowSelectorTests
{
    private static OcrWord Word(string text, double x, double y, double w, double h = 12) =>
        new() { Text = text, X = x, Y = y, W = w, H = h };

    private static List<OcrWord> Row(params OcrWord[] words) => words.ToList();

    [Fact]
    public void FindsTheHeaderRowEvenWhenTwoChromeRowsPrecedeIt()
    {
        // Row0: window title chrome. Row1: menu-bar chrome. Row2: the REAL
        // header. Row3: a data row. The old fixed 2-row cap would exhaust
        // its whole budget on rows 0-1 and never reach row2 at all.
        var rows = new List<IReadOnlyList<OcrWord>>
        {
            Row(Word("Some", 0, 0, 30), Word("Window", 40, 0, 50)),
            Row(Word("Menu", 0, 20, 30), Word("Bar", 40, 20, 25)),
            Row(Word("Supplier", 0, 40, 50), Word("Rebate", 150, 40, 40), Word("Cost", 193, 40, 30), Word("Per", 226, 40, 20), Word("Unit", 249, 40, 25)),
            Row(Word("ANDA", 10, 60, 30), Word("7.00", 370, 60, 30)),
        };

        var winner = HeaderRowWindowSelector.SelectBest(rows, new[] { "Supplier", "Rebate Cost Per Unit" });

        Assert.NotNull(winner);
        Assert.Equal(2, winner!.Score);
        Assert.NotNull(ColumnResolver.ResolveExact(winner.Bands, "Supplier"));
        Assert.NotNull(ColumnResolver.ResolveExact(winner.Bands, "Rebate Cost Per Unit"));
    }

    [Fact]
    public void OldFixedTwoRowCapWouldHaveMissedTheSameHeader()
    {
        // Pins the regression: reconstructing exactly what the OLD code
        // computed (HeaderBandLocator.CountHeaderRows, capped at 2, then
        // ColumnResolver straight off those rows) on the IDENTICAL input
        // above must fail to resolve either expected column — proving this
        // is a genuine fix, not just new coverage of already-working code.
        var rows = new List<IReadOnlyList<OcrWord>>
        {
            Row(Word("Some", 0, 0, 30), Word("Window", 40, 0, 50)),
            Row(Word("Menu", 0, 20, 30), Word("Bar", 40, 20, 25)),
            Row(Word("Supplier", 0, 40, 50), Word("Rebate", 150, 40, 40), Word("Cost", 193, 40, 30), Word("Per", 226, 40, 20), Word("Unit", 249, 40, 25)),
            Row(Word("ANDA", 10, 60, 30), Word("7.00", 370, 60, 30)),
        };

        var oldHeaderRowCount = HeaderBandLocator.CountHeaderRows(rows); // capped at the old default of 2
        var oldHeaderRows = rows.Take(oldHeaderRowCount).ToList();
        var oldBands = ColumnResolver.BuildPartitionedColumnBands(oldHeaderRows);

        Assert.Equal(2, oldHeaderRowCount); // confirms the cap was exhausted on the 2 chrome rows
        Assert.Null(ColumnResolver.ResolveExact(oldBands, "Supplier"));
        Assert.Null(ColumnResolver.ResolveExact(oldBands, "Rebate Cost Per Unit"));
    }

    [Fact]
    public void ReturnsNullWhenNoCandidateMatchesAnyExpectedLabel()
    {
        var rows = new List<IReadOnlyList<OcrWord>>
        {
            Row(Word("Totally", 0, 0, 50), Word("Unrelated", 60, 0, 60)),
            Row(Word("ANDA", 10, 40, 30), Word("7.00", 100, 40, 30)),
        };

        Assert.Null(HeaderRowWindowSelector.SelectBest(rows, new[] { "Supplier" }));
    }

    [Fact]
    public void ReturnsNullWhenTheFirstRowIsAlreadyData()
    {
        var rows = new List<IReadOnlyList<OcrWord>>
        {
            Row(Word("7.00", 0, 0, 30)),
        };

        Assert.Null(HeaderRowWindowSelector.SelectBest(rows, new[] { "Supplier" }));
    }

    [Fact]
    public void CombinesTwoLinesForAWrappedHeaderJustLikeTheOldCapDid()
    {
        var rows = new List<IReadOnlyList<OcrWord>>
        {
            Row(Word("Suggested", 205, 0, 55)),
            Row(Word("Order", 203, 14, 30), Word("Qty", 236, 14, 25)),
            Row(Word("1.00", 0, 40, 30)),
        };

        var winner = HeaderRowWindowSelector.SelectBest(rows, new[] { "Suggested Order Qty" });

        Assert.NotNull(winner);
        Assert.Equal(0, winner!.StartRowIndex);
        Assert.Equal(2, winner.RowCount);
        Assert.NotNull(ColumnResolver.ResolveExact(winner.Bands, "Suggested Order Qty"));
    }

    // ---- ROUND 2 (W-T85 bug 1): a chrome row ABOVE the header that itself
    // misclassifies as "data" must not kill the whole scan ---------------

    [Fact]
    public void SkipsPastAFalsePositiveDataLookingChromeRowToFindTheRealHeader()
    {
        // Row0: title/menu chrome (not data). Row1: a FILTER row containing
        // something decimal-shaped (mirrors Will's real screenshot: "Order
        // Date: 8/18/2026" — a date/spinner glyph OCR can turn into, or
        // that genuinely reads as, a decimal-point token) -- this row
        // WOULD classify as HeaderBandLocator.IsDataRow == true. Row2: the
        // REAL header, two rows further down than round 1's fix alone
        // could tolerate once row1 already looked like data. Row3: real
        // data.
        var rows = new List<IReadOnlyList<OcrWord>>
        {
            Row(Word("Create", 0, 0, 60), Word("Recommended", 65, 0, 110), Word("Orders", 180, 0, 55)),
            Row(Word("Order", 0, 20, 40), Word("Date", 45, 20, 35), Word("8.18", 90, 20, 40)), // false-positive "data" row
            Row(Word("Order", 0, 40, 40), Word("Quantity", 45, 40, 60)),
            Row(Word("0", 10, 60, 15)),
        };

        var winner = HeaderRowWindowSelector.SelectBest(rows, new[] { "Order Quantity" });

        Assert.NotNull(winner);
        Assert.Equal(2, winner!.StartRowIndex);
        Assert.NotNull(ColumnResolver.ResolveExact(winner.Bands, "Order Quantity"));
    }

    [Fact]
    public void StopsScanningOnceARealHeaderCandidateHasAlreadyScored()
    {
        // Once the real header (row0) has already been found and scored,
        // a LATER data-looking row (row1) still correctly ends the scan —
        // this is the ORIGINAL round-1 behavior, unchanged for the case it
        // was already right about. Row2 (more header-shaped text, further
        // down) must NEVER be reachable/considered once genuine data (row1)
        // has been seen after a real header was already found.
        var rows = new List<IReadOnlyList<OcrWord>>
        {
            Row(Word("Order", 0, 0, 40), Word("Quantity", 45, 0, 60)),
            Row(Word("7.00", 10, 20, 30)), // genuine data, right after the real header
            Row(Word("Supplier", 0, 40, 50)), // must never be reached
        };

        var candidates = HeaderRowWindowSelector.EnumerateCandidates(rows, new[] { "Order Quantity", "Supplier" });

        Assert.DoesNotContain(candidates, c => c.StartRowIndex == 2);
    }

    [Fact]
    public void FalsePositiveDataRowIsNeverItselfUsedAsAHeaderCandidateStart()
    {
        var rows = new List<IReadOnlyList<OcrWord>>
        {
            Row(Word("8.18", 0, 0, 40)), // looks like data from row 0
            Row(Word("Order", 0, 20, 40), Word("Quantity", 45, 20, 60)),
            Row(Word("0", 10, 40, 15)),
        };

        var candidates = HeaderRowWindowSelector.EnumerateCandidates(rows, new[] { "Order Quantity" });

        Assert.DoesNotContain(candidates, c => c.StartRowIndex == 0);
        Assert.Contains(candidates, c => c.StartRowIndex == 1 && c.Score > 0);
    }

    // ---- LabelsAreCloseMatch (scoring-only OCR-noise tolerance) --------

    [Fact]
    public void ExactNormalizedMatchIsClose()
    {
        Assert.True(HeaderRowWindowSelector.LabelsAreCloseMatch("Rebate Cost Per Unit", "rebate   COST per unit"));
    }

    [Fact]
    public void SingleCharacterOcrMisreadOnALongLabelIsStillClose()
    {
        // "Suppller" (double L, a plausible OCR misread) vs "Supplier" —
        // wait, that's edit distance 1 but Supplier is only 8 characters;
        // use a longer label to stay safely above the length floor.
        Assert.True(HeaderRowWindowSelector.LabelsAreCloseMatch("Rebate Cost Per Unlt", "Rebate Cost Per Unit"));
    }

    [Fact]
    public void CompletelyDifferentLabelsAreNeverClose()
    {
        Assert.False(HeaderRowWindowSelector.LabelsAreCloseMatch("Order Quantity", "Suggested Order Qty"));
    }

    [Fact]
    public void ShortLabelsAreNeverNearMatchedEvenWithinOneEditDistance()
    {
        // Guards the substring-trap-adjacent risk: a short real column name
        // ("Supplier" is the shortest real target) must never near-match
        // some other short, unrelated word purely because both are tiny.
        Assert.False(HeaderRowWindowSelector.LabelsAreCloseMatch("Cost", "Cast"));
    }
}
