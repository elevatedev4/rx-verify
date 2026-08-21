using System;
using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Decisions;
using RxVerifyOverlay.OrderAssist.Geometry;

namespace RxVerifyOverlay.OrderAssist.Scanning;

/// <summary>
/// End-to-end PURE pipeline for the "Recommended Order - Catalog Item
/// Substitution Selection" window: raw OCR words in, every Order Assist
/// annotation for that window out (see <see cref="CatalogAnnotations"/>).
/// The header row(s) are located via HeaderRowWindowSelector (2026-08-18,
/// W-T76/78/81 fix — see its own root-cause doc: this window's title bar
/// PLUS its own menu bar PLUS a "Catalog Items Filter: All" toolbar row
/// all sit above the real grid header, more leading chrome rows than the
/// old fixed 2-row assumption ever accounted for). Resolves the Supplier,
/// Rebate Cost Per Unit, AND Shipping Size columns EXACTLY (never "Rebate
/// Cost" or "Cost Per Unit" — the same substring-trap shape exists on this
/// window too, see ColumnResolver's doc), reads every body row's cells,
/// then hands them to SubstitutionRecommender (round 3: a savings badge per
/// row cheaper than McKesson, not just one green pick — see that class's
/// own doc), PackageClassifier (best-large/best-small package markers), and
/// SortOrderChecker (the column-header sort badge) — each a separate,
/// independently fail-closed decision (see each one's own doc). See
/// CreateRecommendedOrdersScanner's doc for why this stays a pure,
/// OrderAssistCoordinator-only-caller pipeline.
///
/// ROUND 3 also fixes the "first row is skipped" repeat complaint (Will:
/// "the first row, again, is going to be highlighted in blue at the
/// start... it is currently being skipped still and not analyzed") and a
/// new report that a McKesson-YELLOW-highlighted row gets skipped too —
/// see Ocr/RowHighlightColorDetector's own root-cause doc for the actual
/// fix (it lives in the OCR/bitmap preprocessing step OrderAssistCoordinator
/// runs before this class ever sees any words, not in this file — this
/// class has never been color-aware, see that class's own doc).
/// </summary>
public static class CatalogSubstitutionScanner
{
    public const string SupplierHeaderLabel = "Supplier";
    public const string RebateCostPerUnitHeaderLabel = "Rebate Cost Per Unit";
    public const string ShippingSizeHeaderLabel = "Shipping Size";

    /// <summary>One row's savings badge, anchored to the row's own FULL extent (Left/Top/Right/Bottom — see RowBounds.ComputeUniform for why Top/Bottom are normalized to the table's own canonical row height, round 3: "make the height match the height of the row"). SavingsDisplay/MeetsThreshold drive the badge's text/color — see SubstitutionRecommender.RowSavings.</summary>
    public sealed record SavingsBadge(int RowIndex, double Left, double Top, double Right, double Bottom, string SavingsDisplay, bool MeetsThreshold);

    /// <summary>A single highlighted row with no percentage label of its own — used for the best-large/best-small package marker. Same FULL ROW extent convention as SavingsBadge.</summary>
    public sealed record RowMarker(int RowIndex, double Left, double Top, double Right, double Bottom, string? Label);

    /// <summary>The sort-order badge for the Rebate Cost Per Unit column — Left/Right are that column's own raw header-text extent (not its wider bucketing partition) and Top is the header band's own top edge, so callers draw the badge just ABOVE that, never over the header text itself. IsSorted lets a renderer pick a color without re-parsing Text's glyph.</summary>
    public sealed record ColumnBadge(double Left, double Right, double Top, string Text, bool IsSorted);

    /// <summary>Left/Right/Top of the Rebate Cost Per Unit column's own header band — round 3: available whenever the column itself resolves, INDEPENDENT of whether SortIndicatorBadge's own text resolved that tick (see that record's own "fewer than 2 rows" Unknown case). OrderAssistCoordinator uses this to anchor the "Processing" indicator (Will: "add a 'Processing' by the sorted by rebate notice") on a tick where the SAVINGS analysis is still debouncing but the column itself is genuinely there.</summary>
    public sealed record ColumnAnchor(double Left, double Right, double Top);

    /// <summary>
    /// Every Order Assist annotation for one tick of this window, bundled
    /// together. SavingsBadges/BestLargePackageMarker/BestSmallPackageMarker/
    /// SortIndicatorBadge/McKessonBaselineMarker are each independently
    /// fail-closed (see SubstitutionRecommender, PackageClassifier,
    /// SortOrderChecker) — an empty/null field means "this particular
    /// decision found nothing worth drawing this tick", never a
    /// placeholder for a wrong guess.
    /// </summary>
    public sealed record CatalogAnnotations(
        IReadOnlyList<SavingsBadge> SavingsBadges,
        RowMarker? BestLargePackageMarker,
        RowMarker? BestSmallPackageMarker,
        ColumnBadge? SortIndicatorBadge,
        ColumnAnchor? CostColumnHeaderAnchor,
        RowMarker? McKessonBaselineMarker = null)
    {
        public static readonly CatalogAnnotations Empty = new(Array.Empty<SavingsBadge>(), null, null, null, null, null);
    }

    /// <summary>CatalogAnnotations.Empty if the Supplier or Rebate Cost Per Unit column can't be resolved at all (nothing further downstream can be computed without those two) — the Shipping Size column is resolved independently and its absence only suppresses the package markers, never the rest.</summary>
    public static CatalogAnnotations Analyze(IReadOnlyList<OcrWord> words)
    {
        var rows = TableRowGrouper.GroupIntoRows(words);

        // 2026-08-18 (W-T76/78/81 fix — see HeaderRowWindowSelector's own
        // root-cause doc): scored against the two columns actually
        // REQUIRED for this window (Shipping Size, resolved further below,
        // is optional — its absence only suppresses the package markers)
        // rather than assuming the header sits in the first 1-2 rows.
        var winner = HeaderRowWindowSelector.SelectBest(rows, new[] { SupplierHeaderLabel, RebateCostPerUnitHeaderLabel });
        if (winner is null) return CatalogAnnotations.Empty;

        var bands = winner.Bands;
        var bodyRows = rows.Skip(winner.StartRowIndex + winner.RowCount).ToList();

        var supplierColumn = ColumnResolver.ResolveExact(bands, SupplierHeaderLabel);
        var costColumn = ColumnResolver.ResolveExact(bands, RebateCostPerUnitHeaderLabel);
        if (supplierColumn is null || costColumn is null) return CatalogAnnotations.Empty;

        var costColumnHeaderAnchor = new ColumnAnchor(costColumn.Left, costColumn.Right, winner.Top);

        var supplierCells = CellValueBucketizer.BucketColumn(bodyRows, supplierColumn);
        var costCells = CellValueBucketizer.BucketColumn(bodyRows, costColumn);

        var rowInputs = new List<SubstitutionRecommender.CatalogRowInput>(bodyRows.Count);
        var costTextsInRowOrder = new List<string?>(bodyRows.Count);
        for (var i = 0; i < bodyRows.Count; i++)
        {
            var supplierText = supplierCells.FirstOrDefault(c => c.RowIndex == i)?.Text ?? "";
            var costText = costCells.FirstOrDefault(c => c.RowIndex == i)?.Text ?? "";
            rowInputs.Add(new SubstitutionRecommender.CatalogRowInput(i, supplierText, costText));
            costTextsInRowOrder.Add(costText);
        }

        // ROUND 3 (Will: "Make the height match the height of the row too
        // (which should be the same for all rows)") — computed ONCE per
        // tick from the table's own row-to-row spacing, then used for every
        // full-row rect built below (package markers) instead of each row's
        // own raw OCR'd word extent.
        var canonicalRowHeight = RowPitchEstimator.EstimateCanonicalHeight(bodyRows);

        // ROUND 4 (Will verbatim, two combined asks that share one fix):
        //   3. "Try highlighting the whole row in green ... make sure the
        //      green highlight covers the whole row" -- a fill anchored to
        //      each ROW's own OCR'd word extent (round 3's approach) only
        //      covers whatever cells happened to have text that tick, not
        //      "the whole row".
        //   4. "the % indicator ... always at the right side (one is
        //      floating over due to AWP and AWP per unit being empty" --
        //      same root cause from the other side: the badge is anchored
        //      at rowRect.Right, which shrinks whenever a trailing column
        //      (AWP, AWP Per Unit, ...) has no OCR'd text that tick.
        // FIX: anchor the badge/fill's LEFT/RIGHT to the table's own
        // resolved column bands (bands.Left/Right are the header's real
        // text extents, stable every tick regardless of which BODY cells
        // happen to be blank) instead of the row's own matched-word
        // extent. Top/Bottom still come from RowBounds.ComputeUniform
        // (per-row, canonical height) -- only the horizontal extent
        // changes. bandsLeftEdge/bandsRightEdge are null (falling back to
        // the row's own raw extent) only if bands is somehow empty, which
        // can't happen once winner is non-null (HeaderRowWindowSelector
        // never returns a winner with zero bands).
        var bandsLeftEdge = bands.Count > 0 ? bands.Min(b => b.Left) : (double?)null;
        var bandsRightEdge = bands.Count > 0 ? bands.Max(b => b.Right) : (double?)null;

        // ROUND 3 REDESIGN (Will: "Always Calculate the savings for each
        // item cheaper than mckesson and display it at the end of the row.
        // Below our threshold, show in yellow, above show green.") --
        // ROUND 4 (Will: "highlighting the whole row in green" -- see
        // above) restores a full-row fill alongside the % badge (never
        // instead of it — OrderAssistOverlayWindow keeps drawing both).
        // One entry per qualifying row.
        var savingsBadges = new List<SavingsBadge>();
        foreach (var savings in SubstitutionRecommender.EvaluateSavings(rowInputs))
        {
            if (RowBounds.ComputeUniform(bodyRows[savings.RowIndex], canonicalRowHeight) is not { } rowRect) continue;

            var left = bandsLeftEdge ?? rowRect.Left;
            var right = bandsRightEdge ?? rowRect.Right;

            savingsBadges.Add(new SavingsBadge(
                savings.RowIndex, left, rowRect.Top, right, rowRect.Bottom,
                savings.SavingsDisplay, savings.Tier == SavingsTier.AboveThreshold));
        }

        // ROUND 4 (Will: "also highlight the cheapest mckesson item that is
        // being compared in some intuitive color") — the SAME row
        // SubstitutionRecommender used as its baseline (see
        // FindCheapestMcKessonRowIndex's own doc: shares selection logic
        // with EvaluateSavings so the two can never disagree), drawn as a
        // full-row marker like the package-class markers below. Null
        // (nothing drawn) under the exact same conditions EvaluateSavings
        // itself would produce no badges at all — see that method's doc.
        RowMarker? mckessonBaselineMarker = null;
        if (SubstitutionRecommender.FindCheapestMcKessonRowIndex(rowInputs) is { } baselineIdx &&
            RowBounds.ComputeUniform(bodyRows[baselineIdx], canonicalRowHeight) is { } baselineRect)
        {
            var left = bandsLeftEdge ?? baselineRect.Left;
            var right = bandsRightEdge ?? baselineRect.Right;
            mckessonBaselineMarker = new RowMarker(baselineIdx, left, baselineRect.Top, right, baselineRect.Bottom, "McKesson (compared)");
        }

        RowMarker? bestLargeMarker = null;
        RowMarker? bestSmallMarker = null;
        var shippingSizeColumn = ColumnResolver.ResolveExact(bands, ShippingSizeHeaderLabel);
        if (shippingSizeColumn is not null)
        {
            var shippingCells = CellValueBucketizer.BucketColumn(bodyRows, shippingSizeColumn);
            var packageRowInputs = new List<PackageClassifier.PackageRowInput>(bodyRows.Count);
            for (var i = 0; i < bodyRows.Count; i++)
            {
                var shippingText = shippingCells.FirstOrDefault(c => c.RowIndex == i)?.Text ?? "";
                var costText = costCells.FirstOrDefault(c => c.RowIndex == i)?.Text ?? "";
                packageRowInputs.Add(new PackageClassifier.PackageRowInput(i, shippingText, costText));
            }

            var picks = PackageClassifier.FindBestPerClass(packageRowInputs);

            if (picks.BestLargeRowIndex is { } largeIdx &&
                RowBounds.ComputeUniform(bodyRows[largeIdx], canonicalRowHeight) is { } largeRect)
            {
                bestLargeMarker = new RowMarker(largeIdx, largeRect.Left, largeRect.Top, largeRect.Right, largeRect.Bottom, "best large pkg");
            }

            if (picks.BestSmallRowIndex is { } smallIdx &&
                RowBounds.ComputeUniform(bodyRows[smallIdx], canonicalRowHeight) is { } smallRect)
            {
                bestSmallMarker = new RowMarker(smallIdx, smallRect.Left, smallRect.Top, smallRect.Right, smallRect.Bottom, "best small pkg");
            }
        }

        ColumnBadge? sortBadge = null;
        var sortState = SortOrderChecker.Classify(costTextsInRowOrder);
        var sortText = SortOrderChecker.Describe(sortState);
        if (sortText is not null)
        {
            // winner.Top is already the winning header row-window's own
            // topmost word Y (see HeaderRowWindowSelector.Candidate) — no
            // need to re-walk headerRows' words for this.
            sortBadge = new ColumnBadge(costColumn.Left, costColumn.Right, winner.Top, sortText, sortState == SortIndicatorState.Sorted);
        }

        return new CatalogAnnotations(savingsBadges, bestLargeMarker, bestSmallMarker, sortBadge, costColumnHeaderAnchor, mckessonBaselineMarker);
    }
}
