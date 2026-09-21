using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// Round 3 fix (owner: "Each of those reports has to be double clicked on
/// to get them to open the next window" — his screenshot shows ~36 rows,
/// several of which share a long common prefix: "Inventory Valuation" vs
/// "Inventory Valuation (With Last Supplier)" vs "Inventory Valuation for
/// Excel"). Every row-matching decision in PioneerReportDriver's new
/// layered row-selection step (UIA deep search, grid keyboard, OCR) goes
/// through THIS single exact-match rule, so "Inventory Valuation" can
/// never accidentally select "Inventory Valuation (With Last Supplier)" —
/// unlike ribbon navigation's FindDescendantByNamePrefix (a deliberately
/// looser prefix match, fine there because ribbon tab/button names don't
/// collide the way report row names do).
/// </summary>
public static class ReportRowTextMatcher
{
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Trims and collapses internal whitespace runs to a single space — OCR line reconstruction (word-by-word Join) and UIA Name/Value text can both introduce irregular spacing that a plain string comparison would wrongly treat as a mismatch.</summary>
    public static string Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : WhitespaceRun.Replace(text.Trim(), " ");

    /// <summary>True only for a WHOLE-STRING match (case-insensitive, whitespace-normalized) — never a prefix/substring/contains match. This is the entire fix for the "Inventory Valuation" vs "Inventory Valuation (With Last Supplier)" collision the GOAL brief calls out by name.</summary>
    public static bool IsExactMatch(string? candidateText, string? targetText)
    {
        var normalizedTarget = Normalize(targetText);
        if (normalizedTarget.Length == 0) return false;

        var normalizedCandidate = Normalize(candidateText);
        if (normalizedCandidate.Length == 0) return false;

        return string.Equals(normalizedCandidate, normalizedTarget, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// GOAL brief step 2's diagnostic-dump extension: "walk the RAW view
/// under FinancialReportsWorkArea to depth 8 (names redacted unless they
/// equal a catalog report name or a known column header)". DIFFERENT
/// allowlist rule from UiaDiagnostics.cs's UiaNameRedaction (control-type
/// based) — this dump walks INSIDE the report-picker grid itself, where a
/// value-bearing DataItem/ListItem cell's Name could legitimately BE one
/// of ReportCatalog's own row names (safe — Will's own fixed, non-PHI
/// list) or a grid column header ("Report Name" / "Report Description"),
/// so those two known-safe categories are logged verbatim; everything
/// else (including some OTHER grid's patient-bearing text, if the wrong
/// pane were ever walked by mistake) is redacted, same "unknown -&gt;
/// redact" default as UiaNameRedaction.
/// </summary>
public static class ReportRowNameRedaction
{
    private static readonly string[] KnownColumnHeaders = { "Report Name", "Report Description" };

    public static bool IsKnownSafeValue(string? name)
    {
        var normalized = ReportRowTextMatcher.Normalize(name);
        if (normalized.Length == 0) return false;

        foreach (var header in KnownColumnHeaders)
        {
            if (string.Equals(normalized, ReportRowTextMatcher.Normalize(header), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var entry in ReportCatalog.All)
        {
            if (string.Equals(normalized, ReportRowTextMatcher.Normalize(entry.PioneerRowText), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (entry.PioneerRowTextAlias is { } alias
                && string.Equals(normalized, ReportRowTextMatcher.Normalize(alias), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string RedactIfNeeded(string? name) =>
        IsKnownSafeValue(name) ? name! : UiaNameRedaction.RedactedName;
}

/// <summary>
/// Pure sequencing policy behind PioneerReportDriver's row-selection step
/// — try each candidate row text (ReportCatalogEntry.PioneerRowText, then
/// PioneerRowTextAlias if present — see ReportCatalog's round-3 fix) in
/// order, and for each row text try the three layered strategies (UIA
/// deep search, grid keyboard, OCR) in order, stopping at the first
/// strategy that reports success. No FlaUI/UIA dependency at all —
/// PioneerReportDriver supplies each strategy as a bool-returning
/// delegate that performs the real automation AND its own "did the
/// target screen actually confirm" check — so this class is unit
/// testable with simple fake delegates, standing in for the real
/// UIA/keyboard/OCR driver calls (see RxVerifyOverlay.Tests/Reports/
/// ReportRowSelectionSequencerTests.cs).
/// </summary>
public static class ReportRowSelectionSequencer
{
    /// <param name="rowTextCandidates">In try order — normally [PioneerRowText, PioneerRowTextAlias?].</param>
    /// <param name="strategiesInOrder">In try order — normally [UIA deep search, grid keyboard, OCR].</param>
    /// <param name="onStrategyAttempt">Optional (rowText, oneBasedStrategyNumber) callback fired immediately before each attempt, purely for logging.</param>
    public static bool TrySelect(
        IReadOnlyList<string> rowTextCandidates,
        IReadOnlyList<Func<string, bool>> strategiesInOrder,
        Action<string, int>? onStrategyAttempt = null)
    {
        foreach (var rowText in rowTextCandidates)
        {
            if (string.IsNullOrWhiteSpace(rowText)) continue;

            for (var i = 0; i < strategiesInOrder.Count; i++)
            {
                onStrategyAttempt?.Invoke(rowText, i + 1);
                if (strategiesInOrder[i](rowText)) return true;
            }
        }

        return false;
    }

    /// <summary>ReportCatalogEntry -&gt; the ordered row-text candidate list (PioneerRowText always first; PioneerRowTextAlias appended only when set) — the one place that ordering rule lives, shared by PioneerReportDriver and this class's own tests.</summary>
    public static IReadOnlyList<string> BuildRowTextCandidates(ReportCatalogEntry entry)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.PioneerRowText)) candidates.Add(entry.PioneerRowText);
        if (!string.IsNullOrWhiteSpace(entry.PioneerRowTextAlias)) candidates.Add(entry.PioneerRowTextAlias!);
        return candidates;
    }
}
