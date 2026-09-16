using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>
/// Unit tests for Reports/ReportsCoordinator.cs sequencing policy, using
/// FakeReportDriver so no FlaUI/UIA/Windows/real filesystem is involved
/// — appLog/runLog are injected as no-ops (or capturing lambdas) per the
/// constructor's own test-seam parameters.
/// </summary>
public class ReportsCoordinatorTests
{
    private static ReportRunItem MakeItem(string key)
    {
        var entry = ReportCatalog.FindByKey(key)!;
        return ReportRunPlan.Build(entry, new DateTime(2026, 8, 1), new DateTime(2026, 8, 31), Path.Combine("out", "2026-08"));
    }

    private static ReportsCoordinator MakeCoordinator(IPioneerReportDriver driver, List<string>? runLogLines = null)
    {
        return new ReportsCoordinator(
            driver,
            appLog: _ => { },
            runLog: (_, _, line) => runLogLines?.Add(line));
    }

    [Fact]
    public async Task AllSucceed_ReturnsSavedForEveryItem_InOrder()
    {
        var driver = new FakeReportDriver();
        var coordinator = MakeCoordinator(driver);
        var items = new[] { MakeItem(ReportCatalog.SalesSummaryKey), MakeItem(ReportCatalog.InventoryValuationKey) };
        var log = new List<string>();

        var outcomes = await coordinator.RunAsync(items, log.Add, CancellationToken.None);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal(ReportRunStatus.Saved, o.Status));
        Assert.Equal(new[] { "FindMainWindow", ReportCatalog.SalesSummaryKey, ReportCatalog.InventoryValuationKey }, driver.Calls);
    }

    [Fact]
    public async Task OneFailure_DoesNotStopTheBatch()
    {
        var driver = new FakeReportDriver();
        driver.Results[ReportCatalog.SalesSummaryKey] = ReportRunResult.Failed("Pioneer lost focus", TimeSpan.Zero);
        var coordinator = MakeCoordinator(driver);
        var items = new[]
        {
            MakeItem(ReportCatalog.SalesSummaryKey),
            MakeItem(ReportCatalog.InventoryValuationKey),
            MakeItem(ReportCatalog.InventoryControlBalanceKey)
        };

        var outcomes = await coordinator.RunAsync(items, _ => { }, CancellationToken.None);

        Assert.Equal(ReportRunStatus.Failed, outcomes[0].Status);
        Assert.Equal("Pioneer lost focus", outcomes[0].Detail);
        Assert.Equal(ReportRunStatus.Saved, outcomes[1].Status);
        Assert.Equal(ReportRunStatus.Saved, outcomes[2].Status);
        // All three were still attempted despite the first failing.
        Assert.Equal(3, driver.Calls.Count - 1);
    }

    [Fact]
    public async Task ThrownExceptionIsTreatedAsFailureAndBatchContinues()
    {
        var driver = new FakeReportDriver();
        driver.ThrowFor[ReportCatalog.SalesSummaryKey] = new InvalidOperationException("row not found");
        var coordinator = MakeCoordinator(driver);
        var items = new[] { MakeItem(ReportCatalog.SalesSummaryKey), MakeItem(ReportCatalog.InventoryValuationKey) };

        var outcomes = await coordinator.RunAsync(items, _ => { }, CancellationToken.None);

        Assert.Equal(ReportRunStatus.Failed, outcomes[0].Status);
        Assert.Contains("row not found", outcomes[0].Detail);
        Assert.Equal(ReportRunStatus.Saved, outcomes[1].Status);
    }

    [Fact]
    public async Task CancelBeforeStart_LeavesEveryItemPending()
    {
        var driver = new FakeReportDriver();
        var coordinator = MakeCoordinator(driver);
        var items = new[] { MakeItem(ReportCatalog.SalesSummaryKey), MakeItem(ReportCatalog.InventoryValuationKey) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcomes = await coordinator.RunAsync(items, _ => { }, cts.Token);

        Assert.All(outcomes, o => Assert.Equal(ReportRunStatus.Pending, o.Status));
        // FindMainWindow still runs once (batch policy checks Pioneer first), but no report is attempted.
        Assert.Equal(new[] { "FindMainWindow" }, driver.Calls);
    }

    [Fact]
    public async Task CancellationDuringAReport_StopsRemainingItemsAsPending()
    {
        var driver = new FakeReportDriver();
        driver.ThrowCancelledFor.Add(ReportCatalog.SalesSummaryKey);
        var coordinator = MakeCoordinator(driver);
        var items = new[]
        {
            MakeItem(ReportCatalog.SalesSummaryKey),
            MakeItem(ReportCatalog.InventoryValuationKey)
        };

        var outcomes = await coordinator.RunAsync(items, _ => { }, CancellationToken.None);

        Assert.Equal(ReportRunStatus.Failed, outcomes[0].Status);
        Assert.Equal("Stopped", outcomes[0].Detail);
        Assert.Equal(ReportRunStatus.Pending, outcomes[1].Status);
        // The second report's key was never called on the driver.
        Assert.DoesNotContain(ReportCatalog.InventoryValuationKey, driver.Calls);
    }

    [Fact]
    public async Task MainWindowNotFound_FailsEveryItemWithoutAttemptingAny()
    {
        var driver = new FakeReportDriver { MainWindowFound = false };
        var coordinator = MakeCoordinator(driver);
        var items = new[] { MakeItem(ReportCatalog.SalesSummaryKey), MakeItem(ReportCatalog.InventoryValuationKey) };

        var outcomes = await coordinator.RunAsync(items, _ => { }, CancellationToken.None);

        Assert.All(outcomes, o => Assert.Equal(ReportRunStatus.Failed, o.Status));
        Assert.All(outcomes, o => Assert.Equal("PioneerRx main window not found", o.Detail));
        Assert.Equal(new[] { "FindMainWindow" }, driver.Calls);
    }

    [Fact]
    public async Task EmptyItemList_ReturnsEmptyOutcomesWithoutTouchingDriver()
    {
        var driver = new FakeReportDriver();
        var coordinator = MakeCoordinator(driver);

        var outcomes = await coordinator.RunAsync(Array.Empty<ReportRunItem>(), _ => { }, CancellationToken.None);

        Assert.Empty(outcomes);
        Assert.Empty(driver.Calls);
    }

    [Fact]
    public async Task ProgressChangedFiresRunningThenTerminalStatusPerItem()
    {
        var driver = new FakeReportDriver();
        var coordinator = MakeCoordinator(driver);
        var statuses = new List<ReportRunStatus>();
        coordinator.ProgressChanged += (_, e) => statuses.Add(e.Outcome.Status);
        var items = new[] { MakeItem(ReportCatalog.SalesSummaryKey) };

        await coordinator.RunAsync(items, _ => { }, CancellationToken.None);

        Assert.Equal(new[] { ReportRunStatus.Running, ReportRunStatus.Saved }, statuses);
    }

    [Fact]
    public async Task WritesARunSummaryLineForEachAttemptedReport()
    {
        var driver = new FakeReportDriver();
        var runLogLines = new List<string>();
        var coordinator = MakeCoordinator(driver, runLogLines);
        var items = new[] { MakeItem(ReportCatalog.SalesSummaryKey) };

        await coordinator.RunAsync(items, _ => { }, CancellationToken.None);

        Assert.Contains(runLogLines, l => l.Contains("saved to"));
    }
}
