using System;
using System.IO;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>Unit tests for Reports/ReportRunPlan.cs — default date range, filename, and output-folder math (no filesystem I/O in the class itself, so none needed here either).</summary>
public class ReportRunPlanTests
{
    [Fact]
    public void DefaultRangeIsLastCompleteCalendarMonth_MidMonthToday()
    {
        var today = new DateTime(2026, 9, 16);

        var begin = ReportRunPlan.DefaultBegin(today);
        var end = ReportRunPlan.DefaultEnd(today);

        Assert.Equal(new DateTime(2026, 8, 1), begin);
        Assert.Equal(new DateTime(2026, 8, 31), end);
    }

    [Fact]
    public void DefaultRangeHandlesYearRollover_JanuaryToday()
    {
        var today = new DateTime(2026, 1, 15);

        var begin = ReportRunPlan.DefaultBegin(today);
        var end = ReportRunPlan.DefaultEnd(today);

        Assert.Equal(new DateTime(2025, 12, 1), begin);
        Assert.Equal(new DateTime(2025, 12, 31), end);
    }

    [Fact]
    public void DefaultRangeHandlesLeapFebruary()
    {
        // 2028 is a leap year - last complete month before March 2028 is
        // February 2028, which has 29 days.
        var today = new DateTime(2028, 3, 5);

        var begin = ReportRunPlan.DefaultBegin(today);
        var end = ReportRunPlan.DefaultEnd(today);

        Assert.Equal(new DateTime(2028, 2, 1), begin);
        Assert.Equal(new DateTime(2028, 2, 29), end);
    }

    [Fact]
    public void DefaultRangeHandlesNonLeapFebruary()
    {
        var today = new DateTime(2027, 3, 5);

        var end = ReportRunPlan.DefaultEnd(today);

        Assert.Equal(new DateTime(2027, 2, 28), end);
    }

    [Fact]
    public void DefaultRangeFromFirstOfMonthStillUsesPriorMonth()
    {
        // Edge case: "today" is the 1st itself - the last COMPLETE month
        // is still the prior one, not the one that just started.
        var today = new DateTime(2026, 5, 1);

        var begin = ReportRunPlan.DefaultBegin(today);
        var end = ReportRunPlan.DefaultEnd(today);

        Assert.Equal(new DateTime(2026, 4, 1), begin);
        Assert.Equal(new DateTime(2026, 4, 30), end);
    }

    [Fact]
    public void FileNameDateIsAlwaysTheEndDate()
    {
        var begin = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 31);

        Assert.Equal(end, ReportRunPlan.FileNameDate(ReportParameterKind.DateRange, begin, end));
        Assert.Equal(end, ReportRunPlan.FileNameDate(ReportParameterKind.AsOfDate, begin, end));
    }

    [Fact]
    public void BuildFileNameMatchesWillsConvention()
    {
        var entry = ReportCatalog.FindByKey(ReportCatalog.SalesSummaryKey)!;
        var begin = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 31);

        var fileName = ReportRunPlan.BuildFileName(entry, begin, end);

        Assert.Equal("08-31-26_Sales Summary.pdf", fileName);
    }

    [Fact]
    public void BuildFileNameUsesXlsxExtensionForPayments()
    {
        var entry = ReportCatalog.FindByKey(ReportCatalog.PaymentsKey)!;
        var begin = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 31);

        var fileName = ReportRunPlan.BuildFileName(entry, begin, end);

        Assert.EndsWith(".xlsx", fileName);
    }

    [Fact]
    public void DefaultOutputFolderIsUnderDocumentsPioneerReportsNamedByEndMonth()
    {
        var end = new DateTime(2026, 8, 31);
        var expectedDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var folder = ReportRunPlan.DefaultOutputFolder(end);

        Assert.Equal(Path.Combine(expectedDocuments, "Pioneer Reports", "2026-08"), folder);
    }

    [Fact]
    public void BuildComposesEntryDatesAndFullPath()
    {
        var entry = ReportCatalog.FindByKey(ReportCatalog.InventoryValuationKey)!;
        var begin = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 31);
        var folder = Path.Combine("C:", "Reports", "2026-08");

        var item = ReportRunPlan.Build(entry, begin, end, folder);

        Assert.Equal(entry, item.Entry);
        Assert.Equal(begin.Date, item.Begin);
        Assert.Equal(end.Date, item.End);
        Assert.Equal(folder, item.OutputFolder);
        Assert.Equal(Path.Combine(folder, "08-31-26_Inventory Valuation.pdf"), item.OutputFilePath);
    }
}
