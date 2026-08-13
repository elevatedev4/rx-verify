using System;
using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Decisions;
using RxVerifyOverlay.OrderAssist.Geometry;

namespace RxVerifyOverlay.OrderAssist.Scanning;

/// <summary>
/// End-to-end PURE pipeline for the "Recommended Order - Catalog Item
/// Substitution Selection" window: raw OCR words in, at most one
/// recommended-row highlight out. Resolves BOTH the Supplier column and
/// the Rebate Cost Per Unit column EXACTLY (never "Rebate Cost" or "Cost
/// Per Unit" — the same substring-trap shape exists on this window too,
/// see ColumnResolver's doc), reads every body row's two cells, then
/// hands them to SubstitutionRecommender for the McKesson-vs-secondary
/// decision. See CreateRecommendedOrdersScanner's doc for why this stays
/// a pure, OrderAssistCoordinator-only-caller pipeline.
/// </summary>
public static class CatalogSubstitutionScanner
{
    public const string SupplierHeaderLabel = "Supplier";
    public const string RebateCostPerUnitHeaderLabel = "Rebate Cost Per Unit";

    /// <summary>The one row to highlight green plus its savings label — Left/Top/Right/Bottom are the FULL ROW's extent (not just the cost cell), in the same coordinate space as the input OcrWords.</summary>
    public sealed record SubstitutionHighlight(int RowIndex, double Left, double Top, double Right, double Bottom, string SavingsDisplay);

    /// <summary>Null if either target column can't be resolved, or SubstitutionRecommender found no recommendation worth making this tick.</summary>
    public static SubstitutionHighlight? FindRecommendation(IReadOnlyList<OcrWord> words)
    {
        var rows = TableRowGrouper.GroupIntoRows(words);
        var headerRowCount = HeaderBandLocator.CountHeaderRows(rows);
        if (headerRowCount == 0 || headerRowCount >= rows.Count) return null;

        var headerRows = rows.Take(headerRowCount).ToList();
        var bodyRows = rows.Skip(headerRowCount).ToList();

        var bands = ColumnResolver.BuildPartitionedColumnBands(headerRows);
        var supplierColumn = ColumnResolver.ResolveExact(bands, SupplierHeaderLabel);
        var costColumn = ColumnResolver.ResolveExact(bands, RebateCostPerUnitHeaderLabel);
        if (supplierColumn is null || costColumn is null) return null;

        var supplierCells = CellValueBucketizer.BucketColumn(bodyRows, supplierColumn);
        var costCells = CellValueBucketizer.BucketColumn(bodyRows, costColumn);

        var rowInputs = new List<SubstitutionRecommender.CatalogRowInput>(bodyRows.Count);
        for (var i = 0; i < bodyRows.Count; i++)
        {
            var supplierText = supplierCells.FirstOrDefault(c => c.RowIndex == i)?.Text ?? "";
            var costText = costCells.FirstOrDefault(c => c.RowIndex == i)?.Text ?? "";
            rowInputs.Add(new SubstitutionRecommender.CatalogRowInput(i, supplierText, costText));
        }

        var result = SubstitutionRecommender.Evaluate(rowInputs);
        if (result.Recommendation != SubstitutionRecommendation.RecommendSecondary || result.RecommendedRowIndex is not { } rowIndex)
        {
            return null;
        }

        if (RowBounds.Compute(bodyRows[rowIndex]) is not { } rowRect) return null;

        return new SubstitutionHighlight(rowIndex, rowRect.Left, rowRect.Top, rowRect.Right, rowRect.Bottom, result.SavingsDisplay ?? "");
    }
}
