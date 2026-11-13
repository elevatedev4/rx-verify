using System;
using System.Collections.Generic;
using System.Linq;

namespace RxVerifyOverlay.Reports;

// W-T97 (Will, HQ 2026-11-12): "On the page where it is supposed to choose
// the report before entering the date range, it just scrolled down one at a
// time, probably with the down arrow."
//
// The pure half of choosing a report on Pioneer's "Run Financial Reports"
// page: which keys the type-to-search fallback may send (never Down), how a
// row is recognised, and the one log line written when the report cannot be
// found. No UIA / Win32 / WPF, so it is unit tested on any OS
// (RxVerifyOverlay.Tests/Reports/ReportChooserSelectionTests.cs).

/// <summary>The only keys the chooser's keyboard fallback is allowed to send. There is deliberately no Down arrow here.</summary>
public enum ChooserKey
{
    /// <summary>Ctrl+Home: go to the first row so type-to-search starts from the top.</summary>
    CtrlHome,

    /// <summary>The whole report name, typed so an incremental search lands on it.</summary>
    TypeName,

    /// <summary>Enter: open the row, sent only after the selected row was read back as the exact report name.</summary>
    Enter
}

public readonly record struct ChooserKeyStep(ChooserKey Key, string? Text);

/// <summary>What was read back from the chooser after typing the name.</summary>
public enum ChooserReadback
{
    /// <summary>The selected / focused row's text is exactly the requested report.</summary>
    ExactMatch,

    /// <summary>A row is selected but it is a different report: never press Enter.</summary>
    OtherRow,

    /// <summary>Nothing could be read from the selection: never press Enter on a guess.</summary>
    Unreadable
}

/// <summary>What the search of the chooser's UI tree saw. Counts only; no window or row text except known catalog report names.</summary>
public sealed record ChooserScanStats(
    bool WorkAreaFound,
    int ElementsVisited,
    int RowLikeElements,
    IReadOnlyList<string> KnownReportNamesSeen,
    bool NodeCapReached)
{
    public static ChooserScanStats None { get; } = new(false, 0, 0, Array.Empty<string>(), false);
}

public static class ReportChooserSelection
{
    /// <summary>How many known report names the failure line lists at most.</summary>
    public const int MaxNamesListed = 6;

    /// <summary>
    /// The keyboard fallback for choosing a row: Ctrl+Home, type the whole
    /// report name, then (only after the caller read the selection back and
    /// it matched) Enter. NEVER an arrow key: stepping Down row by row and
    /// hoping is what Will saw scrolling past the report.
    /// </summary>
    public static IReadOnlyList<ChooserKeyStep> BuildTypeToSearchKeys(string rowText)
    {
        var name = ReportRowTextMatcher.Normalize(rowText);
        if (name.Length == 0) return Array.Empty<ChooserKeyStep>();

        return new[]
        {
            new ChooserKeyStep(ChooserKey.CtrlHome, null),
            new ChooserKeyStep(ChooserKey.TypeName, name),
            new ChooserKeyStep(ChooserKey.Enter, null)
        };
    }

    /// <summary>Exact whole-text match of what the chooser reports as selected (every readable text of the selected / focused row) against the requested report.</summary>
    public static ChooserReadback Decide(IEnumerable<string?> selectedRowTexts, string rowText)
    {
        var texts = selectedRowTexts.Where(t => ReportRowTextMatcher.Normalize(t).Length > 0).ToList();
        if (texts.Count == 0) return ChooserReadback.Unreadable;
        return AnyTextMatches(texts, rowText) ? ChooserReadback.ExactMatch : ChooserReadback.OtherRow;
    }

    /// <summary>
    /// True when ANY of the element's readable texts (UIA Name, Value, and
    /// MSAA name and value separately) is exactly the report name. A grid
    /// cell often has a generic Name ("Report Name Row 5") and the real
    /// text only in its Value, so the first non-empty text must not stop the
    /// check.
    /// </summary>
    public static bool AnyTextMatches(IEnumerable<string?> texts, string rowText) =>
        texts.Any(t => ReportRowTextMatcher.IsExactMatch(t, rowText));

    /// <summary>Is this one of Will's catalog report names (safe to log verbatim)?</summary>
    public static bool IsKnownReportName(string? text) => ReportRowNameRedaction.IsKnownSafeValue(text)
        && !string.Equals(ReportRowTextMatcher.Normalize(text), "Report Name", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(ReportRowTextMatcher.Normalize(text), "Report Description", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The single log line when the report could not be chosen: the report
    /// asked for, whether the report list was found and what the search of it
    /// saw (counts and catalog report names only; never a window title or any
    /// other on-screen text).
    /// </summary>
    public static string BuildNotFoundMessage(string rowText, ChooserScanStats stats)
    {
        var asked = $"Could not choose the report '{rowText}' on Pioneer's report list; nothing more was sent.";
        if (!stats.WorkAreaFound)
        {
            return $"{asked} The report list pane was not found on screen, so the Financial Reports page may not be open.";
        }

        var seen = stats.KnownReportNamesSeen.Take(MaxNamesListed).ToList();
        var seenText = seen.Count == 0
            ? "none of the known report names were visible"
            : $"known report names visible: {string.Join("; ", seen)}{(stats.KnownReportNamesSeen.Count > seen.Count ? "; ..." : string.Empty)}";
        var cap = stats.NodeCapReached ? " (search stopped at its element limit)" : string.Empty;
        return $"{asked} Report list found; {stats.ElementsVisited} element(s) searched{cap}, {stats.RowLikeElements} row-like; {seenText}.";
    }
}
