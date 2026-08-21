using System.Collections.Generic;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Scanning;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// ROUND 4 (Will's SECOND repeat report on Order Quantity zero-flagging,
/// this time with a real screenshot — order screen round 4 qty-0 report).
/// REPLACES a round-4-in-progress scratch probe (same file name area) that
/// asked "does ColumnResolver's 1.5x-word-height clustering gap merge
/// 'Cost Per Unit' into 'Order Quantity' when their header text sits
/// closer together than the existing fixtures ever modeled?"
///
/// ANSWERED, with real pixel evidence, not another synthetic guess: the
/// owner's own Create Recommended Orders screenshot (order screen round 4
/// qty-0 report) was measured pixel-for-pixel (cropped, converted to BMP,
/// scanned for dark/text pixel columns). The real gap between "Cost Per
/// Unit"'s right edge and "Order Quantity"'s left edge is 43px, against a
/// measured header word height of ~15px -- so ColumnResolver's own
/// gapThreshold (1.5 * median word height = ~22.5px) is comfortably
/// CLEARED, not violated. The tight-spacing hypothesis the scratch probe
/// was checking is RULED OUT as this bug's root cause on Will's real
/// screen resolution/DPI -- this test fixture is that real measurement,
/// preserved as a permanent regression (not the probe's speculative
/// 8/12/16px variants, which don't reflect anything actually seen on
/// Will's screen).
///
/// The genuinely still-open explanation for the qty-0 report (see the
/// branch report) is a rare OCR miss on an isolated single-digit "0"
/// glyph -- not reproducible or fixable from pure C# geometry code without
/// live Windows telemetry. OrderAssistCoordinator.LogOrderQuantityColumnCellsIfEmpty
/// (added this round) will make that call definitively on Will's next
/// repro.
/// </summary>
public class OrderQuantityRealisticSpacingRegressionTests
{
    private static OcrWord Word(string text, double x, double y, double w, double h = 15) =>
        new() { Text = text, X = x, Y = y, W = w, H = h };

    [Fact]
    public void ZeroQuantityStillResolvesAtTheRealMeasuredHeaderGap()
    {
        // "Cost Per" / "Unit" (2-line header) right edge at x=48; "Order" /
        // "Quantity" (2-line header) left edge at x=91 -- a 43px gap,
        // pixel-measured directly from Will's own screenshot (see class
        // doc). "Suggested" / "Order Qty" starts at x=178 (matches
        // ColumnResolver's own "substring trap" neighbor) -- chrome-above-
        // header interference is already covered separately by
        // OrderHeaderBandRegressionTests, so this fixture stays a clean
        // header+data table to isolate the ONE thing this round measured:
        // the column-gap spacing itself.
        var words = new List<OcrWord>
        {
            // header line 1 (y=40, matches the real screenshot's own row pitch)
            Word("Cost", 6, 40, 22), Word("Per", 33, 40, 15),
            Word("Order", 91, 40, 29),
            Word("Suggested", 178, 40, 52),

            // header line 2 (y=52)
            Word("Unit", 6, 52, 20),
            Word("Quantity", 91, 52, 45),
            Word("Order", 178, 52, 28), Word("Qty", 211, 52, 17),

            // data row: Order Quantity = 0 (should highlight), Suggested Order Qty = 1 (should not).
            Word("36.42", 0, 70, 30, 10),
            Word("0", 95, 70, 10, 10),
            Word("1", 195, 70, 10, 10),
        };

        var highlights = CreateRecommendedOrdersScanner.FindZeroQuantityHighlights(words);

        var highlight = Assert.Single(highlights);
        Assert.Equal(0, highlight.RowIndex);
        // The highlighted cell must be the Order Quantity "0" (x~95), not
        // the Suggested Order Qty "1" (x~195) -- proves the substring-trap
        // safety net still holds at this realistic gap.
        Assert.InRange(highlight.Left, 90, 110);
    }
}
