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

    /// <summary>
    /// The POS Summary navigation (how the "Search Drawers" screen is
    /// reached and what <ALT><TAB><TAB> does there) is not settled by the
    /// macro, so POS steps are refused up front rather than guessed. Flip to
    /// true only after Will's answers are built into PioneerReportDriver.
    /// </summary>
    public const bool PosNavigationEnabled = false;

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

    /// <summary>Null when every step can run; otherwise why the whole run is refused before anything is sent to Pioneer.</summary>
    public static string? CheckRunnable(IReadOnlyList<DailyReportStep> steps)
    {
        if (steps.Count == 0) return "Nothing to run.";
        if (!PosNavigationEnabled)
        {
            foreach (var step in steps)
            {
                if (step.Kind == DailyReportKind.Pos)
                {
                    return "POS Summary is not available yet: how to reach its screen is still being confirmed. Nothing was sent to Pioneer.";
                }
            }
        }

        return null;
    }

    /// <summary>The line logged before a step: counts and the report name, no patient data.</summary>
    public static string DescribeStep(DailyReportStep step) =>
        $"[{step.Index}/{step.Total}] {DisplayName(step.Kind)} for {DateText(step.Day)}";
}
