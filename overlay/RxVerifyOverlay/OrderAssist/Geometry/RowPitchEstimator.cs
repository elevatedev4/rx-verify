using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.OrderAssist.Geometry;

/// <summary>
/// ROUND 3 (Will verbatim, Catalog Substitution: "Make the height match the
/// height of the row too (which should be the same for all rows)"). Derives
/// the table's own CANONICAL row height — the real, uniform pixel height
/// every row occupies in the actual grid — from row-to-row vertical
/// spacing, so RowBounds.ComputeUniform can build every highlight rect at
/// that same height instead of whatever height that one row's own OCR'd
/// words happened to span (which varies row to row purely from OCR noise/
/// missed cells, not anything real about the grid).
/// </summary>
public static class RowPitchEstimator
{
    /// <summary>
    /// Median distance between consecutive rows' own vertical CENTERS
    /// (median, not mean, so one wildly-misread row's Y jitter can't skew
    /// every other row's highlight height). Falls back to the single row's
    /// own raw Compute height when fewer than two rows have any bounds to
    /// measure a pitch from at all (nothing to compute a spacing from), and
    /// to 0 (meaning "no canonical height available" — RowBounds.ComputeUniform
    /// degrades to the raw per-row height in that case) when there's
    /// nothing usable at all.
    /// </summary>
    public static double EstimateCanonicalHeight(IReadOnlyList<IReadOnlyList<OcrWord>> rows)
    {
        var bounds = rows.Select(RowBounds.Compute).Where(b => b is not null).Select(b => b!.Value).ToList();
        if (bounds.Count == 0) return 0;
        if (bounds.Count == 1) return bounds[0].Bottom - bounds[0].Top;

        var centers = bounds.Select(b => (b.Top + b.Bottom) / 2.0).OrderBy(c => c).ToList();
        var gaps = new List<double>(centers.Count - 1);
        for (var i = 1; i < centers.Count; i++)
        {
            gaps.Add(centers[i] - centers[i - 1]);
        }

        gaps.Sort();
        var mid = gaps.Count / 2;
        return gaps.Count % 2 == 0 ? (gaps[mid - 1] + gaps[mid]) / 2.0 : gaps[mid];
    }
}
