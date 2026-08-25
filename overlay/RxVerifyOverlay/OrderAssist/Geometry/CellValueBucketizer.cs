using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.OrderAssist.Geometry;

/// <summary>
/// Reads a single resolved column's value out of every body (data) row —
/// the last step of turning raw OCR words into a usable table cell. A
/// word belongs to a row's cell for a given column if its horizontal
/// CENTER (not just any overlap) falls inside that column's PARTITION
/// (ColumnBand.PartitionLeft/PartitionRight, not just its raw header-text
/// extent — see ColumnBand's own doc for why the wider partition matters
/// for right-aligned numeric values).
/// </summary>
public static class CellValueBucketizer
{
    /// <summary>One CellValue per body row, in the SAME order/count as <paramref name="bodyRows"/> (RowIndex is that row's index into bodyRows) — a row with no matching words gets Text="" and Bounds=null rather than being omitted, so callers can always index by row position.</summary>
    public static IReadOnlyList<CellValue> BucketColumn(IReadOnlyList<IReadOnlyList<OcrWord>> bodyRows, ColumnBand band)
    {
        var results = new List<CellValue>(bodyRows.Count);

        for (var i = 0; i < bodyRows.Count; i++)
        {
            var matched = bodyRows[i]
                .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .Where(w => IsCenterWithinPartition(w, band))
                .OrderBy(w => w.X)
                .ToList();

            if (matched.Count == 0)
            {
                results.Add(new CellValue(i, "", null));
                continue;
            }

            var text = string.Join(" ", matched.Select(w => w.Text.Trim()));
            var bounds = new RowRect(
                matched.Min(w => w.X),
                matched.Min(w => w.Y),
                matched.Max(w => w.X + w.W),
                matched.Max(w => w.Y + w.H));

            results.Add(new CellValue(i, text, bounds));
        }

        return results;
    }

    /// <summary>
    /// True if <paramref name="word"/>'s horizontal CENTER falls inside
    /// <paramref name="band"/>'s partition -- the same column-membership
    /// test <see cref="BucketColumn"/> uses per word. Exposed (ROUND 5,
    /// was private) so CreateRecommendedOrdersScanner's blank-cell
    /// candidate-zero path (see that class's own doc) can ask "does this
    /// row have content OUTSIDE the Order Quantity column" using the exact
    /// same partition test, rather than re-deriving a slightly different
    /// one.
    /// </summary>
    public static bool IsCenterWithinPartition(OcrWord word, ColumnBand band)
    {
        var center = word.X + word.W / 2.0;
        return center >= band.PartitionLeft && center < band.PartitionRight;
    }
}
