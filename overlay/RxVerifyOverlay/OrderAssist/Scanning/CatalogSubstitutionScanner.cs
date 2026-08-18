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
/// then hands them to
/// SubstitutionRecommender (green pick), DualHighlightPlanner (yellow
/// McKesson contrast), PackageClassifier (best-large/best-small package
/// markers), and SortOrderChecker (the column-header sort badge) — each a
/// separate, independently fail-closed decision (see each one's own doc).
/// See CreateRecommendedOrdersScanner's doc for why this stays a pure,
/// OrderAssistCoordinator-only-caller pipeline.
/// </summary>
public static class CatalogSubstitutionScanner
{
    public const string SupplierHeaderLabel = "Supplier";
    public const string RebateCostPerUnitHeaderLabel = "Rebate Cost Per Unit";
    public const string ShippingSizeHeaderLabel = "Shipping Size";

    /// <summary>The one row to highlight green plus its savings label — Left/Top/Right/Bottom are the FULL ROW's extent (not just the cost cell), in the same coordinate space as the input OcrWords.</summary>
    public sealed record SubstitutionHighlight(int RowIndex, double Left, double Top, double Right, double Bottom, string SavingsDisplay);

    /// <summary>A single highlighted row with no percentage label of its own — used for BOTH the yellow McKesson contrast row (Label null) and a best-large/best-small package marker (Label e.g. "best large pkg"). Same FULL ROW extent convention as SubstitutionHighlight.</summary>
    public sealed record RowMarker(int RowIndex, double Left, double Top, double Right, double Bottom, string? Label);

    /// <summary>The sort-order badge for the Rebate Cost Per Unit column — Left/Right are that column's own raw header-text extent (not its wider bucketing partition) and Top is the header band's own top edge, so callers draw the badge just ABOVE that, never over the header text itself. IsSorted lets a renderer pick a color without re-parsing Text's glyph.</summary>
    public sealed record ColumnBadge(double Left, double Right, double Top, string Text, bool IsSorted);

    /// <summary>
    /// Every Order Assist annotation for one tick of this window, bundled
    /// together. Every field is independently nullable because every
    /// decision behind it is independently fail-closed (see
    /// SubstitutionRecommender, DualHighlightPlanner, PackageClassifier,
    /// SortOrderChecker) — a null field means "this particular decision
    /// found nothing worth drawing this tick", never a placeholder for a
    /// wrong guess.
    /// </summary>
    public sealed record CatalogAnnotations(
        SubstitutionHighlight? GreenHighlight,
        RowMarker? YellowHighlight,
        RowMarker? BestLargePackageMarker,
        RowMarker? BestSmallPackageMarker,
        ColumnBadge? SortIndicatorBadge)
    {
        public static readonly CatalogAnnotations Empty = new(null, null, null, null, null);
    }

    /// <summary>Back-compat convenience for callers that only want the round-1 green pick — equivalent to Analyze(words).GreenHighlight.</summary>
    public static SubstitutionHighlight? FindRecommendation(IReadOnlyList<OcrWord> words) => Analyze(words).GreenHighlight;

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

        var recommendation = SubstitutionRecommender.Evaluate(rowInputs);

        SubstitutionHighlight? greenHighlight = null;
        if (recommendation.Recommendation == SubstitutionRecommendation.RecommendSecondary &&
            recommendation.RecommendedRowIndex is { } greenRowIndex &&
            RowBounds.Compute(bodyRows[greenRowIndex]) is { } greenRect)
        {
            greenHighlight = new SubstitutionHighlight(greenRowIndex, greenRect.Left, greenRect.Top, greenRect.Right, greenRect.Bottom, recommendation.SavingsDisplay ?? "");
        }

        RowMarker? yellowHighlight = null;
        if (DualHighlightPlanner.FindMcKessonHighlightRowIndex(rowInputs, recommendation) is { } yellowRowIndex &&
            RowBounds.Compute(bodyRows[yellowRowIndex]) is { } yellowRect)
        {
            yellowHighlight = new RowMarker(yellowRowIndex, yellowRect.Left, yellowRect.Top, yellowRect.Right, yellowRect.Bottom, null);
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

            // Never re-mark a row that's already green — see class doc:
            // "the main green pick stays the overall recommendation; the
            // other class's best gets a lighter/secondary marker".
            if (picks.BestLargeRowIndex is { } largeIdx &&
                largeIdx != recommendation.RecommendedRowIndex &&
                RowBounds.Compute(bodyRows[largeIdx]) is { } largeRect)
            {
                bestLargeMarker = new RowMarker(largeIdx, largeRect.Left, largeRect.Top, largeRect.Right, largeRect.Bottom, "best large pkg");
            }

            if (picks.BestSmallRowIndex is { } smallIdx &&
                smallIdx != recommendation.RecommendedRowIndex &&
                RowBounds.Compute(bodyRows[smallIdx]) is { } smallRect)
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

        return new CatalogAnnotations(greenHighlight, yellowHighlight, bestLargeMarker, bestSmallMarker, sortBadge);
    }
}
