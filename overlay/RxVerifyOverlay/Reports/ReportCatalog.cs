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
    TimeSpan MacroRunTime = default);

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
            // Macro "Customer A/R Control Balance": <TAB><TAB>%start_date_text%<TAB><TAB>%date_text%<F12> — 2 tabs between dates (the default), 6s report_run_time.
            TabsBetweenDates: 2,
            MacroRunTime: TimeSpan.FromSeconds(6)),

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
            // Macro "Third Party Aged Trial Balance": %date_text%<F12> — 20s report_run_time.
            MacroRunTime: TimeSpan.FromSeconds(20)),

        new(
            Key: ThirdPartyControlBalanceKey,
            DisplayName: "Third Party Control Balance Summary",
            PioneerRowText: "Third Party Control Balance Summary by Date Range",
            ParameterKind: ReportParameterKind.DateRange,
            OutputFormat: ReportOutputFormat.Pdf,
            SaveName: "Third Party Control Balance Summary",
            Enabled: true,
            // Macro "Third Party Control Balance": %start_date_text%<TAB>%date_text%<F12> — single tab, 20s report_run_time.
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
            // Macro "Inventory valuation": %date_text%<TAB><ARROW DOWN><F12><F12> — 15s report_run_time. The dropdown/second F12 are left at their all-groups default per this file's own header doc.
            MacroRunTime: TimeSpan.FromSeconds(15)),

        new(
            Key: InventoryControlBalanceKey,
            DisplayName: "Inventory Control Balance Summary",
            PioneerRowText: "Inventory Control Balance Summary",
            ParameterKind: ReportParameterKind.DateRange,
            OutputFormat: ReportOutputFormat.Pdf,
            SaveName: "Inventory Control Balance Summary",
            Enabled: true,
            // Macro "Inventory Control Balance": %start_date_text%<TAB>%date_text%<F12> — single tab, 20s report_run_time.
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
            // Macro "Sales summary": %start_date_text%<TAB>%date_text%<F12> — single tab, 6s report_run_time.
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
