using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.OrderAssist.Parsing;

namespace RxVerifyOverlay.OrderAssist.Decisions;

/// <summary>Whether one row's savings badge (see <see cref="RowSavings"/>) clears the owner's savings threshold — governs the badge's color (green vs. yellow), never whether the badge shows at all.</summary>
public enum SavingsTier
{
    /// <summary>Savings percent &gt;= the threshold — green badge.</summary>
    AboveThreshold,

    /// <summary>Cheaper than McKesson, but by less than the threshold — still shown (round 3 design change, see class doc), yellow badge.</summary>
    BelowThreshold
}

/// <summary>
/// ROUND 3 REDESIGN (Will verbatim, two combined asks):
///   1. "Make sure if it is less than our savings threshold, it should
///      still show the analysis and not use green, but yellow and still
///      show the %."
///   2. "Always Calculate the savings for each item cheaper than mckesson
///      and display it at the end of the row. Below our threshold, show in
///      yellow, above show green. Don't highlight the whole row, just show
///      it at the end."
///
/// This REPLACES round 1/2's "pick ONE cheapest secondary, recommend it
/// only if it clears 25%, highlight its whole row green" model
/// (SubstitutionRecommendation/SubstitutionResult/Evaluate) with a per-row
/// list: every non-McKesson row that is cheaper than the cheapest McKesson
/// row on screen gets its own savings badge, tiered by the threshold — not
/// just the single cheapest one, and never gated on clearing the threshold
/// to be shown at all. CatalogSubstitutionScanner turns each entry into a
/// small end-of-row badge (see RowMarker/RowBounds) instead of a full-row
/// fill — see OrderAssistOverlayWindow.AddSavingsLabel.
///
/// EDGE CASES (deliberate, same fail-closed posture as round 1):
///   - A row with unparseable/blank cost text is EXCLUDED entirely — never
///     treated as free/zero, never crashes.
///   - No McKesson row with a readable, positive cost at all -&gt; no badges
///     at all (nothing to compare against — round 3 drops round 1's "no
///     McKesson option -&gt; recommend cheapest secondary with n/a savings"
///     special case, since "cheaper than McKesson" is now the literal,
///     per-row gate and that requires an actual McKesson baseline to mean
///     anything).
///   - Cheapest McKesson cost is exactly 0 -&gt; no badges (a savings
///     percentage against a zero baseline is undefined/divide-by-zero, and
///     a real $0 rebate cost is far more likely OCR noise than an actual
///     free item).
///   - A non-McKesson row whose cost is NOT strictly cheaper than the
///     cheapest McKesson cost gets no badge at all (nothing to show —
///     McKesson is already as good or better).
///   - The threshold boundary is INCLUSIVE ("savings of 25% or more" — the
///     owner's own wording, unchanged from round 1): exactly 25.0% is
///     AboveThreshold (green).
///   - Results are returned in RowIndex order (top-to-bottom, matching the
///     grid's own reading order) — no other ordering is meaningful once
///     every qualifying row gets its own badge.
/// </summary>
public static class SubstitutionRecommender
{
    public const decimal DefaultThresholdPercent = 25m;

    /// <summary>One catalog row's Supplier + Rebate Cost Per Unit text, already extracted by Scanning/CatalogSubstitutionScanner via ColumnResolver/CellValueBucketizer — this class never touches OCR geometry itself.</summary>
    public sealed record CatalogRowInput(int RowIndex, string Supplier, string RebateCostPerUnitText);

    /// <summary>One row's savings badge — see class doc. SavingsPercent is always &gt; 0 (a badge only ever exists for a row genuinely cheaper than McKesson's cheapest cost).</summary>
    public sealed record RowSavings(int RowIndex, decimal SavingsPercent, string SavingsDisplay, SavingsTier Tier);

    /// <summary>Empty (never null) if there's no valid McKesson baseline to compare against, or no row is actually cheaper than it — see class doc's edge cases.</summary>
    public static IReadOnlyList<RowSavings> EvaluateSavings(IReadOnlyList<CatalogRowInput> rows, decimal thresholdPercent = DefaultThresholdPercent)
    {
        var parsed = ParseRows(rows);
        var baseline = FindCheapestMcKessonBaseline(parsed);
        if (baseline is null) return System.Array.Empty<RowSavings>();

        var results = new List<RowSavings>();
        foreach (var p in parsed.Where(p => !p.IsMcKesson && p.Cost is not null))
        {
            if (p.Cost!.Value >= baseline.Value.Cost) continue; // not cheaper than McKesson -> no badge

            var savingsPercent = (baseline.Value.Cost - p.Cost.Value) / baseline.Value.Cost * 100m;
            var tier = savingsPercent >= thresholdPercent ? SavingsTier.AboveThreshold : SavingsTier.BelowThreshold;
            results.Add(new RowSavings(p.RowIndex, savingsPercent, FormatSavings(savingsPercent), tier));
        }

        return results.OrderBy(r => r.RowIndex).ToList();
    }

    /// <summary>
    /// ROUND 4 (Will: "highlight the cheapest mckesson item that is being
    /// compared in some intuitive color") — exposes WHICH row
    /// EvaluateSavings actually used as its McKesson baseline, so
    /// CatalogSubstitutionScanner can draw a marker on that specific row.
    /// Shares ParseRows/FindCheapestMcKessonBaseline with EvaluateSavings
    /// itself (same cheapest-valid-McKesson-row selection, same $0-baseline
    /// exclusion) so the two can never disagree about which row is "the"
    /// baseline. Null under the exact same conditions EvaluateSavings
    /// returns empty for baseline reasons (no McKesson row with a
    /// readable, positive cost) — see class doc's edge cases.
    /// </summary>
    public static int? FindCheapestMcKessonRowIndex(IReadOnlyList<CatalogRowInput> rows) =>
        FindCheapestMcKessonBaseline(ParseRows(rows))?.RowIndex;

    private sealed record ParsedRow(int RowIndex, bool IsMcKesson, decimal? Cost);

    private static List<ParsedRow> ParseRows(IReadOnlyList<CatalogRowInput> rows) =>
        rows.Select(r => new ParsedRow(r.RowIndex, SupplierClassifier.IsMcKesson(r.Supplier), CurrencyParser.Parse(r.RebateCostPerUnitText))).ToList();

    /// <summary>The cheapest McKesson row with a readable, POSITIVE cost — null if no McKesson row parses at all, or the cheapest one parses to exactly $0 (see class doc's zero-cost-baseline edge case: a real free item is far less likely than OCR noise, and a savings percentage against a $0 baseline is undefined anyway).</summary>
    private static (int RowIndex, decimal Cost)? FindCheapestMcKessonBaseline(IReadOnlyList<ParsedRow> parsed)
    {
        var mckessonCandidates = parsed.Where(p => p.IsMcKesson && p.Cost is not null).ToList();
        if (mckessonCandidates.Count == 0) return null;

        var cheapest = mckessonCandidates.OrderBy(p => p.Cost!.Value).ThenBy(p => p.RowIndex).First();
        return cheapest.Cost!.Value > 0m ? (cheapest.RowIndex, cheapest.Cost.Value) : null;
    }

    /// <summary>
    /// "0.#" (not a bare interpolation of the decimal) deliberately
    /// controls BOTH the rounding (to at most 1 decimal place) AND the
    /// display, independent of decimal's own internal scale bookkeeping —
    /// a plain "{savingsPercent}" interpolation would print however many
    /// trailing zeros the arithmetic that produced it happened to carry
    /// (e.g. "25.00" instead of "25"), which is an implementation detail
    /// no caller should have to care about.
    ///
    /// ROUND 4 (Will verbatim: "Just make the % indicator show the % ...
    /// Leave off the word savings from there") — dropped the trailing
    /// " savings" this round 3 originally appended; the badge is now just
    /// the number and a percent sign, nothing else.
    /// </summary>
    private static string FormatSavings(decimal savingsPercent) => $"{savingsPercent:0.#}%";
}
