using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.OrderAssist.Parsing;

namespace RxVerifyOverlay.OrderAssist.Decisions;

/// <summary>
/// Will's round-2 "dual visibility" rule verbatim: "Highlight the cheapest
/// products as green and if McKesson is not the cheapest and you're
/// showing a cheaper secondary in green, highlight McKesson as yellow so
/// we can see both." Green itself is unchanged from round 1 (still
/// SubstitutionRecommender.Evaluate's own McKesson-vs-25%-savings rule —
/// this class doesn't touch that decision, only adds the yellow contrast
/// row on top of whatever green already decided).
/// </summary>
public static class DualHighlightPlanner
{
    /// <summary>
    /// Null whenever a yellow highlight isn't warranted this tick. Three
    /// distinct fail-closed/spec-mandated cases collapse to the same null
    /// result here:
    ///   - No green recommendation at all this tick (nothing to contrast
    ///     a yellow row against).
    ///   - The green pick IS McKesson (defensive: under today's
    ///     SubstitutionRecommender rule the recommended row is always a
    ///     non-McKesson secondary, so this can't currently happen, but the
    ///     owner's spec is explicit -- "Yellow must never appear when
    ///     McKesson IS the green pick" -- so this checks the invariant
    ///     directly rather than assuming it holds).
    ///   - No McKesson row on this screen has a readable Rebate Cost Per
    ///     Unit at all -- nothing to reliably point to (same "unparseable
    ///     -&gt; no highlight, never a wrong one" posture as every other
    ///     Decisions class in this module). An unknown/blank supplier cell
    ///     is never McKesson (see SupplierClassifier.IsMcKesson), so it can
    ///     never be picked here either.
    /// Ties among McKesson candidates resolve to the lowest RowIndex, same
    /// tiebreak as SubstitutionRecommender/PackageClassifier. Always at
    /// most one row -- a single First() pick, never a list.
    /// </summary>
    public static int? FindMcKessonHighlightRowIndex(
        IReadOnlyList<SubstitutionRecommender.CatalogRowInput> rows,
        SubstitutionRecommender.SubstitutionResult greenResult)
    {
        if (greenResult.Recommendation != SubstitutionRecommendation.RecommendSecondary ||
            greenResult.RecommendedRowIndex is not { } greenRowIndex)
        {
            return null;
        }

        var greenRow = rows.FirstOrDefault(r => r.RowIndex == greenRowIndex);
        if (greenRow is not null && SupplierClassifier.IsMcKesson(greenRow.Supplier))
        {
            return null;
        }

        var mckessonCandidates = rows
            .Where(r => SupplierClassifier.IsMcKesson(r.Supplier))
            .Select(r => new { r.RowIndex, Cost = CurrencyParser.Parse(r.RebateCostPerUnitText) })
            .Where(r => r.Cost is not null)
            .ToList();

        if (mckessonCandidates.Count == 0) return null;

        return mckessonCandidates.OrderBy(r => r.Cost!.Value).ThenBy(r => r.RowIndex).First().RowIndex;
    }
}
