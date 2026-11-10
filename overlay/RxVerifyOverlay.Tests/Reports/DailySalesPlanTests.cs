using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>"Daily Sales + POS": date loop, file names, step order (DailySalesPlan) and the stop-at-first-failure run policy (ReportsCoordinator.RunDailyAsync).</summary>
public class DailySalesPlanTests
{
    private static readonly string Folder = Path.Combine("out", "2026-10");

    public class DateLoopTests
    {
        [Fact]
        public void EnumeratesEveryDayInclusive()
        {
            var days = DailySalesPlan.EnumerateDays(new DateTime(2026, 10, 1), new DateTime(2026, 10, 31));

            Assert.Equal(31, days.Count);
            Assert.Equal(new DateTime(2026, 10, 1), days[0]);
            Assert.Equal(new DateTime(2026, 10, 31), days[^1]);
        }

        [Fact]
        public void ASingleDayRangeIsOneDay()
        {
            Assert.Single(DailySalesPlan.EnumerateDays(new DateTime(2026, 10, 5, 14, 30, 0), new DateTime(2026, 10, 5)));
        }

        [Fact]
        public void CrossesMonthAndLeapYearBoundaries()
        {
            var days = DailySalesPlan.EnumerateDays(new DateTime(2028, 2, 27), new DateTime(2028, 3, 2));

            Assert.Equal(5, days.Count);
            Assert.Equal(new DateTime(2028, 2, 29), days[2]);
        }

        [Fact]
        public void DefaultRangeIsTheLastCompleteMonth()
        {
            var today = new DateTime(2026, 11, 10);

            Assert.Equal(new DateTime(2026, 10, 1), ReportRunPlan.DefaultBegin(today));
            Assert.Equal(new DateTime(2026, 10, 31), ReportRunPlan.DefaultEnd(today));
        }

        [Fact]
        public void RejectsAReversedRange()
        {
            Assert.False(DailySalesPlan.TryValidateRange(new DateTime(2026, 10, 5), new DateTime(2026, 10, 4), out var problem));
            Assert.Contains("before", problem);
            Assert.Empty(DailySalesPlan.Build(new DateTime(2026, 10, 5), new DateTime(2026, 10, 4), Folder, true, true));
        }

        [Fact]
        public void RejectsMoreThanTheDayLimitButAcceptsExactlyTheLimit()
        {
            var begin = new DateTime(2026, 1, 1);

            Assert.True(DailySalesPlan.TryValidateRange(begin, begin.AddDays(DailySalesPlan.MaxDays - 1), out _));
            Assert.False(DailySalesPlan.TryValidateRange(begin, begin.AddDays(DailySalesPlan.MaxDays), out var problem));
            Assert.Contains("limit", problem);
        }
    }

    public class FileNameTests
    {
        [Theory]
        [InlineData(DailyReportKind.Pos, 2026, 10, 3, "POS_10-03-26")]
        [InlineData(DailyReportKind.Sales, 2026, 10, 31, "Sales_10-31-26")]
        [InlineData(DailyReportKind.Sales, 2027, 1, 1, "Sales_01-01-27")]
        public void BaseNameIsTheMacrosPrefixUnderscoreMmDdYy(DailyReportKind kind, int y, int m, int d, string expected)
        {
            Assert.Equal(expected, DailySalesPlan.BaseName(kind, new DateTime(y, m, d)));
            Assert.Equal(expected + ".pdf", DailySalesPlan.FileName(kind, new DateTime(y, m, d)));
        }

        [Fact]
        public void FileNamesAreCultureIndependent()
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
                Assert.Equal("POS_10-03-26", DailySalesPlan.BaseName(DailyReportKind.Pos, new DateTime(2026, 10, 3)));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void PathIsTheFolderPlusTheFileName()
        {
            Assert.Equal(Path.Combine(Folder, "Sales_10-03-26.pdf"), DailySalesPlan.FilePath(Folder, DailyReportKind.Sales, new DateTime(2026, 10, 3)));
        }

        [Fact]
        public void DailyNamesNeverCollideWithTheMonthlyReportNames()
        {
            var monthly = ReportRunPlan.BuildFileName(ReportCatalog.FindByKey(ReportCatalog.SalesSummaryKey)!, new DateTime(2026, 10, 1), new DateTime(2026, 10, 31));

            Assert.Equal("10-31-26_Sales Summary.pdf", monthly);
            Assert.NotEqual(monthly, DailySalesPlan.FileName(DailyReportKind.Sales, new DateTime(2026, 10, 31)));
        }
    }

    public class StepPlanTests
    {
        [Fact]
        public void FollowsTheMacroPassOrderAllPosDaysThenAllSalesDays()
        {
            var steps = DailySalesPlan.Build(new DateTime(2026, 10, 1), new DateTime(2026, 10, 3), Folder, includePos: true, includeSales: true);

            Assert.Equal(6, steps.Count);
            Assert.Equal(
                new[] { DailyReportKind.Pos, DailyReportKind.Pos, DailyReportKind.Pos, DailyReportKind.Sales, DailyReportKind.Sales, DailyReportKind.Sales },
                steps.Select(s => s.Kind));
            Assert.Equal(new[] { 1, 2, 3, 1, 2, 3 }, steps.Select(s => s.Day.Day));
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, steps.Select(s => s.Index));
            Assert.All(steps, s => Assert.Equal(6, s.Total));
        }

        [Fact]
        public void AKindThatIsNotWantedIsAbsent()
        {
            var salesOnly = DailySalesPlan.Build(new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), Folder, includePos: false, includeSales: true);

            Assert.Equal(2, salesOnly.Count);
            Assert.All(salesOnly, s => Assert.Equal(DailyReportKind.Sales, s.Kind));
            Assert.Equal(Path.Combine(Folder, "Sales_10-01-26.pdf"), salesOnly[0].OutputFilePath);
            Assert.Empty(DailySalesPlan.Build(new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), Folder, false, false));
        }

        [Fact]
        public void AFullMonthIsThirtyOneSalesSteps()
        {
            var steps = DailySalesPlan.Build(ReportRunPlan.DefaultBegin(new DateTime(2026, 11, 10)), ReportRunPlan.DefaultEnd(new DateTime(2026, 11, 10)), Folder, false, true);

            Assert.Equal(31, steps.Count);
            Assert.Equal("Sales_10-31-26.pdf", Path.GetFileName(steps[^1].OutputFilePath));
        }

        [Fact]
        public void APosStepIsRefusedBeforeAnythingRunsWhileItsNavigationIsUnconfirmed()
        {
            var steps = DailySalesPlan.Build(new DateTime(2026, 10, 1), new DateTime(2026, 10, 1), Folder, true, true);

            Assert.False(DailySalesPlan.PosNavigationEnabled);
            Assert.Contains("POS Summary is not available", DailySalesPlan.CheckRunnable(steps));
            Assert.Null(DailySalesPlan.CheckRunnable(DailySalesPlan.Build(new DateTime(2026, 10, 1), new DateTime(2026, 10, 1), Folder, false, true)));
            Assert.NotNull(DailySalesPlan.CheckRunnable(Array.Empty<DailyReportStep>()));
        }

        [Fact]
        public void StepDescriptionCarriesCountsReportAndDateOnly()
        {
            var step = DailySalesPlan.Build(new DateTime(2026, 10, 2), new DateTime(2026, 10, 2), Folder, false, true)[0];

            Assert.Equal("[1/1] Sales Summary for 10-02-26", DailySalesPlan.DescribeStep(step));
        }
    }

    public class AltEndFieldTests
    {
        private static readonly ReportCatalogEntry Sales = ReportCatalog.FindByKey(ReportCatalog.SalesSummaryKey)!;
        private static readonly DateTime Day = new(2026, 10, 3);

        [Fact]
        public void TheDailyPlanIsBeginAltEEndF12WithTheMonthlyTabsOnlyAsTheFallback()
        {
            var daily = Sales with { EndFieldAccelerator = DailySalesPlan.EndFieldAccelerator };

            var plan = ReportParameterKeyPlan.Build(daily, Day, Day);

            Assert.Equal(
                new[]
                {
                    ReportParameterKeyAction.TypeText("10-03-2026"),
                    ReportParameterKeyAction.AltKey("e", Sales.TabsBetweenDates),
                    ReportParameterKeyAction.TypeText("10-03-26"),
                    ReportParameterKeyAction.F12()
                },
                plan);
            Assert.DoesNotContain(plan, a => a.Kind == ReportParameterKeyActionKind.Tab);
        }

        [Fact]
        public void TheMonthlyPlanKeepsItsTabsAndHasNoAltKey()
        {
            var plan = ReportParameterKeyPlan.Build(Sales, new DateTime(2026, 10, 1), new DateTime(2026, 10, 31));

            Assert.DoesNotContain(plan, a => a.Kind == ReportParameterKeyActionKind.AltKey);
            Assert.Equal(Sales.TabsBetweenDates, plan.Count(a => a.Kind == ReportParameterKeyActionKind.Tab));
            Assert.Null(Sales.EndFieldAccelerator);
        }

        [Fact]
        public void NoF12InTheTestPlanButStillAltE()
        {
            var daily = Sales with { EndFieldAccelerator = "e" };

            var plan = ReportParameterKeyPlan.Build(daily, Day, Day, includeF12: false);

            Assert.DoesNotContain(plan, a => a.Kind == ReportParameterKeyActionKind.F12);
            Assert.Contains(plan, a => a.Kind == ReportParameterKeyActionKind.AltKey);
        }

        [Theory]
        [InlineData("a|r1", "b|r2", true)]        // moved to a different control
        [InlineData("a|r1", "a|r1", false)]       // same control, same place: Alt+e did nothing
        [InlineData("a|r1", "a|r2", true)]        // same description, different rectangle (two look-alike date fields)
        [InlineData(null, "a|r1", true)]
        [InlineData("a|r1", null, false)]         // nothing focused afterwards: not a landing
        [InlineData("a|r1", "", false)]
        public void FocusMoveDecisionDrivesTheTabFallback(string? before, string? after, bool moved)
        {
            Assert.Equal(moved, ReportFocusMove.Moved(before, after));
        }

        [Fact]
        public async Task EveryDailySalesRunUsesAltEForTheEndField()
        {
            var driver = new FakeReportDriver();
            var coordinator = new ReportsCoordinator(driver, appLog: _ => { }, runLog: (_, _, _) => { });
            var steps = DailySalesPlan.Build(new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), Folder, false, true);

            await coordinator.RunDailyAsync(steps, _ => { }, CancellationToken.None);

            Assert.Equal(2, driver.Items.Count);
            Assert.All(driver.Items, i => Assert.Equal("e", i.Entry.EndFieldAccelerator));
            Assert.Null(ReportCatalog.FindByKey(ReportCatalog.SalesSummaryKey)!.EndFieldAccelerator);
        }
    }

    public class RunDailyTests
    {
        private static ReportsCoordinator Coordinator(FakeReportDriver driver) =>
            new(driver, appLog: _ => { }, runLog: (_, _, _) => { });

        private static IReadOnlyList<DailyReportStep> Sales(int fromDay, int toDay) =>
            DailySalesPlan.Build(new DateTime(2026, 10, fromDay), new DateTime(2026, 10, toDay), Folder, false, true);

        [Fact]
        public async Task RunsEachDayWithBeginEqualEndAndTheMacroFileName()
        {
            var driver = new FakeReportDriver();
            var log = new List<string>();

            var summary = await Coordinator(driver).RunDailyAsync(Sales(1, 3), log.Add, CancellationToken.None);

            Assert.Equal(3, summary.Saved);
            Assert.Null(summary.FailedStep);
            Assert.Equal(new[] { "FindMainWindow", "sales-summary", "sales-summary", "sales-summary" }, driver.Calls);
            Assert.Equal(new[] { 1, 2, 3 }, driver.Items.Select(i => i.Begin.Day));
            Assert.All(driver.Items, i => Assert.Equal(i.Begin, i.End));
            Assert.Equal(
                new[] { "Sales_10-01-26.pdf", "Sales_10-02-26.pdf", "Sales_10-03-26.pdf" },
                driver.Items.Select(i => Path.GetFileName(i.OutputFilePath)));
            Assert.Contains(log, l => l.Contains("[2/3] Sales Summary for 10-02-26: OK"));
            Assert.Contains(log, l => l.Contains("Daily run finished: 3 of 3 saved."));
        }

        [Fact]
        public async Task AFailureStopsTheRunAtThatDayAndNamesIt()
        {
            var driver = new FakeReportDriver();
            driver.ResultsByFileName["Sales_10-02-26.pdf"] = ReportRunResult.Failed("Save As window not recognised - stopped without typing", TimeSpan.Zero);
            var log = new List<string>();

            var summary = await Coordinator(driver).RunDailyAsync(Sales(1, 5), log.Add, CancellationToken.None);

            Assert.Equal(1, summary.Saved);
            Assert.Equal(5, summary.Total);
            Assert.Equal(new DateTime(2026, 10, 2), summary.FailedStep!.Day);
            Assert.Equal(2, driver.Items.Count);   // day 3..5 were never attempted
            Assert.Contains(log, l => l.Contains("[2/5] Sales Summary for 10-02-26: FAILED - Save As window not recognised"));
            Assert.Contains(log, l => l.Contains("stopped at Sales Summary for 10-02-26: 1 of 5 saved; the remaining 3 were not run."));
        }

        [Fact]
        public async Task AThrownExceptionAlsoStopsTheRun()
        {
            var driver = new FakeReportDriver();
            driver.ThrowFor[ReportCatalog.SalesSummaryKey] = new InvalidOperationException("boom");

            var summary = await Coordinator(driver).RunDailyAsync(Sales(1, 3), _ => { }, CancellationToken.None);

            Assert.Equal(0, summary.Saved);
            Assert.Equal("boom", summary.FailureReason);
            Assert.Single(driver.Items);
        }

        [Fact]
        public async Task StopBeforeAStepRunsNoFurtherDays()
        {
            var driver = new FakeReportDriver();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var summary = await Coordinator(driver).RunDailyAsync(Sales(1, 3), _ => { }, cts.Token);

            Assert.True(summary.Stopped);
            Assert.Equal(0, summary.Saved);
            Assert.Empty(driver.Items);
        }

        [Fact]
        public async Task CancellationInsideAReportStopsWithoutCountingItAsFailed()
        {
            var driver = new FakeReportDriver();
            driver.ThrowCancelledFor.Add(ReportCatalog.SalesSummaryKey);

            var summary = await Coordinator(driver).RunDailyAsync(Sales(1, 3), _ => { }, CancellationToken.None);

            Assert.True(summary.Stopped);
            Assert.Null(summary.FailedStep);
        }

        [Fact]
        public async Task PosStepsAreRefusedAndNothingIsSentToPioneer()
        {
            var driver = new FakeReportDriver();
            var steps = DailySalesPlan.Build(new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), Folder, true, true);
            var log = new List<string>();

            var summary = await Coordinator(driver).RunDailyAsync(steps, log.Add, CancellationToken.None);

            Assert.Equal(0, summary.Saved);
            Assert.Empty(driver.Calls);   // not even FindMainWindow
            Assert.Contains("POS Summary is not available", summary.FailureReason);
        }

        [Fact]
        public async Task NoPioneerWindowMeansNothingRuns()
        {
            var driver = new FakeReportDriver { MainWindowFound = false };

            var summary = await Coordinator(driver).RunDailyAsync(Sales(1, 2), _ => { }, CancellationToken.None);

            Assert.Equal(0, summary.Saved);
            Assert.Empty(driver.Items);
            Assert.Contains("main PioneerRx window", summary.FailureReason);
        }
    }
}
