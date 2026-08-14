using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for RunOnce (Integrated/RunOnce.cs) — the pure "at most
/// once" guarantee behind MainWindow's Window.DpiChanged handling for
/// ReportErrorWindow's positioning. Review finding this closes: an
/// unguarded DpiChanged handler re-ran the dialog's positioning on EVERY
/// subsequent DPI change, snapping it back toward the original
/// right-click location if Will dragged the OPEN dialog across a DPI
/// boundary himself — see MainWindow.xaml.cs OpenReportErrorDialog.
/// </summary>
public class RunOnceTests
{
    [Fact]
    public void FiresTheWrappedActionOnTheFirstCall()
    {
        var callCount = 0;
        var runOnce = new RunOnce(() => callCount++);

        runOnce.Fire();

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void NeverFiresAgainOnSubsequentCalls()
    {
        var callCount = 0;
        var runOnce = new RunOnce(() => callCount++);

        runOnce.Fire();
        runOnce.Fire();
        runOnce.Fire();

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void TwoSeparateRunOnceInstancesDoNotShareState()
    {
        // Each ReportErrorWindow open gets its OWN RunOnce (constructed
        // fresh in OpenReportErrorDialog) — guards against a static/shared
        // instance bug that would silently suppress positioning for every
        // report after the first.
        var firstCount = 0;
        var secondCount = 0;
        var first = new RunOnce(() => firstCount++);
        var second = new RunOnce(() => secondCount++);

        first.Fire();
        first.Fire();
        second.Fire();

        Assert.Equal(1, firstCount);
        Assert.Equal(1, secondCount);
    }
}
