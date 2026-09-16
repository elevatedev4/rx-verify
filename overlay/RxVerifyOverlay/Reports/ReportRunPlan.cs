using System;
using System.IO;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// One resolved (report, date range, output path) instruction —
/// everything PioneerReportDriver needs to actually run one report, with
/// no UIA/FlaUI/WPF dependency of its own (pure data + pure date/string
/// math below, unit tested in ReportRunPlanTests.cs).
/// </summary>
public sealed record ReportRunItem(
    ReportCatalogEntry Entry,
    DateTime Begin,
    DateTime End,
    string OutputFolder,
    string OutputFilePath);

/// <summary>
/// Pure date-range default + filename/folder composition, split out of
/// ReportsWindow/ReportsCoordinator so it's fully unit-testable without
/// WPF or Windows at all.
///
/// DEFAULT RANGE (GOAL brief: "Defaults to all reports, for last
/// complete month"): Begin = the 1st of the calendar month before
/// <paramref name="today"/>'s month; End = the last day of that same
/// month. AsOf-kind reports use End as their single date (the "as of"
/// date is the end of the reporting period, same as Will's macros use
/// %date_text% — the END date — for every report's save-name date
/// token, AsOf reports included).
/// </summary>
public static class ReportRunPlan
{
    /// <summary>Last complete calendar month's Begin (1st) relative to <paramref name="today"/>.</summary>
    public static DateTime DefaultBegin(DateTime today)
    {
        var firstOfThisMonth = new DateTime(today.Year, today.Month, 1);
        return firstOfThisMonth.AddMonths(-1);
    }

    /// <summary>Last complete calendar month's End (last day) relative to <paramref name="today"/>.</summary>
    public static DateTime DefaultEnd(DateTime today)
    {
        var firstOfThisMonth = new DateTime(today.Year, today.Month, 1);
        return firstOfThisMonth.AddDays(-1);
    }

    /// <summary>
    /// The date a filename/AsOf field is stamped with for a given report
    /// — always <paramref name="end"/>, matching Will's macros
    /// (%date_text% is always the END date, even for the single-date
    /// Third Party Aged Trial Balance / Inventory Valuation reports).
    /// </summary>
    public static DateTime FileNameDate(ReportParameterKind kind, DateTime begin, DateTime end) => end;

    /// <summary>
    /// Will's naming convention verbatim: "&lt;mm-dd-yy of the END/as-of
    /// date&gt;_&lt;Report Name&gt;" (his macros: %date_text%_%report_save_name%,
    /// date mm-dd-yy) plus the entry's own extension.
    /// </summary>
    public static string BuildFileName(ReportCatalogEntry entry, DateTime begin, DateTime end)
    {
        var stampDate = FileNameDate(entry.ParameterKind, begin, end);
        var extension = entry.OutputFormat == ReportOutputFormat.Xlsx ? "xlsx" : "pdf";
        return $"{stampDate:MM-dd-yy}_{entry.SaveName}.{extension}";
    }

    /// <summary>
    /// Default output folder: %USERPROFILE%\Documents\Pioneer Reports\&lt;yyyy-MM
    /// of the End date&gt; — created on run (Directory.CreateDirectory is
    /// idempotent), not here (this method is pure/no I/O so it stays
    /// unit-testable without touching the filesystem).
    /// </summary>
    public static string DefaultOutputFolder(DateTime end)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents, "Pioneer Reports", end.ToString("yyyy-MM"));
    }

    /// <summary>
    /// Builds the full ReportRunItem for one catalog entry given a
    /// (begin, end) range and an output folder (caller supplies the
    /// folder — usually DefaultOutputFolder(end), but ReportsWindow lets
    /// Will override it via Browse).
    /// </summary>
    public static ReportRunItem Build(ReportCatalogEntry entry, DateTime begin, DateTime end, string outputFolder)
    {
        var fileName = BuildFileName(entry, begin, end);
        var fullPath = Path.Combine(outputFolder, fileName);
        return new ReportRunItem(entry, begin.Date, end.Date, outputFolder, fullPath);
    }
}
