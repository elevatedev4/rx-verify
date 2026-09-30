using System;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>
/// Unit tests for Reports/ReportCompletionStatus.cs — pure status-text/
/// elapsed-time decision logic, no UIA/FlaUI/WPF involved
/// (PioneerReportDriver.WaitForReportCompletionStatus is the
/// untestable-on-this-Mac poll loop that actually reads Pioneer's status
/// text and feeds it through Decide).
/// </summary>
public class ReportCompletionStatusTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    // --- IsCompleted / IsGenerating ---

    [Theory]
    [InlineData("The report has completed")]
    [InlineData("the report has completed")]
    [InlineData("THE REPORT HAS COMPLETED")]
    [InlineData("  The report has completed.  ")]
    [InlineData("Status: The report has completed (100%)")]
    public void IsCompletedMatchesCaseInsensitiveContains(string statusText)
    {
        Assert.True(ReportCompletionStatus.IsCompleted(statusText));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Please wait while the report is generated...")]
    [InlineData("Something else entirely")]
    public void IsCompletedIsFalseForAnythingElse(string? statusText)
    {
        Assert.False(ReportCompletionStatus.IsCompleted(statusText));
    }

    [Theory]
    [InlineData("Please wait while the report is generated...")]
    [InlineData("PLEASE WAIT WHILE THE REPORT IS GENERATED")]
    [InlineData("please wait while the report is generated (42%)")]
    public void IsGeneratingMatchesCaseInsensitiveContains(string statusText)
    {
        Assert.True(ReportCompletionStatus.IsGenerating(statusText));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("The report has completed")]
    public void IsGeneratingIsFalseForAnythingElse(string? statusText)
    {
        Assert.False(ReportCompletionStatus.IsGenerating(statusText));
    }

    // --- Decide ---

    [Fact]
    public void DecideProceedsAsSoonAsStatusTextReadsCompleted()
    {
        var decision = ReportCompletionStatus.Decide("The report has completed", TimeSpan.FromSeconds(1), Timeout);

        Assert.Equal(ReportCompletionDecision.Proceed, decision);
    }

    [Fact]
    public void DecideProceedsEvenAtOrPastTheTimeoutIfStatusTextReadsCompleted()
    {
        // A slow final poll tick that happens to land exactly on/after the
        // timeout should still proceed if the text genuinely says
        // completed - "completed" always wins over "timed out".
        var decision = ReportCompletionStatus.Decide("The report has completed", Timeout, Timeout);

        Assert.Equal(ReportCompletionDecision.Proceed, decision);
    }

    [Fact]
    public void DecideKeepsWaitingWhileStatusTextReadsGenerating()
    {
        var decision = ReportCompletionStatus.Decide(
            "Please wait while the report is generated...",
            TimeSpan.FromSeconds(30),
            Timeout);

        Assert.Equal(ReportCompletionDecision.KeepWaiting, decision);
    }

    [Fact]
    public void DecideKeepsWaitingWhenStatusTextIsNullAndTimeoutNotYetReached()
    {
        // The status element not being found (yet) is never itself treated
        // as "done" - only ever "keep waiting" until the timeout actually
        // elapses (see PioneerReportDriver's diagnostic-dump-once handling
        // of this exact case).
        var decision = ReportCompletionStatus.Decide(null, TimeSpan.FromSeconds(5), Timeout);

        Assert.Equal(ReportCompletionDecision.KeepWaiting, decision);
    }

    [Fact]
    public void DecideTimesOutWhenElapsedReachesTimeoutWithoutCompleting()
    {
        var decision = ReportCompletionStatus.Decide(
            "Please wait while the report is generated...",
            Timeout,
            Timeout);

        Assert.Equal(ReportCompletionDecision.TimedOut, decision);
    }

    [Fact]
    public void DecideTimesOutWhenElapsedExceedsTimeoutAndStatusTextWasNeverFound()
    {
        var decision = ReportCompletionStatus.Decide(null, Timeout + TimeSpan.FromSeconds(1), Timeout);

        Assert.Equal(ReportCompletionDecision.TimedOut, decision);
    }

    [Fact]
    public void DecideTimesOutWhenStatusTextIsSomethingUnrecognizedAndTimeoutReached()
    {
        var decision = ReportCompletionStatus.Decide("Something else entirely", Timeout, Timeout);

        Assert.Equal(ReportCompletionDecision.TimedOut, decision);
    }
}
