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
/// Round 4 (W-T92 round 4 rewrite — Will's build-67a1566 report: "it's
/// making it to the report parameters screen, then it does something,
/// then it starts scrolling through the months in the initial date
/// window, probably pushing the down arrow. You need to use tab to get
/// between fields", plus his working Macro Express macros themselves).
/// Round 5's clipboard PasteText is no longer the DEFAULT way a date gets
/// into a field — Tab/TypeText/ArrowDown/F12 is exactly Will's own macro
/// shape (leading Tabs to actually reach the field, then the literal date
/// text, never a click or a paste). TypeText is sent one Unicode
/// character at a time via SendInput's KEYEVENTF_UNICODE (see
/// PioneerReportDriver.TypeUnicodeText) specifically so no VK/scan-code
/// mapping is ever involved — the exact mechanism round 5 suspected of
/// turning a numpad digit into an arrow key. SelectAll/PasteText remain
/// as action kinds ONLY for PioneerReportDriver's own runtime mismatch
/// retry (Ctrl+A, Ctrl+V on a bad read-back) — a ReportParameterKeyPlan
/// built by ReportParameterKeyPlan.Build never contains either; they are
/// sent directly by the driver, not planned in advance, since whether a
/// retry happens depends on a live UIA read-back this pure class can't
/// see.
/// </summary>
public enum ReportParameterKeyActionKind
{
    Tab,
    TypeText,
    ArrowDown,
    F12,
    SelectAll,
    PasteText
}

/// <summary>One step of a ReportParameterKeyPlan. Text is set only for TypeText — every other kind carries no data (Value.Text is null for those).</summary>
public readonly record struct ReportParameterKeyAction(ReportParameterKeyActionKind Kind, string? Text = null)
{
    public static ReportParameterKeyAction Tab() => new(ReportParameterKeyActionKind.Tab);

    public static ReportParameterKeyAction TypeText(string text) => new(ReportParameterKeyActionKind.TypeText, text);

    public static ReportParameterKeyAction ArrowDown() => new(ReportParameterKeyActionKind.ArrowDown);

    public static ReportParameterKeyAction F12() => new(ReportParameterKeyActionKind.F12);

    public static ReportParameterKeyAction SelectAll() => new(ReportParameterKeyActionKind.SelectAll);

    public static ReportParameterKeyAction PasteText(string text) => new(ReportParameterKeyActionKind.PasteText, text);
}

/// <summary>
/// Round 4 rewrite (W-T92 round 4 — Will's build-67a1566 report, verbatim:
/// "it's making it to the report parameters screen, then it does
/// something, then it starts scrolling through the months in the initial
/// date window, probably pushing the down arrow. You need to use tab to
/// get between fields"). Pure (entry, begin, end) -&gt; ordered keystroke
/// plan for Pioneer's "Report Parameters" popup, now built to be exactly
/// each report's own Macro Express sequence (Reports/recipes/
/// README-macro-strings.txt): ReportCatalogEntry.LeadingTabs tabs first
/// (the popup does NOT reliably open with Begin/Date already focused —
/// that wrong assumption in round 3 is what let Ctrl+A/paste land on some
/// other control and start "scrolling through months"), then the date
/// text itself, then TabsBetweenDates/the second date for a range, then
/// any TrailingKeys, then F12. No FlaUI/UIA dependency of its own, so the
/// plan itself is fully unit testable; PioneerReportDriver.
/// ReplayReportParameterKeyPlan is the thin, build/run-unverifiable-on-
/// this-Mac shim that actually sends each step (and owns the live
/// read-back + one-retry logic this pure class can't see).
/// </summary>
public static class ReportParameterKeyPlan
{
    /// <param name="includeF12">
    /// Default true (every real report run). False is used ONLY by the
    /// "Test date entry" button (W-T92 round 3, GOAL brief step 3) —
    /// same plan, minus the trailing F12, so Will can verify the
    /// tab/type behavior against a Report Parameters window he already
    /// has open without ever actually running/saving a report.
    /// </param>
    public static IReadOnlyList<ReportParameterKeyAction> Build(ReportCatalogEntry entry, DateTime begin, DateTime end, bool includeF12 = true)
    {
        var actions = new List<ReportParameterKeyAction>();

        switch (entry.ParameterKind)
        {
            case ReportParameterKind.AsOfDate:
                AddLeadingTabs(actions, entry);
                actions.Add(ReportParameterKeyAction.TypeText(ReportDateKeys.Format(end)));
                AddTrailingKeys(actions, entry, includeF12);
                if (includeF12) actions.Add(ReportParameterKeyAction.F12());
                break;

            case ReportParameterKind.DateRange:
                AddLeadingTabs(actions, entry);
                actions.Add(ReportParameterKeyAction.TypeText(ReportDateKeys.Format(begin)));
                for (var i = 0; i < entry.TabsBetweenDates; i++)
                {
                    actions.Add(ReportParameterKeyAction.Tab());
                }
                actions.Add(ReportParameterKeyAction.TypeText(ReportDateKeys.Format(end)));
                AddTrailingKeys(actions, entry, includeF12);
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

    private static void AddLeadingTabs(List<ReportParameterKeyAction> actions, ReportCatalogEntry entry)
    {
        for (var i = 0; i < entry.LeadingTabs; i++)
        {
            actions.Add(ReportParameterKeyAction.Tab());
        }
    }

    /// <summary>
    /// Only Inventory Valuation has any today (Tab, Arrow Down, F12 — see
    /// its ReportCatalog entry comment, matching its macro's own trailing
    /// &lt;TAB&gt;&lt;ARROW DOWN&gt;&lt;F12&gt; before the run F12); every
    /// other report's TrailingKeys is null/empty and this is a no-op.
    ///
    /// Round 4 reviewer fix (blocking finding 3): the macro's OWN trailing
    /// F12 (Inventory Valuation: %date_text%&lt;TAB&gt;&lt;ARROW DOWN&gt;&lt;F12&gt;&lt;F12&gt;)
    /// is now part of TrailingKeys itself, not just the separate "run" F12
    /// Build() appends afterward - so <paramref name="includeF12"/> has to
    /// gate BOTH: "Test date entry" (includeF12: false) must strip every
    /// trailing F12, not just the very last one, or it would still fire a
    /// keystroke that runs the report.
    /// </summary>
    private static void AddTrailingKeys(List<ReportParameterKeyAction> actions, ReportCatalogEntry entry, bool includeF12)
    {
        if (entry.TrailingKeys is null) return;

        foreach (var kind in entry.TrailingKeys)
        {
            if (kind == ReportParameterKeyActionKind.F12 && !includeF12) continue;

            actions.Add(kind switch
            {
                ReportParameterKeyActionKind.Tab => ReportParameterKeyAction.Tab(),
                ReportParameterKeyActionKind.ArrowDown => ReportParameterKeyAction.ArrowDown(),
                ReportParameterKeyActionKind.F12 => ReportParameterKeyAction.F12(),
                _ => throw new ArgumentOutOfRangeException(nameof(entry), kind, "ReportCatalogEntry.TrailingKeys may only contain Tab, ArrowDown, or F12.")
            });
        }
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

/// <summary>
/// Round 4 reviewer fix (blocking finding 2): PioneerReportDriver.
/// TryReadFocusedFieldValue returning null used to be treated as a
/// mismatch (a wrong value), triggering the one clipboard-paste retry and
/// then aborting the whole report without F12 if the retry ALSO read back
/// null - which is exactly what happens on every run against any field
/// that doesn't expose ValuePattern or LegacyIAccessible at all (readback
/// genuinely impossible, not wrong). This distinguishes the three real
/// outcomes as pure data so the decision itself is unit-testable without
/// any FlaUI/UIA dependency: Ok (matches), Unavailable (readback is null -
/// the macro-faithful keys were already sent; nothing to retry against, so
/// the driver proceeds without retrying or aborting), Mismatch (readback
/// is non-null and genuinely different - the one clipboard-paste retry
/// applies here, and a second Mismatch aborts without F12).
/// </summary>
public enum ReadbackDecision
{
    Ok,
    Unavailable,
    Mismatch
}

/// <summary>Pure wrapper around ReadbackDecision - see that enum's own doc.</summary>
public static class ReadbackEvaluator
{
    public static ReadbackDecision Decide(string? readback, string expected)
    {
        if (readback is null) return ReadbackDecision.Unavailable;
        return string.Equals(readback, expected, StringComparison.Ordinal) ? ReadbackDecision.Ok : ReadbackDecision.Mismatch;
    }
}
