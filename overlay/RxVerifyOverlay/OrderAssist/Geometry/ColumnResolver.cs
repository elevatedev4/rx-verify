using System;
using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.OrderAssist.Geometry;

/// <summary>
/// Reconstructs column boundaries from a table's header-row words (see
/// HeaderBandLocator for how many leading rows count as "header"), then
/// resolves a specific column by its EXACT full label text.
///
/// THE SUBSTRING TRAP (the owner's explicit spec hazard): the Create
/// Recommended Orders window has an "Order Quantity" column immediately
/// next to a "Suggested Order Qty" column — "Order Quantity" reads as a
/// literal substring/prefix-sharing neighbor of the other, and the
/// Catalog Item Substitution window has the same shape twice over
/// ("Rebate Cost" vs "Rebate Cost Per Unit", "Cost Per Unit" vs "Rebate
/// Cost Per Unit"). <see cref="ResolveExact"/> matches the FULL,
/// whitespace-normalized column label with case-insensitive EQUALITY,
/// never Contains/StartsWith/EndsWith — "order quantity" != "suggested
/// order qty" as strings, full stop, regardless of how the clustering
/// below groups the underlying OCR words. This is the actual safety net,
/// not the clustering: see the FAIL-SAFE note below.
///
/// CLUSTERING (JUDGMENT CALL, unverified against a real OCR capture —
/// flagged in the branch report): header words are clustered into
/// columns purely by horizontal (X-axis) proximity, IGNORING which
/// header row/line each word came from — this is what correctly keeps a
/// 2-line-wrapped header (e.g. "Suggested" over "Order Qty") in ONE
/// column while still separating it from an adjacent column, since a
/// wrapped cell's lines share the same horizontal extent by definition,
/// while two different columns' header words never do. The gap threshold
/// that decides "same column" vs "different column" is expressed as a
/// multiple of the header band's own median word HEIGHT (a DPI/zoom-
/// independent proxy for font size — ordinary inter-word spacing within
/// one label scales with font size much the same way column padding
/// roughly does) rather than a fixed pixel count, but the exact
/// multiplier (<see cref="ColumnGapMultiplier"/>) has not been tuned
/// against real OCR output from either target window.
///
/// FAIL-SAFE PROPERTY: if this clustering ever over- or under-merges
/// (e.g. two adjacent columns' header words get lumped into one
/// cluster), the resulting concatenated label simply won't exactly match
/// any of this module's target label constants, so ResolveExact returns
/// null and the caller draws NO highlight that tick — never a highlight
/// on the WRONG column. A clustering mistake degrades to "nothing
/// happens this tick", not "something wrong is highlighted". This is the
/// property that makes the substring-trap requirement provably satisfied
/// regardless of how well-tuned the clustering constant turns out to be
/// on Will's real screen.
/// </summary>
public static class ColumnResolver
{
    private const double ColumnGapMultiplier = 1.5;

    /// <summary>
    /// Builds every column band from the given header rows (already
    /// sliced out via HeaderBandLocator.CountHeaderRows), with PARTITION
    /// edges filled in at the midpoint to each neighbor (outer edges are
    /// extended outward by the band's own width — a bounded stand-in for
    /// "the rest of the table" rather than true infinity).
    /// </summary>
    public static IReadOnlyList<ColumnBand> BuildPartitionedColumnBands(IReadOnlyList<IReadOnlyList<OcrWord>> headerRows)
    {
        var allWords = headerRows
            .SelectMany(row => row)
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .OrderBy(w => w.X)
            .ToList();

        if (allWords.Count == 0) return Array.Empty<ColumnBand>();

        var gapThreshold = Median(allWords.Select(w => w.H).ToList()) * ColumnGapMultiplier;

        var clusters = new List<List<OcrWord>>();
        foreach (var word in allWords)
        {
            if (clusters.Count > 0)
            {
                var last = clusters[^1];
                var clusterRight = last.Max(w => w.X + w.W);
                if (word.X - clusterRight < gapThreshold)
                {
                    last.Add(word);
                    continue;
                }
            }

            clusters.Add(new List<OcrWord> { word });
        }

        var rawBands = clusters
            .Select(cluster => (
                Left: cluster.Min(w => w.X),
                Right: cluster.Max(w => w.X + w.W),
                // Reading order: top-to-bottom then left-to-right, so a
                // wrapped 2-line header reconstructs as "Suggested Order
                // Qty", not a scrambled word order.
                Label: string.Join(" ", cluster.OrderBy(w => w.Y).ThenBy(w => w.X).Select(w => w.Text.Trim()))))
            .OrderBy(b => b.Left)
            .ToList();

        var bands = new List<ColumnBand>();
        for (var i = 0; i < rawBands.Count; i++)
        {
            var (left, right, label) = rawBands[i];
            var width = right - left;

            var partitionLeft = i > 0
                ? (left + rawBands[i - 1].Right) / 2.0
                : left - width;

            var partitionRight = i < rawBands.Count - 1
                ? (right + rawBands[i + 1].Left) / 2.0
                : right + width;

            bands.Add(new ColumnBand(label, left, right, partitionLeft, partitionRight));
        }

        return bands;
    }

    /// <summary>EXACT (not Contains) match on the whitespace-normalized, case-insensitive label — see class doc "THE SUBSTRING TRAP". Null if no band's label equals <paramref name="exactLabel"/>.</summary>
    public static ColumnBand? ResolveExact(IReadOnlyList<ColumnBand> bands, string exactLabel)
    {
        var target = NormalizeLabel(exactLabel);
        return bands.FirstOrDefault(b => NormalizeLabel(b.Label) == target);
    }

    private static string NormalizeLabel(string label) =>
        string.Join(" ", label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 1.0;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }
}
