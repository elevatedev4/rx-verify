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
public sealed record ReportCatalogEntry(
    string Key,
    string DisplayName,
    string PioneerRowText,
    ReportParameterKind ParameterKind,
    ReportOutputFormat OutputFormat,
    string SaveName,
    bool Enabled,
    string? PioneerRowTextAlias = null);

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
            Enabled: true),

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
            PioneerRowTextAlias: "Third Party Aged Trial Balance As of Date"),

        new(
            Key: ThirdPartyControlBalanceKey,
            DisplayName: "Third Party Control Balance Summary",
            PioneerRowText: "Third Party Control Balance Summary by Date Range",
            ParameterKind: ReportParameterKind.DateRange,
            OutputFormat: ReportOutputFormat.Pdf,
            SaveName: "Third Party Control Balance Summary",
            Enabled: true),

        new(
            Key: InventoryValuationKey,
            DisplayName: "Inventory Valuation",
            PioneerRowText: "Inventory Valuation",
            ParameterKind: ReportParameterKind.AsOfDate,
            OutputFormat: ReportOutputFormat.Pdf,
            SaveName: "Inventory Valuation",
            Enabled: true),

        new(
            Key: InventoryControlBalanceKey,
            DisplayName: "Inventory Control Balance Summary",
            PioneerRowText: "Inventory Control Balance Summary",
            ParameterKind: ReportParameterKind.DateRange,
            OutputFormat: ReportOutputFormat.Pdf,
            SaveName: "Inventory Control Balance Summary",
            Enabled: true),

        new(
            Key: SalesSummaryKey,
            DisplayName: "Sales Summary",
            PioneerRowText: "Accrual System Sales Totals Summary",
            ParameterKind: ReportParameterKind.DateRange,
            OutputFormat: ReportOutputFormat.Pdf,
            SaveName: "Sales Summary",
            Enabled: true),

        new(
            Key: PaymentsKey,
            DisplayName: "Payments",
            PioneerRowText: "Reconcile Third Party Payments",
            ParameterKind: ReportParameterKind.PaymentsSearch,
            OutputFormat: ReportOutputFormat.Xlsx,
            SaveName: "Third Party Payments",
            Enabled: true),

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
