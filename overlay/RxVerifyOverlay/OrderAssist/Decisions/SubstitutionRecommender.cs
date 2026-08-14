using System;
using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.OrderAssist.Parsing;

namespace RxVerifyOverlay.OrderAssist.Decisions;

/// <summary>Whether SubstitutionRecommender.Evaluate found a secondary item worth ordering instead of McKesson — see SubstitutionResult.</summary>
public enum SubstitutionRecommendation
{
    None,
    RecommendSecondary
}

/// <summary>
/// The owner's McKesson-vs-cheaper-secondary rule (spec verbatim): find
/// the cheapest McKesson item by Rebate Cost Per Unit and the cheapest
/// secondary item by the same column; if the secondary represents a
/// savings of 25% or more, recommend it (green highlight + % savings
/// label) instead of the McKesson default.
/// </summary>
public static class SubstitutionRecommender
{
    public const decimal DefaultThresholdPercent = 25m;

    /// <summary>One catalog row's Supplier + Rebate Cost Per Unit text, already extracted by Scanning/CatalogSubstitutionScanner via ColumnResolver/CellValueBucketizer — this class never touches OCR geometry itself.</summary>
    public sealed record CatalogRowInput(int RowIndex, string Supplier, string RebateCostPerUnitText);

    public sealed record SubstitutionResult(SubstitutionRecommendation Recommendation, int? RecommendedRowIndex, decimal? SavingsPercent, string? SavingsDisplay)
    {
        public static readonly SubstitutionResult NoRecommendation = new(SubstitutionRecommendation.None, null, null, null);
    }

    /// <summary>
    /// EDGE CASES (all deliberate judgment calls — see the branch report):
    ///   - A row with unparseable/blank cost text is EXCLUDED from both
    ///     the McKesson and secondary candidate pools entirely — never
    ///     treated as free/zero, never crashes.
    ///   - No secondary rows with a readable cost at all -&gt; NoRecommendation
    ///     (nothing cheaper exists to ever suggest).
    ///   - No McKesson rows with a readable cost at all -&gt; still
    ///     RECOMMENDS the cheapest secondary (better than recommending
    ///     nothing when there's genuinely no primary-wholesaler option on
    ///     this list), but SavingsPercent is null and SavingsDisplay reads
    ///     "n/a (no McKesson option)" rather than fabricating a percentage
    ///     with no baseline to compute it against.
    ///   - Cheapest McKesson cost is exactly 0 -&gt; NoRecommendation (a
    ///     savings percentage against a zero baseline is undefined/
    ///     divide-by-zero, and a real $0 rebate cost is far more likely
    ///     OCR noise than an actual free item).
    ///   - The 25% threshold is INCLUSIVE ("savings of 25% or more" — the
    ///     owner's own wording): exactly 25.0% recommends.
    ///   - Ties among cheapest candidates (secondary or McKesson) resolve
    ///     to the lowest RowIndex — i.e. whichever appears first in the
    ///     catalog grid's own top-to-bottom order — a stable, deterministic
    ///     choice with no other signal available to break it.
    /// </summary>
    public static SubstitutionResult Evaluate(IReadOnlyList<CatalogRowInput> rows, decimal thresholdPercent = DefaultThresholdPercent)
    {
        var parsed = rows
            .Select(r => new
            {
                r.RowIndex,
                IsMcKesson = SupplierClassifier.IsMcKesson(r.Supplier),
                Cost = CurrencyParser.Parse(r.RebateCostPerUnitText)
            })
            .ToList();

        var secondaryCandidates = parsed.Where(p => !p.IsMcKesson && p.Cost is not null).ToList();
        if (secondaryCandidates.Count == 0) return SubstitutionResult.NoRecommendation;

        var cheapestSecondary = secondaryCandidates.OrderBy(p => p.Cost!.Value).ThenBy(p => p.RowIndex).First();

        var mckessonCandidates = parsed.Where(p => p.IsMcKesson && p.Cost is not null).ToList();
        if (mckessonCandidates.Count == 0)
        {
            return new SubstitutionResult(
                SubstitutionRecommendation.RecommendSecondary,
                cheapestSecondary.RowIndex,
                SavingsPercent: null,
                SavingsDisplay: "n/a (no McKesson option)");
        }

        var cheapestMcKesson = mckessonCandidates.OrderBy(p => p.Cost!.Value).ThenBy(p => p.RowIndex).First();
        if (cheapestMcKesson.Cost!.Value <= 0m) return SubstitutionResult.NoRecommendation;

        var savingsPercent = (cheapestMcKesson.Cost.Value - cheapestSecondary.Cost!.Value) / cheapestMcKesson.Cost.Value * 100m;

        if (savingsPercent >= thresholdPercent)
        {
            return new SubstitutionResult(
                SubstitutionRecommendation.RecommendSecondary,
                cheapestSecondary.RowIndex,
                savingsPercent,
                FormatSavings(savingsPercent));
        }

        return SubstitutionResult.NoRecommendation;
    }

    /// <summary>
    /// "0.#" (not a bare interpolation of the decimal) deliberately
    /// controls BOTH the rounding (to at most 1 decimal place) AND the
    /// display, independent of decimal's own internal scale bookkeeping —
    /// a plain "{savingsPercent}" interpolation would print however many
    /// trailing zeros the arithmetic that produced it happened to carry
    /// (e.g. "25.00" instead of "25"), which is an implementation detail
    /// no caller should have to care about.
    /// </summary>
    private static string FormatSavings(decimal savingsPercent) => $"{savingsPercent:0.#}% savings";
}
