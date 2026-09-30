using System;
using System.Collections.Generic;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// Which "Report Parameters" popup shape a catalog entry uses (see
/// ReportsWindow.xaml's date controls and PioneerReportDriver's
/// per-kind step list). DateRange = Begin + End textboxes. AsOfDate = a
/// single Date textbox (Inventory Valuation also has an "Inventory
/// Group" dropdown, left on its all-groups default — see
/// PioneerReportDriver.RunFinancialReport's own doc). PaymentsSearch is
/// the one report that isn't a "Financial Reports" row at all — it's
/// the Third Party > Payments > Reconcile screen's "Payment Confirmed
/// Between" search, driven by PioneerReportDriver.RunPaymentsExport
/// instead of RunFinancialReport.
/// </summary>
public enum ReportParameterKind
{
    DateRange,
    AsOfDate,
    PaymentsSearch
}

/// <summary>Which export button PioneerReportDriver invokes and what extension ReportRunPlan.BuildFileName appends.</summary>
public enum ReportOutputFormat
{
    Pdf,
    Xlsx
}

/// <summary>
/// One row of Will's "Run financial reports" list (or, for Payments, the
/// Third Party > Payments screen) — pure data, no UIA/FlaUI/WPF
/// dependency. Built from his recorded walkthrough + Macro Express
/// macros (see Reports/recipes/README-macro-strings.txt) — PioneerRowText
/// is the literal row/report name PioneerReportDriver searches for in
/// Pioneer's UI; DisplayName is Will's own name for the report, shown in
/// ReportsWindow's picker.
/// </summary>
/// <param name="PioneerRowTextAlias">
/// Round 3 fix (owner's screenshot of the real "Run Financial Reports"
/// grid has no row literally named "Third Party Aged Trial Balance As of
/// Date" - the closest is "Third Party Reconciliation Account Aged Trial
/// Balance"). Optional secondary row-name PioneerReportDriver's
/// row-selection step tries SECOND, after PioneerRowText, before giving
/// up on that report entirely — null for every entry except
/// ThirdPartyAgedTrialBalanceKey. Kept as an alias (not just replaced
/// outright) in case Will's install genuinely has an older/differently
/// named row on some other workstation.
/// </param>
/// <param name="TabsBetweenDates">
/// Round 4 fix (W-T92 follow-up — owner's real "Report Parameters" popup
/// test: "you start with the begin date already highlighted, paste start
/// date, tab tab, paste end date, F12"). How many Tab keys
/// ReportParameterKeyPlan sends between the Begin and End date fields for
/// a DateRange entry — defaults to the owner's own description (2).
/// Will's macro strings (recipes/README-macro-strings.txt) show most
/// date-range reports actually use a single Tab, so those entries below
/// override this to 1; ignored entirely for AsOfDate/PaymentsSearch
/// entries (a single date field has nothing to Tab between).
/// </param>
/// <param name="MacroRunTime">
/// Round 4 fix (W-T92 follow-up, GOAL brief step 4: "make the timeout
/// per-report from the catalog"). How long Will's own Macro Express
/// macros waited (their %report_run_time% variable) after F12 before
/// moving on for this report — ReportTimeoutPlan.CalculateTimeout turns
/// this into PioneerReportDriver's actual per-report preview-wait
/// timeout (×4, 30s floor). TimeSpan.Zero (the default) for any entry
/// the macros didn't record a run time for — ReportTimeoutPlan's 30s
/// floor covers that case.
/// </param>
/// <param name="LeadingTabs">
/// Round 4 fix (W-T92 round 4 — Will's report on build 67a1566: "it's
/// making it to the report parameters screen, then it does something,
/// then it starts scrolling through the months in the initial date
/// window ... You need to use tab to get between fields"). How many Tab
/// keys ReportParameterKeyPlan sends BEFORE typing the first date — the
/// popup's initial focus is NOT reliably the Begin/Date field for every
/// report (round 3 assumed it always was, which is exactly what put the
/// cursor somewhere else and made typed keystrokes "scroll through
/// months"). Value is each report's own macro line
/// (recipes/README-macro-strings.txt) counted verbatim; 0 (the default)
/// for every report whose macro types straight into "Set up report"
/// with no leading &lt;TAB&gt;.
/// </param>
/// <param name="TrailingKeys">
/// Round 4 fix, reviewer round 4 correction. Extra keys
/// ReportParameterKeyPlan sends AFTER the last date is typed — null/empty
/// (the default) for every report whose macro goes straight to
/// &lt;F12&gt;. Only Inventory Valuation's macro has any (Tab, Arrow Down,
/// then its OWN trailing F12 — its macro is literally
/// %date_text%&lt;TAB&gt;&lt;ARROW DOWN&gt;&lt;F12&gt;&lt;F12&gt;, two F12s
/// back to back: the first is part of this list, the second is the
/// "run" F12 ReportParameterKeyPlan.Build appends separately for every
/// entry) — see that entry's own comment below. Restricted to
/// Tab/ArrowDown/F12; never SelectAll/TypeText/PasteText. Any F12 in this
/// list is stripped by ReportParameterKeyPlan.Build when includeF12 is
/// false ("Test date entry"), same as the separate run F12.
/// </param>
public sealed record ReportCatalogEntry(
    string Key,
    string DisplayName,
    string PioneerRowText,
    ReportParameterKind ParameterKind,
    ReportOutputFormat OutputFormat,
    string SaveName,
    bool Enabled,
    string? PioneerRowTextAlias = null,
    int TabsBetweenDates = 2,
    TimeSpan MacroRunTime = default,
    int LeadingTabs = 0,
    IReadOnlyList<ReportParameterKeyActionKind>? TrailingKeys = null);

/// <summary>
/// The fixed phase-1 report list (GOAL brief "WHAT PIONEER LOOKS LIKE"
/// items 1-8). Order here is the order ReportsWindow lists them in and
/// the order ReportsCoordinator runs them in when multiple are selected
/// — matches Will's numbered list. POS daily reports (item 8) is the one
/// entry with Enabled=false: listed greyed-out ("(coming next)") rather
/// than omitted, since Will named it explicitly as a future report, not
/// a rejected one.
/// </summary>
public static class ReportCatalog
{
    public const string ArAgedTrialBalanceKey = "ar-control-balance";
    public const string ThirdPartyAgedTrialBalanceKey = "third-party-aged-trial-balance";
    public const string ThirdPartyControlBalanceKey = "third-party-control-balance";
    public const string InventoryValuationKey = "inventory-valuation";
    public const string InventoryControlBalanceKey = "inventory-control-balance";
    public const string SalesSummaryKey = "sales-summary";
    public const string PaymentsKey = "payments";
    public const string PosDailyKey = "pos-daily";

    public static IReadOnlyList<ReportCatalogEntry> All { get; } = new List<ReportCatalogEntry>
    {
        new(
            Key: ArAgedTrialBalanceKey,
            DisplayName: "Customer A/R Control Balance",
            PioneerRowText: "A/R Control Balance by Date Range",
            ParameterKind: ReportParameterKind.DateRange,
            OutputFormat: ReportOutputFormat.Pdf,
            SaveName: "Customer A-R Control Balance",
            Enabled: true,
            // Round 6 correction (W-T92, Will 2026-09-29, verbatim: "you
            // tabbed before entering the start date. Instead, you need to
            // enter the start date, then tab to the end date field and
            // enter that, then F12 to run the report"). The recorded
            // macro's <TAB><TAB> leader was wrong for this popup — it
            // opens with focus already in the Begin/start-date field, so
            // 0 leading tabs is still correct (kept from round 6).
            //
            // Round 7 correction (W-T92, Will 2026-09-30, verbatim: "You
            // need to tab twice to enter to the end date. Your 1 tab
            // ended in the time field of the start date."). Round 6's
            // single Tab between Begin and End was one tab short — the
            // popup's Begin/start-date field has its own trailing time
            // sub-field, so the FIRST Tab only leaves the date portion
            // for the time portion of that SAME field; a second Tab is
            // what actually reaches the End/date field. Actual sequence:
            // %start_date_text%<TAB><TAB>%date_text%<F12>.
            TabsBetweenDates: 2,
            MacroRunTime: TimeSpan.FromSeconds(6),
            LeadingTabs: 0),

        new(
            Key: ThirdPartyAgedTrialBalanceKey,
            DisplayName: "Third Party Aged Trial Balance",
            // Round 3 fix: the owner's real "Run Financial Reports" grid
            // screenshot has no row named "Third Party Aged Trial Balance
            // As of Date" — the closest verbatim row is "Third Party
            // Reconciliation Account Aged Trial Balance" ("Balance of
            // Third Party Reconciliation Accounts as of Custom Date").
            // The old guessed name is kept as PioneerRowTextAlias, tried
            // second by PioneerReportDriver's row-selection step, in case
            // some other install genuinely has it under the old name.
            PioneerRowText: "Third Party Reconciliation Account Aged Trial Balance",
            ParameterKind: ReportParameterKind.AsOfDate,
            OutputFormat: ReportOutputFormat.Pdf,
            SaveName: "Third Party Aged Trial Balance",
            Enabled: true,
            PioneerRowTextAlias: "Third Party Aged Trial Balance As of Date",
            // Macro "Third Party Aged Trial Balance": %date_text%<F12> — no leading tabs, 20s report_run_time.
            MacroRunTime: TimeSpan.FromSeconds(20)),

        new(
            Key: ThirdPartyControlBalanceKey,
            DisplayName: "Third Party Control Balance Summary",
            PioneerRowText: "Third Party Control Balance Summary by Date Range",
            ParameterKind: ReportParameterKind.DateRange,
            OutputFormat: ReportOutputFormat.Pdf,
            SaveName: "Third Party Control Balance Summary",
            Enabled: true,
            // Macro "Third Party Control Balance": %start_date_text%<TAB>%date_text%<F12> — no leading tabs, single tab between dates, 20s report_run_time.
            TabsBetweenDates: 1,
            MacroRunTime: TimeSpan.FromSeconds(20)),

        new(
            Key: InventoryValuationKey,
            DisplayName: "Inventory Valuation",
            PioneerRowText: "Inventory Valuation",
            ParameterKind: ReportParameterKind.AsOfDate,
            OutputFormat: ReportOutputFormat.Pdf,
            SaveName: "Inventory Valuation",
            Enabled: true,
            // Macro "Inventory valuation": %date_text%<TAB><ARROW DOWN><F12><F12> — no leading tabs; after the date, Tab then Arrow Down reaches/confirms the Inventory Group dropdown (left at its all-groups default per this file's own header doc), then the macro's own F12 (its first one, part of TrailingKeys), then ReportParameterKeyPlan.Build's separate run F12 - mirrored exactly (reviewer round 4 correction: round 4 originally collapsed the macro's two F12s into one). 15s report_run_time.
            MacroRunTime: TimeSpan.FromSeconds(15),
            TrailingKeys: new[] { ReportParameterKeyActionKind.Tab, ReportParameterKeyActionKind.ArrowDown, ReportParameterKeyActionKind.F12 }),

        new(
            Key: InventoryControlBalanceKey,
            DisplayName: "Inventory Control Balance Summary",
            PioneerRowText: "Inventory Control Balance Summary",
            ParameterKind: ReportParameterKind.DateRange,
            OutputFormat: ReportOutputFormat.Pdf,
            SaveName: "Inventory Control Balance Summary",
            Enabled: true,
            // Macro "Inventory Control Balance": %start_date_text%<TAB>%date_text%<F12> — no leading tabs, single tab between dates, 20s report_run_time.
            TabsBetweenDates: 1,
            MacroRunTime: TimeSpan.FromSeconds(20)),

        new(
            Key: SalesSummaryKey,
            DisplayName: "Sales Summary",
            PioneerRowText: "Accrual System Sales Totals Summary",
            ParameterKind: ReportParameterKind.DateRange,
            OutputFormat: ReportOutputFormat.Pdf,
            SaveName: "Sales Summary",
            Enabled: true,
            // Macro "Sales summary": %start_date_text%<TAB>%date_text%<F12> — no leading tabs, single tab between dates, 6s report_run_time.
            TabsBetweenDates: 1,
            MacroRunTime: TimeSpan.FromSeconds(6)),

        new(
            Key: PaymentsKey,
            DisplayName: "Payments",
            PioneerRowText: "Reconcile Third Party Payments",
            ParameterKind: ReportParameterKind.PaymentsSearch,
            OutputFormat: ReportOutputFormat.Xlsx,
            SaveName: "Third Party Payments",
            Enabled: true,
            // Macro "Third Party Payments": 2.5s report_run_time. Never actually consulted for its preview timeout today — RunPaymentsExport uses DefaultReportTimeout, not ReportTimeoutPlan — kept here for completeness/consistency with the other entries.
            MacroRunTime: TimeSpan.FromSeconds(2.5)),

        new(
            Key: PosDailyKey,
            DisplayName: "Sales and POS daily reports (coming next)",
            PioneerRowText: "",
            ParameterKind: ReportParameterKind.DateRange,
            OutputFormat: ReportOutputFormat.Pdf,
            SaveName: "POS Daily",
            Enabled: false)
    };

    public static ReportCatalogEntry? FindByKey(string key)
    {
        foreach (var entry in All)
        {
            if (entry.Key == key) return entry;
        }
        return null;
    }
}
