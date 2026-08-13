using System;
using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Decisions;
using RxVerifyOverlay.OrderAssist.Geometry;

namespace RxVerifyOverlay.OrderAssist.Scanning;

/// <summary>
/// End-to-end PURE pipeline for the "Create Recommended Orders" window:
/// raw OCR words in, zero-quantity cell highlights out. Ties together
/// TableRowGrouper -&gt; HeaderBandLocator -&gt; ColumnResolver (resolving the
/// Order Quantity column EXACTLY, never "Suggested Order Qty" — see
/// ColumnResolver's "substring trap" doc) -&gt; CellValueBucketizer -&gt;
/// ZeroQuantityDetector. No WPF/Win32/OCR-engine dependency at all, so
/// the WHOLE decision (not just its individual pieces) is unit-testable
/// with a synthetic OcrWord list shaped like the owner's reference
/// screenshot. OrderAssistCoordinator is the only caller in real use —
/// it supplies live OCR words and converts the returned LOCAL (capture-
/// region-relative) rects into actual screen highlights.
/// </summary>
public static class CreateRecommendedOrdersScanner
{
    public const string OrderQuantityHeaderLabel = "Order Quantity";

    /// <summary>One zero-quantity cell to highlight red — Left/Top/Right/Bottom are in the SAME coordinate space as the input OcrWords (capture-region-relative, not screen-absolute — see RowRect's own doc).</summary>
    public sealed record ZeroCellHighlight(int RowIndex, double Left, double Top, double Right, double Bottom);

    /// <summary>Empty (never null) if the Order Quantity column can't be resolved this tick (e.g. a bad/partial OCR capture) — degrades to "highlight nothing" rather than guessing, per ColumnResolver's fail-safe property.</summary>
    public static IReadOnlyList<ZeroCellHighlight> FindZeroQuantityHighlights(IReadOnlyList<OcrWord> words)
    {
        var rows = TableRowGrouper.GroupIntoRows(words);
        var headerRowCount = HeaderBandLocator.CountHeaderRows(rows);
        if (headerRowCount == 0 || headerRowCount >= rows.Count) return Array.Empty<ZeroCellHighlight>();

        var headerRows = rows.Take(headerRowCount).ToList();
        var bodyRows = rows.Skip(headerRowCount).ToList();

        var bands = ColumnResolver.BuildPartitionedColumnBands(headerRows);
        var orderQuantityColumn = ColumnResolver.ResolveExact(bands, OrderQuantityHeaderLabel);
        if (orderQuantityColumn is null) return Array.Empty<ZeroCellHighlight>();

        var cells = CellValueBucketizer.BucketColumn(bodyRows, orderQuantityColumn);

        var highlights = new List<ZeroCellHighlight>();
        foreach (var cell in cells)
        {
            if (cell.Bounds is not { } bounds) continue; // blank/unreadable cell -> Unknown, never flagged (see ZeroQuantityDetector doc)
            if (ZeroQuantityDetector.Classify(cell.Text) == ZeroCellState.Zero)
            {
                highlights.Add(new ZeroCellHighlight(cell.RowIndex, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
            }
        }

        return highlights;
    }
}
