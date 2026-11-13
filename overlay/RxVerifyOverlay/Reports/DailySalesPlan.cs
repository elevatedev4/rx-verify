using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RxVerifyOverlay.Reports;

// Will, HQ 2026-11-10 (verbatim): "I need to be able to run individual daily
// sales reports for the month (sales summary and POS summary). ... I Would
// like you to add this functionality to the reporting part of hte Rxverify
// app so I Can put in a range and run those reports automatically. Pay close
// attention to the macro file because it works pretty efficiently."
//
// Pure half of "Daily Sales + POS": the date loop, the file names and the
// step order, with no UIA / Win32 / WPF, so it is unit tested on any OS
// (RxVerifyOverlay.Tests/Reports/DailySalesPlanTests.cs).
//
// What the macro ("POS sales.mex", group "Reports-POS Sales") does, read off
// its strings:
//   loop 1 "Run POS reports": per day date_text = mm-dd-yy; on the "Search
//     Drawers" screen: <ALT> <TAB><TAB> date <TAB> date <ALT>o <DOWN> <ENTER>;
//     wait for window "Sales Summary Report"; check window; "Save as":
//     <ALT>n, name POS_<date_text>, <ENTER>; "Confirm Save As" -> overwrite;
//     check window "Sales Summary Report"; <ALT><F4>; wait "Search Drawers".
//   loop 2 "Run sales reports": open financial reports (<ALT>, "reports");
//     per day: date, <ALT>e, date, <F12><F12>; wait; check window; "Save as":
//     <ALT>n, name Sales_<date_text>, <ENTER>; "Confirm Save As" -> overwrite;
//     check window "Accrual system"; <ALT><F4>.
// The two loops run as two PASSES over the whole range (all POS days, then
// all Sales days), which is the order Build keeps.

/// <summary>Which of the two daily reports a step produces.</summary>
public enum DailyReportKind
{
    /// <summary>POS Summary (the macro's loop 1, saved POS_mm-dd-yy).</summary>
    Pos,

    /// <summary>Sales Summary (the macro's loop 2, saved Sales_mm-dd-yy).</summary>
    Sales
}

/// <summary>One report for one day. <paramref name="Index"/> is 1-based over the whole run.</summary>
public sealed record DailyReportStep(
    DailyReportKind Kind,
    DateTime Day,
    string OutputFolder,
    string OutputFilePath,
    int Index,
    int Total);

public static class DailySalesPlan
{
    /// <summary>Upper bound on the day count of one run - a typo (year 2206) must not queue thousands of reports.</summary>
    public const int MaxDays = 366;

    /// <summary>The macro jumps to the End date field with Alt+e.</summary>
    public const string EndFieldAccelerator = "e";

    public static string FilePrefix(DailyReportKind kind) => kind == DailyReportKind.Pos ? "POS" : "Sales";

    public static string DisplayName(DailyReportKind kind) => kind == DailyReportKind.Pos ? "POS Summary" : "Sales Summary";

    /// <summary>The macro's date text: mm-dd-yy.</summary>
    public static string DateText(DateTime day) => day.ToString("MM-dd-yy", CultureInfo.InvariantCulture);

    /// <summary>"POS_mm-dd-yy" / "Sales_mm-dd-yy" - the macro's names, without extension.</summary>
    public static string BaseName(DailyReportKind kind, DateTime day) => $"{FilePrefix(kind)}_{DateText(day)}";

    /// <summary>Base name plus ".pdf" (both reports are saved as PDF like the monthly ones).</summary>
    public static string FileName(DailyReportKind kind, DateTime day) => BaseName(kind, day) + ".pdf";

    public static string FilePath(string outputFolder, DailyReportKind kind, DateTime day) =>
        Path.Combine(outputFolder, FileName(kind, day));

    /// <summary>False (with a reason) for a reversed range or one longer than <see cref="MaxDays"/>.</summary>
    public static bool TryValidateRange(DateTime begin, DateTime end, out string? problem)
    {
        problem = null;
        if (end.Date < begin.Date)
        {
            problem = "End date is before Begin date.";
            return false;
        }

        var days = (end.Date - begin.Date).Days + 1;
        if (days > MaxDays)
        {
            problem = $"That range is {days} days; the limit is {MaxDays}.";
            return false;
        }

        return true;
    }

    /// <summary>Every day from begin to end, inclusive (the macro's "Number of days" loop).</summary>
    public static IReadOnlyList<DateTime> EnumerateDays(DateTime begin, DateTime end)
    {
        var days = new List<DateTime>();
        for (var day = begin.Date; day <= end.Date; day = day.AddDays(1))
        {
            days.Add(day);
        }

        return days;
    }

    /// <summary>
    /// The run in macro order: pass 1 = POS for every day, pass 2 = Sales for
    /// every day (a kind that is not wanted is simply absent). Empty for an
    /// invalid range.
    /// </summary>
    public static IReadOnlyList<DailyReportStep> Build(DateTime begin, DateTime end, string outputFolder, bool includePos, bool includeSales)
    {
        if (!TryValidateRange(begin, end, out _)) return Array.Empty<DailyReportStep>();

        var kinds = new List<DailyReportKind>();
        if (includePos) kinds.Add(DailyReportKind.Pos);
        if (includeSales) kinds.Add(DailyReportKind.Sales);

        var days = EnumerateDays(begin, end);
        var total = kinds.Count * days.Count;
        var steps = new List<DailyReportStep>(total);
        foreach (var kind in kinds)
        {
            foreach (var day in days)
            {
                steps.Add(new DailyReportStep(kind, day, outputFolder, FilePath(outputFolder, kind, day), steps.Count + 1, total));
            }
        }

        return steps;
    }

    /// <summary>Null when there is something to run; otherwise why the run is refused before anything is sent to Pioneer.</summary>
    public static string? CheckRunnable(IReadOnlyList<DailyReportStep> steps) =>
        steps.Count == 0 ? "Nothing to run." : null;

    /// <summary>
    /// THE one place the "month and year + _EOM reports" folder name is
    /// decided (Will, W-Q11: "It should make a folder in downloads that has
    /// the month and year + _EOM reports"): e.g. "2026-10_EOM reports". Takes the
    /// range's END date. If Will wants the monthly reports there too
    /// (W-Q12), ReportRunPlan.DefaultOutputFolder calls this and nothing else changes.
    /// </summary>
    public static string EomFolderName(DateTime rangeEnd) => rangeEnd.ToString("yyyy-MM", CultureInfo.InvariantCulture) + "_EOM reports";

    /// <summary>"&lt;Downloads&gt;\&lt;yyyy-MM&gt;_EOM reports" - <paramref name="downloadsFolder"/> is the user's Downloads folder.</summary>
    public static string DefaultOutputFolder(DateTime rangeEnd, string downloadsFolder) =>
        Path.Combine(downloadsFolder, EomFolderName(rangeEnd));

    /// <summary>The current user's Downloads folder (%USERPROFILE%\Downloads).</summary>
    public static string UserDownloadsFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// The folder a daily run saves to: the EOM folder unless the user
    /// typed in or browsed the "Save to" box (<paramref name="userChoseFolder"/>)
    /// and left something in it. Choosing the monthly default folder on
    /// purpose therefore works too.
    /// </summary>
    public static string ResolveOutputFolder(string? typedFolder, bool userChoseFolder, DateTime rangeEnd, string downloadsFolder)
    {
        var typed = (typedFolder ?? string.Empty).Trim();
        if (!userChoseFolder || typed.Length == 0) return DefaultOutputFolder(rangeEnd, downloadsFolder);
        return typed;
    }

    /// <summary>
    /// Runs longer than this many days ask for a confirmation first (W-T98: the
    /// Begin/End boxes default to last month, so an untouched run is a full
    /// month - 31 days - not the 3 days Will meant).
    /// </summary>
    public const int ConfirmAboveDays = 7;

    /// <summary>Days in the range, inclusive (Begin = End is 1 day).</summary>
    public static int DayCount(DateTime begin, DateTime end) => (end.Date - begin.Date).Days + 1;

    public static bool NeedsRangeConfirmation(int dayCount) => dayCount > ConfirmAboveDays;

    /// <summary>"62 report(s) over 31 day(s), 10-01-26 to 10-31-26" - the range the run will use, so it can be checked at a glance.</summary>
    public static string DescribeRange(DateTime begin, DateTime end, int reportCount) =>
        $"{reportCount} report(s) over {DayCount(begin, end)} day(s), {DateText(begin)} to {DateText(end)}";

    /// <summary>The question asked before a long run; names the range and says how to change it.</summary>
    public static string RangeConfirmationText(DateTime begin, DateTime end, int reportCount) =>
        $"Run {DescribeRange(begin, end, reportCount)}?\n\nIf you meant fewer days, choose No and change the Begin and End dates at the top of this window.";

    /// <summary>The line logged before a step: counts and the report name, no patient data.</summary>
    public static string DescribeStep(DailyReportStep step) =>
        $"[{step.Index}/{step.Total}] {DisplayName(step.Kind)} for {DateText(step.Day)}";
}
