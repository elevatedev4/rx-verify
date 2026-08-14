using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.OrderAssist.Parsing;

namespace RxVerifyOverlay.OrderAssist.Decisions;

/// <summary>Result of SortOrderChecker.Classify — see that method's doc for exactly when each applies.</summary>
public enum SortIndicatorState
{
    /// <summary>Fewer than 2 rows had a readable Rebate Cost Per Unit value -- not enough signal to call it either way (the owner's spec verbatim: "show nothing rather than guess").</summary>
    Unknown,

    /// <summary>Every readable value is non-decreasing top-to-bottom.</summary>
    Sorted,

    /// <summary>At least one readable value is smaller than the readable value above it.</summary>
    NotSorted
}

/// <summary>
/// Will's round-2 "sort indicator" rule: a small badge above the Rebate
/// Cost Per Unit column showing whether the catalog grid is currently
/// sorted ascending by that column.
/// </summary>
public static class SortOrderChecker
{
    private const string SortedGlyph = "✓"; // check mark
    private const string NotSortedGlyph = "⚠"; // warning sign

    /// <summary>
    /// <paramref name="rebateCostTextsInRowOrder"/> is every body row's raw
    /// Rebate Cost Per Unit OCR text, TOP TO BOTTOM, in the table's own
    /// order -- rows whose text fails to parse are dropped entirely before
    /// comparing (JUDGMENT CALL, spec-mandated: "rows whose value failed
    /// to parse are ignored for the check", never treated as a break in
    /// the sequence or coerced to zero). Comparison is done on the
    /// remaining values in their ORIGINAL relative order, so a dropped row
    /// in the middle of an otherwise-sorted table never itself causes a
    /// false NotSorted.
    /// </summary>
    public static SortIndicatorState Classify(IReadOnlyList<string?> rebateCostTextsInRowOrder)
    {
        var parsed = rebateCostTextsInRowOrder
            .Select(CurrencyParser.Parse)
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .ToList();

        if (parsed.Count < 2) return SortIndicatorState.Unknown;

        for (var i = 1; i < parsed.Count; i++)
        {
            if (parsed[i] < parsed[i - 1]) return SortIndicatorState.NotSorted;
        }

        return SortIndicatorState.Sorted;
    }

    /// <summary>Null for Unknown -- callers must draw NO badge rather than guess, per the owner's spec (see Classify's doc).</summary>
    public static string? Describe(SortIndicatorState state) => state switch
    {
        SortIndicatorState.Sorted => $"{SortedGlyph} sorted by rebate",
        SortIndicatorState.NotSorted => $"{NotSortedGlyph} not sorted by rebate",
        _ => null
    };
}
