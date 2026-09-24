using System;
using System.Collections.Generic;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// Round 4 fix (W-T92 follow-up — the owner's real "Report Parameters"
/// popup test: "It needs to select all on each date field and paste the
/// proper date into it in the format MMDDYYYY, then F12 to run the
/// report"). Pure "MMddyyyy" digit formatting (no slashes — Pioneer's own
/// masked date field fills those in) for the date text
/// ReportParameterKeyPlan types. No FlaUI/UIA dependency — unit tested in
/// RxVerifyOverlay.Tests/Reports/ReportParameterKeysTests.cs.
/// </summary>
public static class ReportDateKeys
{
    public static string Format(DateTime date) => date.ToString("MMddyyyy");
}

/// <summary>
/// Which literal keystroke group one step of a ReportParameterKeyPlan
/// sends — see PioneerReportDriver.ReplayReportParameterKeyPlan, the only
/// thing that actually sends these.
///
/// Round 5 fix (W-T92 follow-up — the owner's next round of testing after
/// Round 4 shipped: "It seems that the down arrow is being pushed
/// repeatedly, which lowers the month on the first item. Instead, you
/// should copy/paste as I mentioned before and use tab to navigate.").
/// FlaUI's Keyboard.Type of plain digit characters was apparently landing
/// as numpad/scan-code input in Pioneer's masked date field rather than
/// real top-row digits (numpad 2 == Down when NumLock is off), so typing
/// is out entirely for the date text — TypeText is replaced by PasteText,
/// which PioneerReportDriver now fulfills by setting the clipboard and
/// sending Ctrl+V instead of sending individual character keystrokes.
/// </summary>
public enum ReportParameterKeyActionKind
{
    SelectAll,
    PasteText,
    Tab,
    F12
}

/// <summary>One step of a ReportParameterKeyPlan. Text is set only for PasteText — every other kind carries no data (Value.Text is null for those).</summary>
public readonly record struct ReportParameterKeyAction(ReportParameterKeyActionKind Kind, string? Text = null)
{
    public static ReportParameterKeyAction SelectAll() => new(ReportParameterKeyActionKind.SelectAll);

    public static ReportParameterKeyAction PasteText(string text) => new(ReportParameterKeyActionKind.PasteText, text);

    public static ReportParameterKeyAction Tab() => new(ReportParameterKeyActionKind.Tab);

    public static ReportParameterKeyAction F12() => new(ReportParameterKeyActionKind.F12);
}

/// <summary>
/// Round 4 fix (W-T92 follow-up — owner's own words, verbatim: "On the
/// new report parameters window, you start with the begin date already
/// highlighted, paste start date, tab tab, paste end date, F12, then let
/// the report run fully before moving on"). Pure (entry, begin, end) -&gt;
/// ordered keystroke plan for Pioneer's "Report Parameters" popup — no
/// FlaUI/UIA dependency of its own, so the plan itself is fully unit
/// testable; PioneerReportDriver.ReplayReportParameterKeyPlan is the thin,
/// build/run-unverifiable-on-this-Mac shim that actually sends each step.
///
/// This REPLACES the old ValuePattern.SetValue-on-a-masked-field approach
/// (SetDateValue, still used only by the unrelated Payments screen) —
/// that was Will's own diagnosis of "the app was just cycling through
/// dates in a weird way": ValuePattern.SetValue on a masked date picker
/// doesn't behave like real keystrokes, so Pioneer's mask kept
/// re-interpreting the assigned text. Every step here is a literal
/// keystroke instead, macro-style — the same shape as Will's own Macro
/// Express recordings (Reports/recipes/README-macro-strings.txt).
/// </summary>
public static class ReportParameterKeyPlan
{
    /// <param name="includeF12">
    /// Default true (every real report run). False is used ONLY by the
    /// "Test date entry" button (W-T92 round 3, GOAL brief step 3) —
    /// same plan, minus the trailing F12, so Will can verify the paste/Tab
    /// behavior against a Report Parameters window he already has open
    /// without ever actually running/saving a report.
    /// </param>
    public static IReadOnlyList<ReportParameterKeyAction> Build(ReportCatalogEntry entry, DateTime begin, DateTime end, bool includeF12 = true)
    {
        var actions = new List<ReportParameterKeyAction>();

        switch (entry.ParameterKind)
        {
            case ReportParameterKind.AsOfDate:
                actions.Add(ReportParameterKeyAction.SelectAll());
                actions.Add(ReportParameterKeyAction.PasteText(ReportDateKeys.Format(end)));
                if (includeF12) actions.Add(ReportParameterKeyAction.F12());
                break;

            case ReportParameterKind.DateRange:
                actions.Add(ReportParameterKeyAction.SelectAll());
                actions.Add(ReportParameterKeyAction.PasteText(ReportDateKeys.Format(begin)));
                for (var i = 0; i < entry.TabsBetweenDates; i++)
                {
                    actions.Add(ReportParameterKeyAction.Tab());
                }
                actions.Add(ReportParameterKeyAction.SelectAll());
                actions.Add(ReportParameterKeyAction.PasteText(ReportDateKeys.Format(end)));
                if (includeF12) actions.Add(ReportParameterKeyAction.F12());
                break;

            case ReportParameterKind.PaymentsSearch:
            default:
                // Payments never reaches this popup at all - it's driven by
                // RunPaymentsExport/SetPaymentsDateRange directly on
                // _mainWindow, never PioneerReportDriver.SetReportParameters
                // (see ReportCatalog's own ParameterKind doc). An empty plan
                // here is deliberate, not a gap - ReplayReportParameterKeyPlan
                // would just be a no-op loop if this were ever called for one.
                break;
        }

        return actions;
    }
}

/// <summary>
/// Round 4 fix (W-T92 follow-up, GOAL brief step 4: "make the timeout
/// per-report from the catalog (the macros use 2.5-20 s run times; use
/// those x4 as the ceiling, minimum 30 s)"). Pure math, no FlaUI/UIA
/// dependency — MacroRunTime comes from Will's own %report_run_time%
/// macro values (Reports/recipes/README-macro-strings.txt), how long HE
/// observed each report actually take to generate; ×4 leaves generous
/// headroom over the slowest run his macros ever waited out, with a 30s
/// floor for any report whose macro run time is unset
/// (TimeSpan.Zero — e.g. the not-yet-enabled POS daily entry).
/// </summary>
public static class ReportTimeoutPlan
{
    private static readonly TimeSpan MinimumTimeout = TimeSpan.FromSeconds(30);

    public static TimeSpan CalculateTimeout(TimeSpan macroRunTime)
    {
        if (macroRunTime <= TimeSpan.Zero) return MinimumTimeout;

        var ceiling = TimeSpan.FromTicks(macroRunTime.Ticks * 4);
        return ceiling > MinimumTimeout ? ceiling : MinimumTimeout;
    }
}
