using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// Round 5 fix (W-T92, Will verbatim: "I have described the problem to
/// you in immaculate detail and have given you even the exact keystrokes
/// that are needed through the original macro file I sent you"). Round
/// 4's bare "MMddyyyy" digit formatting was never what the macro actually
/// types — it was an earlier coder's guess, not the spec. The macro
/// (Reports/recipes/README-macro-strings.txt) types two DIFFERENT
/// formats, each with dash separators:
///   %start_date_text% = %month%-01-%year%   -&gt; "MM-dd-yyyy" (4-digit year)
///   %date_text%        = mm'-'dd'-'yy         -&gt; "MM-dd-yy"   (2-digit year)
/// FormatBegin is the first (begin/as-of-range-start field), FormatEnd is
/// the second (end field, and the single field on an AsOfDate popup —
/// every AsOfDate macro types %date_text%, never %start_date_text%). No
/// FlaUI/UIA dependency — unit tested in
/// RxVerifyOverlay.Tests/Reports/ReportParameterKeysTests.cs.
/// </summary>
public static class ReportDateKeys
{
    /// <summary>Begin-date text for a DateRange popup's first field — the macro's %start_date_text% (%month%-01-%year%): two-digit month, dash, two-digit day, dash, FOUR-digit year.</summary>
    public static string FormatBegin(DateTime date) => date.ToString("MM-dd-yyyy");

    /// <summary>End-date text for a DateRange popup's second field, and the single field on an AsOfDate popup — the macro's %date_text% (Macro Express format mm'-'dd'-'yy): two-digit month, dash, two-digit day, dash, TWO-digit year.</summary>
    public static string FormatEnd(DateTime date) => date.ToString("MM-dd-yy");
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
                actions.Add(ReportParameterKeyAction.TypeText(ReportDateKeys.FormatEnd(end)));
                AddTrailingKeys(actions, entry, includeF12);
                if (includeF12) actions.Add(ReportParameterKeyAction.F12());
                break;

            case ReportParameterKind.DateRange:
                AddLeadingTabs(actions, entry);
                actions.Add(ReportParameterKeyAction.TypeText(ReportDateKeys.FormatBegin(begin)));
                for (var i = 0; i < entry.TabsBetweenDates; i++)
                {
                    actions.Add(ReportParameterKeyAction.Tab());
                }
                actions.Add(ReportParameterKeyAction.TypeText(ReportDateKeys.FormatEnd(end)));
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

/// <summary>
/// Round 5 fix (W-T92): a raw ordinal string compare made every legitimate
/// read-back a false "Mismatch" the moment Pioneer's own field redisplayed
/// what we typed in its own formatting — e.g. we type "09-01-2026"
/// (FormatBegin, 4-digit year) but a masked date field reads back
/// "09/01/2026" (slashes) or "9/1/2026" (no leading zeros), or we type
/// "09-28-26" (FormatEnd, 2-digit year) and the field reads back
/// "09/28/2026" (4-digit year). None of those are a WRONG date - only a
/// different display of the same one - so Decide now compares the
/// month/day/year components, not the raw text. Digits are grouped by
/// their ORIGINAL separator runs (not flattened into one digit string):
/// flattening "9/1/2026" would collapse into "912026", which is the wrong
/// length to tell apart from a genuine "91-20-26"-shaped mismatch, so the
/// three digit groups are compared positionally instead. A 2-digit vs.
/// 4-digit year only needs its last two digits to agree; month and day
/// must always match exactly. Only a real month/day/last-two-year-digit
/// mismatch triggers the caller's one-time Ctrl+A/Ctrl+V retry.
/// </summary>
public static class ReadbackEvaluator
{
    public static ReadbackDecision Decide(string? readback, string expected)
    {
        if (readback is null) return ReadbackDecision.Unavailable;

        if (string.Equals(readback, expected, StringComparison.Ordinal)) return ReadbackDecision.Ok;

        var readParts = ExtractDateDigitGroups(readback);
        var expectedParts = ExtractDateDigitGroups(expected);

        if (readParts is not null && expectedParts is not null
            && readParts[0] == expectedParts[0]
            && readParts[1] == expectedParts[1]
            && YearsMatch(readParts[2], expectedParts[2]))
        {
            return ReadbackDecision.Ok;
        }

        return ReadbackDecision.Mismatch;
    }

    /// <summary>Month/day/year as integers (leading zeros stripped), taken from the value's own digit runs — null unless there are exactly three groups (any non-digit separator: '/', '-', etc.).</summary>
    private static int[]? ExtractDateDigitGroups(string value)
    {
        var matches = Regex.Matches(value, @"\d+");
        if (matches.Count != 3) return null;

        var groups = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(matches[i].Value, out groups[i])) return null;
        }
        return groups;
    }

    private static bool YearsMatch(int a, int b) => a == b || (a % 100) == (b % 100);
}
