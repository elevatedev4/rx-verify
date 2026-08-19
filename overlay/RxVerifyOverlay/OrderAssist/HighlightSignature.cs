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

    /// <summary>Every independently-fail-closed field of CatalogAnnotations folded into one key — see that record's own doc for why each is optional.</summary>
    public static string ForCatalogAnnotations(CatalogSubstitutionScanner.CatalogAnnotations annotations)
    {
        var isEmpty = annotations.GreenHighlight is null &&
                      annotations.YellowHighlight is null &&
                      annotations.BestLargePackageMarker is null &&
                      annotations.BestSmallPackageMarker is null &&
                      annotations.SortIndicatorBadge is null;
        if (isEmpty) return "";

        var green = annotations.GreenHighlight is { } g ? $"G{g.RowIndex}:{g.SavingsDisplay}" : "G-";
        var yellow = annotations.YellowHighlight is { } y ? $"Y{y.RowIndex}" : "Y-";
        var bestLarge = annotations.BestLargePackageMarker is { } bl ? $"L{bl.RowIndex}" : "L-";
        var bestSmall = annotations.BestSmallPackageMarker is { } bs ? $"S{bs.RowIndex}" : "S-";
        var sortBadge = annotations.SortIndicatorBadge is { } badge ? $"B{badge.Text}:{badge.IsSorted}" : "B-";

        return string.Join("|", green, yellow, bestLarge, bestSmall, sortBadge);
    }
}
