using System;
using System.Collections.Generic;
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

    // --- Decide(statusText, completedIsTrusted, elapsed, timeout) —
    // reviewer round 7 re-review BLOCKING fix: PioneerReportDriver's
    // _mainWindow fallback is resolved once per BATCH, not refreshed per
    // report, so a Completed reading reached only through that fallback
    // must not be trusted until a Generating reading has actually been
    // observed THIS run (see PioneerReportDriver.WaitForReportCompletionStatus's
    // own doc for the full story). These four cases are exactly the ones
    // the reviewer asked for. ---

    [Fact]
    public void DecideKeepsWaitingOnFallbackCompletedWithoutGeneratingObservedYet()
    {
        // fallback-completed without generating -> KeepWaiting.
        var decision = ReportCompletionStatus.Decide(
            "The report has completed",
            completedIsTrusted: false,
            TimeSpan.FromSeconds(5),
            Timeout);

        Assert.Equal(ReportCompletionDecision.KeepWaiting, decision);
    }

    [Fact]
    public void DecideProceedsOnFallbackCompletedAfterGeneratingWasObserved()
    {
        // fallback-completed after generating -> Proceed. Once the caller
        // has seen a Generating reading this run, it passes
        // completedIsTrusted: true for the fallback match too.
        var decision = ReportCompletionStatus.Decide(
            "The report has completed",
            completedIsTrusted: true,
            TimeSpan.FromSeconds(5),
            Timeout);

        Assert.Equal(ReportCompletionDecision.Proceed, decision);
    }

    [Fact]
    public void DecideProceedsOnPreviewCompletedWithoutGeneratingObserved()
    {
        // preview-completed without generating -> Proceed. A Completed
        // reading from the report's OWN preview window is always trusted
        // (completedIsTrusted: true), independent of whether a Generating
        // reading happened to be observed first - the preview window is
        // never a stale leftover from a prior report.
        var decision = ReportCompletionStatus.Decide(
            "The report has completed",
            completedIsTrusted: true,
            TimeSpan.FromSeconds(1),
            Timeout);

        Assert.Equal(ReportCompletionDecision.Proceed, decision);
    }

    [Fact]
    public void DecideTimesOutOnFallbackCompletedWithoutGeneratingObservedPastTimeout()
    {
        // fallback-completed without generating, past timeout -> TimedOut.
        // An untrusted Completed reading never overrides the timeout -
        // it's treated exactly like any other non-completed reading.
        var decision = ReportCompletionStatus.Decide(
            "The report has completed",
            completedIsTrusted: false,
            Timeout,
            Timeout);

        Assert.Equal(ReportCompletionDecision.TimedOut, decision);
    }

    // --- SelectStatusText (reviewer round 7 non-blocking fix: prefer
    // "completed" over "generating" regardless of UIA enumeration order) ---

    [Fact]
    public void SelectStatusTextPrefersCompletedEvenWhenGeneratingCameFirst()
    {
        var candidates = new[]
        {
            "Please wait while the report is generated...",
            "The report has completed",
        };

        var selected = ReportCompletionStatus.SelectStatusText(candidates);

        Assert.Equal("The report has completed", selected);
    }

    [Fact]
    public void SelectStatusTextPrefersCompletedWhenItCameFirst()
    {
        var candidates = new[]
        {
            "The report has completed",
            "Please wait while the report is generated...",
        };

        var selected = ReportCompletionStatus.SelectStatusText(candidates);

        Assert.Equal("The report has completed", selected);
    }

    [Fact]
    public void SelectStatusTextFallsBackToGeneratingWhenNoCompletedCandidateExists()
    {
        var candidates = new[]
        {
            "Something unrelated",
            "Please wait while the report is generated...",
        };

        var selected = ReportCompletionStatus.SelectStatusText(candidates);

        Assert.Equal("Please wait while the report is generated...", selected);
    }

    [Fact]
    public void SelectStatusTextReturnsNullWhenNoCandidateMatchesEitherPhrase()
    {
        var candidates = new[] { "Something unrelated", "", null };

        var selected = ReportCompletionStatus.SelectStatusText(candidates);

        Assert.Null(selected);
    }

    [Fact]
    public void SelectStatusTextReturnsNullForAnEmptyCandidateList()
    {
        var selected = ReportCompletionStatus.SelectStatusText(Array.Empty<string?>());

        Assert.Null(selected);
    }

    [Fact]
    public void SelectStatusTextIgnoresNullAndEmptyCandidatesMixedInWithRealOnes()
    {
        var candidates = new List<string?> { null, "", "Please wait while the report is generated..." };

        var selected = ReportCompletionStatus.SelectStatusText(candidates);

        Assert.Equal("Please wait while the report is generated...", selected);
    }
}
