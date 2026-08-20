using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.OrderAssist.Scanning;

namespace RxVerifyOverlay.OrderAssist;

/// <summary>
/// Builds a HighlightStabilityPolicy comparison key from a scanner's raw
/// result — deliberately built from ROW INDICES/labels (semantically
/// stable identity: "which row(s), which decision"), not from the
/// DIP-converted pixel geometry OrderAssistCoordinator eventually draws —
/// see HighlightStabilityPolicy's own doc for why pixel comparison would
/// be too brittle. "" always means "nothing to display" — matches
/// HighlightStabilityPolicy's own empty-signature convention.
/// </summary>
public static class HighlightSignature
{
    /// <summary>Order-independent (sorted) so a tick that finds the same zero cells in a different OCR word-list iteration order still compares equal.</summary>
    public static string ForZeroQuantityHighlights(IReadOnlyList<CreateRecommendedOrdersScanner.ZeroCellHighlight> highlights) =>
        highlights.Count == 0
            ? ""
            : string.Join(",", highlights.Select(h => h.RowIndex).OrderBy(i => i));

    /// <summary>
    /// Every independently-fail-closed field of CatalogAnnotations folded
    /// into one key — see that record's own doc for why each is optional.
    /// CostColumnHeaderAnchor is DELIBERATELY excluded: it's a positioning
    /// helper for OrderAssistCoordinator's own "Processing" indicator (see
    /// that record's own doc), never a highlight, so it must never affect
    /// whether HighlightStabilityPolicy treats two ticks as "the same
    /// result" or drive a Display/Clear decision on its own.
    /// </summary>
    public static string ForCatalogAnnotations(CatalogSubstitutionScanner.CatalogAnnotations annotations)
    {
        var isEmpty = annotations.SavingsBadges.Count == 0 &&
                      annotations.BestLargePackageMarker is null &&
                      annotations.BestSmallPackageMarker is null &&
                      annotations.SortIndicatorBadge is null;
        if (isEmpty) return "";

        // Order-independent (sorted by RowIndex — already the case for
        // SubstitutionRecommender.EvaluateSavings' own output, but sorted
        // again here defensively) so a tick that finds the same badges in
        // a different iteration order still compares equal.
        var savings = annotations.SavingsBadges.Count == 0
            ? "V-"
            : string.Join("+", annotations.SavingsBadges
                .OrderBy(b => b.RowIndex)
                .Select(b => $"V{b.RowIndex}:{(b.MeetsThreshold ? "G" : "Y")}:{b.SavingsDisplay}"));
        var bestLarge = annotations.BestLargePackageMarker is { } bl ? $"L{bl.RowIndex}" : "L-";
        var bestSmall = annotations.BestSmallPackageMarker is { } bs ? $"S{bs.RowIndex}" : "S-";
        var sortBadge = annotations.SortIndicatorBadge is { } badge ? $"B{badge.Text}:{badge.IsSorted}" : "B-";

        return string.Join("|", savings, bestLarge, bestSmall, sortBadge);
    }
}
