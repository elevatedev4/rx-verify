using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Scanning;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// END-TO-END pure pipeline test for the Create Recommended Orders
/// window: a synthetic OCR word list shaped like the owner's reference
/// screenshot (adjacent "Order Quantity" / "Suggested Order Qty" columns,
/// the latter wrapped across two lines — see ColumnResolverTests for the
/// exact layout this reuses) with several data rows, proving the
/// substring trap is defended all the way through row/column resolution
/// AND zero-detection, not just at the column-resolver unit level. No
/// real pricing/patient data — every value is a small synthetic integer.
/// </summary>
public class CreateRecommendedOrdersScannerTests
{
    private static OcrWord Word(string text, double x, double y, double w, double h = 12) =>
        new() { Text = text, X = x, Y = y, W = w, H = h };

    private static List<OcrWord> BuildWords()
    {
        var words = new List<OcrWord>
        {
            // Header row 0
            Word("Cost", 0, 0, 20),
            Word("Per", 24, 0, 15),
            Word("Unit", 41, 0, 20),
            Word("Order", 90, 0, 30),
            Word("Quantity", 124, 0, 50),
            Word("Suggested", 205, 0, 55),
            // Header row 1 (wrapped continuation of "Suggested Order Qty")
            Word("Order", 203, 14, 30),
            Word("Qty", 236, 14, 25),

            // Body row 0: Order Quantity = 0 (zero -> should highlight),
            // Suggested Order Qty = 1 (irrelevant column, non-zero anyway).
            Word("5.00", 10, 40, 30), // Cost Per Unit value, makes this a "data row"
            Word("0", 130, 40, 10),
            Word("1", 250, 40, 10),

            // Body row 1: Order Quantity = 2 (non-zero -> no highlight).
            Word("6.00", 10, 60, 30),
            Word("2", 130, 60, 10),
            Word("2", 250, 60, 10),

            // Body row 2: Order Quantity = 3 (non-zero), but Suggested
            // Order Qty = 0 — THE TRAP: a naive substring/contains match
            // on "Order Quantity" could wrongly resolve to this column
            // and flag a false positive here.
            Word("7.00", 10, 80, 30),
            Word("3", 130, 80, 10),
            Word("0", 250, 80, 10),
        };

        return words;
    }

    [Fact]
    public void HighlightsOnlyTheGenuineZeroInTheOrderQuantityColumn()
    {
        var highlights = CreateRecommendedOrdersScanner.FindZeroQuantityHighlights(BuildWords());

        var highlight = Assert.Single(highlights);
        Assert.Equal(0, highlight.RowIndex);

        // Bounds are the "0" word's own rect (x=130..140, y=40..52) —
        // tight to the cell, not the whole column/row.
        Assert.Equal(130, highlight.Left);
        Assert.Equal(140, highlight.Right);
        Assert.Equal(40, highlight.Top);
        Assert.Equal(52, highlight.Bottom);
    }

    [Fact]
    public void NeverFlagsTheSuggestedColumnsOwnZeroValue()
    {
        var highlights = CreateRecommendedOrdersScanner.FindZeroQuantityHighlights(BuildWords());

        // Row 2 has Order Quantity=3 (non-zero) and Suggested Order
        // Qty=0 — must never appear in the result.
        Assert.DoesNotContain(highlights, h => h.RowIndex == 2);
    }

    [Fact]
    public void ReturnsNoHighlightsWhenTheColumnCannotBeResolved()
    {
        var wordsWithNoRecognizableHeader = new List<OcrWord>
        {
            Word("999.95", 0, 0, 40), // looks like data from the very first row -> no header at all
        };

        Assert.Empty(CreateRecommendedOrdersScanner.FindZeroQuantityHighlights(wordsWithNoRecognizableHeader));
    }

    /// <summary>
    /// ROUND 3 (Will, repeat complaint: "Changing qty to 0 doesn't
    /// highlight anything still ... the app should monitor the Order
    /// Quantity column for anything that says 0"). This pipeline is PURE
    /// and stateless — FindZeroQuantityHighlights never caches or
    /// memoizes anything between calls, so two calls with the SAME row but
    /// a DIFFERENT Order Quantity value (simulating a live edit —
    /// OrderAssistCoordinator re-runs this whole pipeline from a fresh OCR
    /// capture every ~500ms tick, see that class's own doc) must produce
    /// independent, correct results each time. This proves the "must react
    /// to edits" requirement holds at the pure-logic layer; the actual
    /// repeat-complaint root cause traced down to OCR read reliability on
    /// a row put into a selection/focus highlight while being edited —
    /// see Ocr/RowHighlightColorDetector's own root-cause doc — which is
    /// untestable off-Windows (no System.Drawing.Bitmap support on macOS).
    /// </summary>
    [Fact]
    public void CallingTwiceWithTheSameRowEditedToZeroDetectsTheEditIndependently()
    {
        var words = BuildWords();
        var beforeEdit = CreateRecommendedOrdersScanner.FindZeroQuantityHighlights(words);
        Assert.DoesNotContain(beforeEdit, h => h.RowIndex == 1); // row 1 starts at Order Quantity=2

        // Simulate the pharmacist editing row 1's Order Quantity cell from
        // "2" to "0" -- a brand-new word list, exactly what a fresh OCR
        // capture on the next tick would produce, with no relationship to
        // the previous call beyond covering the same logical row/screen.
        var editedWords = words.Select(w => w.X == 130 && w.Y == 60 ? new OcrWord { Text = "0", X = w.X, Y = w.Y, W = w.W, H = w.H } : w).ToList();

        var afterEdit = CreateRecommendedOrdersScanner.FindZeroQuantityHighlights(editedWords);

        Assert.Contains(afterEdit, h => h.RowIndex == 1);
    }

    // ---- ROUND 5: blank-cell candidate-zero (OCR silently missed a lone "0") ----

    /// <summary>
    /// ROUND 5 (Will's SECOND repeat report, this time with a screenshot
    /// showing a genuine, unflagged 0 — see FindZeroQuantityHighlights'
    /// own doc for the confirmed root cause and the option-(i)-vs-(ii)
    /// trade-off). A row whose Order Quantity cell produced NO OCR word at
    /// all (CellValueBucketizer's documented "blank -> Bounds=null"
    /// contract) but whose OTHER cells on that same row read fine must
    /// still be flagged — this is exactly what a missed lone "0" glyph
    /// looks like from the OCR word list's own point of view.
    /// </summary>
    [Fact]
    public void BlankOrderQuantityCellWithOtherRowContentIsFlaggedAsCandidateZero()
    {
        var words = BuildWords();

        // Body row 3: Cost Per Unit = 8.00 and Suggested Order Qty = 4 both
        // read fine -- proves the row itself was captured/OCR'd -- but NO
        // word at all exists in the Order Quantity column's own X range
        // (130ish), simulating OCR silently dropping the lone "0" glyph.
        words.Add(Word("8.00", 10, 100, 30));
        words.Add(Word("4", 250, 100, 10));

        var highlights = CreateRecommendedOrdersScanner.FindZeroQuantityHighlights(words);

        Assert.Contains(highlights, h => h.RowIndex == 3);
    }

    [Fact]
    public void BlankOrderQuantityCellWithNoCorroboratingNumericContentIsNotFlagged()
    {
        // Regression guard for the "never coerce blank to zero" contract
        // (Geometry/CellValue.cs): the round-5 gate is deliberately
        // stronger than "the row has SOME other word" (TableRowGrouper
        // guarantees that trivially -- see that class's own doc, and
        // FindZeroQuantityHighlights' own doc for why a bare word-count
        // check would barely filter anything). It requires
        // HeaderBandLocator.IsDataRow -- at least one decimal-formatted
        // number elsewhere in the row, same signal HeaderRowWindowSelector
        // already trusts to spot a genuine grid data row. Here the only
        // other word in the row ("Backorder") isn't numeric at all, so this
        // must stay silent rather than guess.
        var words = new List<OcrWord>
        {
            Word("Cost", 0, 0, 20),
            Word("Per", 24, 0, 15),
            Word("Unit", 41, 0, 20),
            Word("Order", 90, 0, 30),
            Word("Quantity", 124, 0, 50),

            // A genuine, fully-legible data row immediately after the
            // header -- gives HeaderRowWindowSelector a real IsDataRow row
            // right after the header so it can't greedily fold the row
            // below (the one this test actually cares about) into what it
            // treats as the header window (see the round-5 test's own
            // history/comment in this file for why an all-text row sitting
            // directly under a single-line header is otherwise ambiguous).
            Word("5.00", 10, 40, 30),
            Word("3", 130, 40, 10),

            // The row under test: Order Quantity cell blank (nothing lands
            // in its partition), and its only other content ("Backorder")
            // isn't decimal-formatted -- not corroborated as real numeric
            // table data.
            Word("Backorder", 300, 60, 60),
        };

        var highlights = CreateRecommendedOrdersScanner.FindZeroQuantityHighlights(words);

        Assert.Empty(highlights);
    }

    [Fact]
    public void CandidateZeroHighlightBoundsSpanTheOrderQuantityColumnAndTheRowsOtherContent()
    {
        var words = BuildWords();
        words.Add(Word("8.00", 10, 100, 30, h: 14));
        words.Add(Word("4", 250, 100, 10, h: 14));

        var highlights = CreateRecommendedOrdersScanner.FindZeroQuantityHighlights(words);

        var highlight = Assert.Single(highlights, h => h.RowIndex == 3);

        // Horizontal span is the Order Quantity header CLUSTER's own extent
        // (90..174 -- "Order" at x=90 w=30 and "Quantity" at x=124 w=50
        // cluster into one column band, see BuildWords' header row 0) --
        // never the wider partition (which would spill toward the
        // neighboring Suggested Order Qty column).
        Assert.Equal(90, highlight.Left);
        Assert.Equal(174, highlight.Right);

        // Vertical span comes from the row's OTHER words (y=100..114).
        Assert.Equal(100, highlight.Top);
        Assert.Equal(114, highlight.Bottom);
    }
}
