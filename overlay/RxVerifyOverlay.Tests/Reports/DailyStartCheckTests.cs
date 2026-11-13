using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>W-T98: the daily run never silently does nothing when Search Drawers is not in front, and the day range is the one the user set.</summary>
public class DailyStartCheckTests
{
    private static readonly string Folder = Path.Combine("out", "2026-10");
    private const int Pid = 1234;

    private static ReportsCoordinator Coordinator(FakeReportDriver driver) =>
        new(driver, appLog: _ => { }, runLog: (_, _, _) => { });

    private static SaveAsCandidate W(string title, long handle, string cls = "WindowsForms10.Window.8.app.0", int pid = Pid, bool visible = true) =>
        new(title, cls, pid, new IntPtr(handle), IntPtr.Zero, 0, visible);

    [Fact]
    public async Task PosTickedAndSearchDrawersClosedAsksTheUserAndSendsNothing()
    {
        var driver = new FakeReportDriver { SearchDrawersOpen = false };
        var steps = DailySalesPlan.Build(new DateTime(2026, 10, 1), new DateTime(2026, 10, 3), Folder, true, true);
        var log = new List<string>();

        var summary = await Coordinator(driver).RunDailyAsync(steps, log.Add, CancellationToken.None);

        Assert.True(summary.NeedsSearchDrawers);
        Assert.Equal(1, driver.SearchDrawersChecks);
        Assert.Empty(driver.Items);                         // not one report was started
        Assert.Equal(0, summary.Saved);
        Assert.Null(summary.FailedStep);                    // not counted as a failure
        Assert.Null(summary.FailureReason);
        Assert.False(summary.Stopped);
        Assert.DoesNotContain(log, l => l.Contains("FAILED"));
        Assert.Contains(log, l => l.Contains("Open Search Drawers, then click Run daily again") && l.Contains("not counted as a failure"));
    }

    [Fact]
    public async Task PosTickedAndSearchDrawersOpenRunsAsBefore()
    {
        var driver = new FakeReportDriver { SearchDrawersOpen = true };
        var steps = DailySalesPlan.Build(new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), Folder, true, true);

        var summary = await Coordinator(driver).RunDailyAsync(steps, _ => { }, CancellationToken.None);

        Assert.False(summary.NeedsSearchDrawers);
        Assert.Equal(1, driver.SearchDrawersChecks);
        Assert.Equal(4, summary.Saved);
    }

    [Fact]
    public async Task SalesOnlyNeverAsksAboutSearchDrawers()
    {
        var driver = new FakeReportDriver { SearchDrawersOpen = false };
        var steps = DailySalesPlan.Build(new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), Folder, false, true);

        var summary = await Coordinator(driver).RunDailyAsync(steps, _ => { }, CancellationToken.None);

        Assert.False(summary.NeedsSearchDrawers);
        Assert.Equal(0, driver.SearchDrawersChecks);
        Assert.Equal(2, summary.Saved);
    }

    [Fact]
    public async Task NoPioneerWindowIsReportedBeforeTheSearchDrawersCheck()
    {
        var driver = new FakeReportDriver { MainWindowFound = false, SearchDrawersOpen = false };
        var steps = DailySalesPlan.Build(new DateTime(2026, 10, 1), new DateTime(2026, 10, 1), Folder, true, false);

        var summary = await Coordinator(driver).RunDailyAsync(steps, _ => { }, CancellationToken.None);

        Assert.False(summary.NeedsSearchDrawers);
        Assert.Equal(0, driver.SearchDrawersChecks);
        Assert.Equal("Could not find the main PioneerRx window - is Pioneer open?", summary.FailureReason);
    }

    [Fact]
    public void TheDialogTextIsTheOneWillAskedFor()
    {
        Assert.Equal("Open Pioneer's Search Drawers screen, then click Run daily.", PosSummaryPlan.OpenSearchDrawersFirstMessage);
    }

    [Fact]
    public void TheDetectionPassesWhenSearchDrawersIsOpenAmongOtherWindows()
    {
        var all = new List<SaveAsCandidate>
        {
            W("Pioneer", 1),
            W("Search Drawers", 2),
            W("Reports - Rx Verify", 3, "Window", pid: 999)
        };

        Assert.NotNull(PosSummaryPlan.FindSearchDrawers(all, Pid, new IntPtr(2)));
    }

    [Fact]
    public void TheDetectionFailsWhenOnlyTheMainWindowAndOtherProcessesAreThere()
    {
        // Shape of Will's 2026-11-12 log: a Pioneer main window with a 10-character title and a
        // 14-character title in a different (Rx Verify) process - that one must not count.
        var all = new List<SaveAsCandidate>
        {
            W("Pioneer12ab", 1),
            W("0123456789abcd", 2, "Window", pid: 999)
        };

        Assert.Null(PosSummaryPlan.FindSearchDrawers(all, Pid, new IntPtr(1)));
    }

    [Fact]
    public void TheWindowDescriptionHasClassesAndTitleLengthsButNeverATitle()
    {
        var all = new List<SaveAsCandidate>
        {
            W("Secret Title One", 1),
            W("Another Secret", 2, "WindowsForms10.Window.8.app.0.387ad33_r7_ad1"),
            W("Other Process Window", 3, "Window", pid: 999),
            W("Hidden", 4, visible: false)
        };

        var line = PosSummaryPlan.DescribePioneerWindows(all, Pid, new IntPtr(1));

        Assert.Contains("title_len=16", line);
        Assert.Contains("title_len=14", line);
        Assert.Contains("(foreground)", line);
        Assert.Contains("387ad33", line);
        Assert.DoesNotContain("Secret", line);
        Assert.DoesNotContain("Another", line);
        Assert.DoesNotContain("Other Process", line);
        Assert.DoesNotContain("Hidden", line);
        Assert.DoesNotContain("\n", line);
    }

    [Fact]
    public void TheWindowDescriptionSaysSoWhenPioneerHasNoVisibleWindow()
    {
        var line = PosSummaryPlan.DescribePioneerWindows(new List<SaveAsCandidate> { W("x", 1, pid: 5) }, Pid, IntPtr.Zero);

        Assert.Contains("no visible Pioneer window", line);
    }

    // ---- the day range (W-T98: 31 days / 62 reports came from the default, not from "3 days") ----

    [Fact]
    public void TheDefaultRangeIsLastCalendarMonthSoTwoReportsAreSixtyTwoOverThirtyOneDays()
    {
        var today = new DateTime(2026, 11, 12);
        var begin = ReportRunPlan.DefaultBegin(today);
        var end = ReportRunPlan.DefaultEnd(today);

        Assert.Equal(new DateTime(2026, 10, 1), begin);
        Assert.Equal(new DateTime(2026, 10, 31), end);
        Assert.Equal(31, DailySalesPlan.DayCount(begin, end));
        Assert.Equal(62, DailySalesPlan.Build(begin, end, Folder, true, true).Count);
    }

    [Fact]
    public void AThreeDayRangeRunsThreeDaysPerReportAndNeedsNoConfirmation()
    {
        var steps = DailySalesPlan.Build(new DateTime(2026, 10, 1), new DateTime(2026, 10, 3), Folder, true, true);

        Assert.Equal(6, steps.Count);
        Assert.Equal(3, DailySalesPlan.DayCount(new DateTime(2026, 10, 1), new DateTime(2026, 10, 3)));
        Assert.False(DailySalesPlan.NeedsRangeConfirmation(3));
        Assert.False(DailySalesPlan.NeedsRangeConfirmation(DailySalesPlan.ConfirmAboveDays));
        Assert.True(DailySalesPlan.NeedsRangeConfirmation(DailySalesPlan.ConfirmAboveDays + 1));
        Assert.True(DailySalesPlan.NeedsRangeConfirmation(31));
    }

    [Fact]
    public void TheRangeDescriptionAndQuestionNameBothDatesAndTheCounts()
    {
        var begin = new DateTime(2026, 10, 1);
        var end = new DateTime(2026, 10, 31);

        Assert.Equal("62 report(s) over 31 day(s), 10-01-26 to 10-31-26", DailySalesPlan.DescribeRange(begin, end, 62));
        var question = DailySalesPlan.RangeConfirmationText(begin, end, 62);
        Assert.Contains("62 report(s) over 31 day(s), 10-01-26 to 10-31-26", question);
        Assert.Contains("change the Begin and End dates", question);
    }
}
