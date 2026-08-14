using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.OrderAssist.Geometry;

/// <summary>
/// The full horizontal+vertical extent of ONE table row's own words —
/// used for the "highlight the whole row" case (the green box on the
/// Catalog Item Substitution window's recommended row), as opposed to
/// CellValueBucketizer's single-column cell bounds (used for the "just
/// this cell" red box on the Create Recommended Orders window's zero
/// quantities).
/// </summary>
public static class RowBounds
{
    /// <summary>Null if the row has no non-blank words at all (nothing to box).</summary>
    public static RowRect? Compute(IReadOnlyList<OcrWord> rowWords)
    {
        var withText = rowWords.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (withText.Count == 0) return null;

        return new RowRect(
            withText.Min(w => w.X),
            withText.Min(w => w.Y),
            withText.Max(w => w.X + w.W),
            withText.Max(w => w.Y + w.H));
    }
}
