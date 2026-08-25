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
/// TableRowGrouper -&gt; HeaderRowWindowSelector (2026-08-18, W-T76/78/81 fix
/// — searches for whichever leading row(s) actually contain the header,
/// rather than assuming HeaderBandLocator's first 1-2 non-data rows are
/// it) -&gt; ColumnResolver (resolving the Order Quantity column EXACTLY,
/// never "Suggested Order Qty" — see ColumnResolver's "substring trap"
/// doc) -&gt; CellValueBucketizer -&gt;
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

        // 2026-08-18 (W-T76/78/81 fix — see HeaderRowWindowSelector's own
        // root-cause doc): the header is no longer assumed to be whatever
        // HeaderBandLocator finds at the very top of the table — window
        // chrome (title bar, menu bar) reliably sits ABOVE the real grid
        // header on both target windows, so this now searches for whichever
        // leading row(s) actually contain the Order Quantity label.
        var winner = HeaderRowWindowSelector.SelectBest(rows, new[] { OrderQuantityHeaderLabel });
        if (winner is null) return Array.Empty<ZeroCellHighlight>();

        var bodyRows = rows.Skip(winner.StartRowIndex + winner.RowCount).ToList();

        var orderQuantityColumn = ColumnResolver.ResolveExact(winner.Bands, OrderQuantityHeaderLabel);
        if (orderQuantityColumn is null) return Array.Empty<ZeroCellHighlight>();

        var cells = CellValueBucketizer.BucketColumn(bodyRows, orderQuantityColumn);

        var highlights = new List<ZeroCellHighlight>();
        foreach (var cell in cells)
        {
            if (cell.Bounds is { } bounds)
            {
                if (ZeroQuantityDetector.Classify(cell.Text) == ZeroCellState.Zero)
                {
                    highlights.Add(new ZeroCellHighlight(cell.RowIndex, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
                }
                continue;
            }

            // ROUND 5 (Will's SECOND repeat report on this exact symptom,
            // with a screenshot showing a genuine unflagged 0 — see
            // OrderAssistCoordinator.LogOrderQuantityColumnCellsIfEmpty's
            // own doc for the confirmed root cause: Windows.Media.Ocr
            // architecturally struggles with a single, context-free digit,
            // and a lone "0" is exactly that — it can silently produce NO
            // word at all rather than misreading it, which is
            // INDISTINGUISHABLE at this point from a genuinely blank cell.
            //
            // Evaluated against the branch brief's two options: (i) a
            // targeted second OCR pass over just this column, more
            // aggressively upscaled, was the brief's stated preference, but
            // would add a full extra Windows.Media.Ocr call to EVERY tick
            // that has a blank cell here — directly working against track
            // 1's own "too slow" complaint on this same branch, and its
            // crop/coordinate-remapping math couldn't be verified end-to-end
            // without a real Windows build (not available in this
            // environment — see branch report). Chose (ii) instead, exactly
            // as the brief allows ("gate it behind row-quality checks and
            // flag it clearly in your report").
            //
            // GATE (the "row-quality check" — deliberately stronger than
            // just "the row has SOME other word"): TableRowGrouper never
            // produces a row with zero words at all (see that class's own
            // doc), so a row existing at all is a low bar that would barely
            // filter anything. Instead this reuses HeaderBandLocator.IsDataRow
            // — the SAME "does any word in this row look like a decimal-
            // formatted number" heuristic HeaderRowWindowSelector already
            // trusts elsewhere in this exact pipeline to tell a real grid
            // data row apart from chrome/noise — as the corroborating
            // signal. A genuine Create Recommended Orders row always
            // carries at least one decimal-formatted numeric column (Cost
            // Per Unit, per that class's own doc); requiring one here means
            // a blank Order Quantity cell only turns into a highlight when
            // the REST of its row independently proves this tick's OCR
            // pass actually read a real table row, not a garbage/partial
            // capture — the exact distinction Geometry/CellValue.cs's own
            // "never coerce blank to zero" contract warns callers to
            // preserve. This gate is intentionally narrow (this method
            // only, never CellValueBucketizer/CellValue itself, which keep
            // their existing "blank -> Unknown" contract for every other
            // caller, e.g. CatalogSubstitutionScanner).
            var row = bodyRows[cell.RowIndex];
            if (!HeaderBandLocator.IsDataRow(row)) continue; // no corroborating numeric content -- stay silent per ZeroQuantityDetector's own "don't guess" posture

            // BOUNDS APPROXIMATION: no OCR word exists to give an exact
            // rect, so the highlight uses the Order Quantity column's own
            // header-text extent (Left/Right — not the wider partition,
            // which would spill into a neighboring column's territory) for
            // the horizontal span, and the row's OTHER words' own Y-range
            // for the vertical span (that IS this row, wherever its other
            // cells sit).
            var otherWordsInRow = row
                .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .Where(w => !CellValueBucketizer.IsCenterWithinPartition(w, orderQuantityColumn))
                .ToList();

            // Can't actually be empty here: cell.Bounds is null means no
            // word in `row` matched the partition, and TableRowGrouper
            // guarantees `row` itself is never empty (see that class's own
            // doc) — so every one of row's words is, by construction,
            // "other". Guarded anyway rather than assumed, since there's no
            // sane rect to draw without at least one word's own Y-range.
            if (otherWordsInRow.Count == 0) continue;

            highlights.Add(new ZeroCellHighlight(
                cell.RowIndex,
                orderQuantityColumn.Left,
                otherWordsInRow.Min(w => w.Y),
                orderQuantityColumn.Right,
                otherWordsInRow.Max(w => w.Y + w.H)));
        }

        return highlights;
    }
}
