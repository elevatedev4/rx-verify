using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.OrderAssist.Geometry;

/// <summary>
/// The full horizontal+vertical extent of ONE table row's own words —
/// used for the "highlight the whole row" case (the best-large/best-small
/// package markers on the Catalog Item Substitution window), as opposed to
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

    /// <summary>
    /// ROUND 3 (Will verbatim, Catalog Substitution: "Make the height match
    /// the height of the row too (which should be the same for all rows),
    /// so it's easier to read"). Plain Compute's height comes purely from
    /// whatever words THAT row happens to have — a row whose OCR only
    /// caught a short cell value (or missed a cell entirely) ends up with a
    /// visibly SHORTER box than a row where every cell read cleanly, even
    /// though every row is the exact same pixel height in the actual grid.
    /// This keeps the row's own actual horizontal extent (Left/Right) and
    /// vertical CENTER (from Compute's raw bounds) but forces the height to
    /// <paramref name="canonicalHeight"/> (see RowPitchEstimator for how
    /// that's derived from the table's own row-to-row spacing) — every row
    /// highlighted this way is then visually identical in height, matching
    /// the real, uniform grid row height. Falls back to the raw Compute
    /// result when canonicalHeight isn't usable (&lt;= 0 — e.g. only one row
    /// on screen, nothing to measure a pitch from) rather than fabricating
    /// a height from nothing.
    /// </summary>
    public static RowRect? ComputeUniform(IReadOnlyList<OcrWord> rowWords, double canonicalHeight)
    {
        var raw = Compute(rowWords);
        if (raw is null || canonicalHeight <= 0) return raw;

        var centerY = (raw.Value.Top + raw.Value.Bottom) / 2.0;
        var halfHeight = canonicalHeight / 2.0;
        return new RowRect(raw.Value.Left, centerY - halfHeight, raw.Value.Right, centerY + halfHeight);
    }
}
