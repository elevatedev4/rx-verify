using System;
using RxVerifyOverlay.Diagnostics;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Uia;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for AutoDiagnosticPolicy (Diagnostics/AutoDiagnosticPolicy.cs)
/// — the pure decision helper behind the automatic refills diagnostic
/// report (owner field report, 2026-09-15: "the refills box now shows NO
/// colour, neither red nor green"). All Rx numbers/values below are
/// synthetic.
/// </summary>
public class AutoDiagnosticPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    // ---- ClassifyRefillsBoxRenderState ----

    [Fact]
    public void ClassifyIsGreenWhenStatusIsGreenAndOnScreen()
    {
        var state = AutoDiagnosticPolicy.ClassifyRefillsBoxRenderState(VerdictStatus.Green, "exact_match", hasScreenRect: true);
        Assert.Equal(RefillsBoxRenderState.Green, state);
    }

    [Fact]
    public void ClassifyIsRedWhenStatusIsRedAndOnScreen()
    {
        var state = AutoDiagnosticPolicy.ClassifyRefillsBoxRenderState(VerdictStatus.Red, "refills_mismatch", hasScreenRect: true);
        Assert.Equal(RefillsBoxRenderState.Red, state);
    }

    [Fact]
    public void ClassifyIsNotProvidedForThatReasonCode()
    {
        var state = AutoDiagnosticPolicy.ClassifyRefillsBoxRenderState(VerdictStatus.Yellow, "not_provided", hasScreenRect: true);
        Assert.Equal(RefillsBoxRenderState.NotProvided, state);
    }

    [Fact]
    public void ClassifyIsUnparseableQuantityForThatReasonCode()
    {
        var state = AutoDiagnosticPolicy.ClassifyRefillsBoxRenderState(VerdictStatus.Yellow, "unparseable_quantity", hasScreenRect: true);
        Assert.Equal(RefillsBoxRenderState.UnparseableQuantity, state);
    }

    [Fact]
    public void ClassifyIsNotOnScreenWhenNoScreenRectRegardlessOfStatus()
    {
        // A row with nowhere to draw is NotOnScreen even for a Green
        // verdict — checked first, per the class doc, since a row missing
        // its rect never even reaches BoxColorMapper in the real pipeline.
        var state = AutoDiagnosticPolicy.ClassifyRefillsBoxRenderState(VerdictStatus.Green, "exact_match", hasScreenRect: false);
        Assert.Equal(RefillsBoxRenderState.NotOnScreen, state);
    }

    [Theory]
    [InlineData(RefillsBoxRenderState.NotProvided, true)]
    [InlineData(RefillsBoxRenderState.UnparseableQuantity, true)]
    [InlineData(RefillsBoxRenderState.NotOnScreen, true)]
    [InlineData(RefillsBoxRenderState.Green, false)]
    [InlineData(RefillsBoxRenderState.Red, false)]
    public void IsUncolouredMatchesTheThreeNoColorStates(RefillsBoxRenderState state, bool expected)
    {
        Assert.Equal(expected, AutoDiagnosticPolicy.IsUncoloured(state));
    }

    // ---- ShouldReport ----

    [Fact]
    public void UncolouredWithFillWordsReports()
    {
        var (shouldReport, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false,
            rateLimitState: null, nowUtc: Now, rxNumber: "RX-1001");

        Assert.True(shouldReport);
    }

    [Fact]
    public void ColouredNeverReportsEvenWithFillWords()
    {
        var (shouldReport, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.Green, hasFillWords: true, isApproval: true,
            rateLimitState: null, nowUtc: Now, rxNumber: "RX-1001");

        Assert.False(shouldReport);
    }

    [Fact]
    public void NoFillWordsAndNotApprovalNeverReports()
    {
        var (shouldReport, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: false, isApproval: false,
            rateLimitState: null, nowUtc: Now, rxNumber: "RX-1001");

        Assert.False(shouldReport);
    }

    [Fact]
    public void ApprovalAloneWithoutFillWordsStillReports()
    {
        var (shouldReport, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.UnparseableQuantity, hasFillWords: false, isApproval: true,
            rateLimitState: null, nowUtc: Now, rxNumber: "RX-1001");

        Assert.True(shouldReport);
    }

    [Fact]
    public void SameRxTwiceInADayReportsOnlyOnce()
    {
        var (firstShouldReport, stateAfterFirst) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false,
            rateLimitState: null, nowUtc: Now, rxNumber: "RX-1001");

        var (secondShouldReport, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false,
            rateLimitState: stateAfterFirst, nowUtc: Now.AddHours(2), rxNumber: "RX-1001");

        Assert.True(firstShouldReport);
        Assert.False(secondShouldReport);
    }

    [Fact]
    public void SameRxAgainAfterTwentyFourHoursReportsAgain()
    {
        var (_, stateAfterFirst) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false,
            rateLimitState: null, nowUtc: Now, rxNumber: "RX-1001");

        var (shouldReportNextDay, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false,
            rateLimitState: stateAfterFirst, nowUtc: Now.AddHours(25), rxNumber: "RX-1001");

        Assert.True(shouldReportNextDay);
    }

    [Fact]
    public void DifferentRxOnTheSameDayIsNotSuppressedByTheFirstsCap()
    {
        var (_, stateAfterFirst) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false,
            rateLimitState: null, nowUtc: Now, rxNumber: "RX-1001");

        var (shouldReportOther, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false,
            rateLimitState: stateAfterFirst, nowUtc: Now.AddMinutes(5), rxNumber: "RX-2002");

        Assert.True(shouldReportOther);
    }

    [Fact]
    public void SixthReportInAnHourIsSuppressed()
    {
        AutoDiagnosticRateLimitState? state = null;
        var reportedCount = 0;

        for (var i = 0; i < 5; i++)
        {
            var rxNumber = $"RX-{i}";
            var (shouldReport, updated) = AutoDiagnosticPolicy.ShouldReport(
                RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false,
                rateLimitState: state, nowUtc: Now.AddMinutes(i), rxNumber: rxNumber);
            state = updated;
            if (shouldReport) reportedCount++;
        }

        Assert.Equal(5, reportedCount);

        var (sixthShouldReport, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false,
            rateLimitState: state, nowUtc: Now.AddMinutes(6), rxNumber: "RX-5");

        Assert.False(sixthShouldReport);
    }

    [Fact]
    public void HourlyCapResetsAfterAnHourPasses()
    {
        AutoDiagnosticRateLimitState? state = null;
        for (var i = 0; i < 5; i++)
        {
            var (_, updated) = AutoDiagnosticPolicy.ShouldReport(
                RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false,
                rateLimitState: state, nowUtc: Now.AddMinutes(i), rxNumber: $"RX-{i}");
            state = updated;
        }

        var (shouldReportLater, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false,
            rateLimitState: state, nowUtc: Now.AddHours(1).AddMinutes(10), rxNumber: "RX-99");

        Assert.True(shouldReportLater);
    }

    [Fact]
    public void MissingRxNumberSkipsThePerRxCapButStillCountsAgainstTheHourlyCap()
    {
        var (first, stateAfterFirst) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false,
            rateLimitState: null, nowUtc: Now, rxNumber: null);

        var (second, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false,
            rateLimitState: stateAfterFirst, nowUtc: Now.AddMinutes(1), rxNumber: null);

        Assert.True(first);
        Assert.True(second); // no Rx identity to key the daily cap on — never suppressed solely for that reason
        Assert.Single(stateAfterFirst.RecentReportTimestampsUtc); // still counts against the hourly cap
    }

    // ---- ClassifyDocument ----

    [Fact]
    public void ClassifyDocumentIsRefillApprovalWhenApprovalFlagIsSet()
    {
        Assert.Equal("refill approval", AutoDiagnosticPolicy.ClassifyDocument(isApproval: true, screenMode: RxScreenMode.EditRx));
    }

    [Fact]
    public void ClassifyDocumentIsNewRxWhenNotApprovalAndScreenModeIsNewRx()
    {
        Assert.Equal("new Rx", AutoDiagnosticPolicy.ClassifyDocument(isApproval: false, screenMode: RxScreenMode.NewRx));
    }

    [Fact]
    public void ClassifyDocumentIsUnknownOtherwise()
    {
        Assert.Equal("unknown", AutoDiagnosticPolicy.ClassifyDocument(isApproval: false, screenMode: RxScreenMode.PreCheck));
        Assert.Equal("unknown", AutoDiagnosticPolicy.ClassifyDocument(isApproval: false, screenMode: RxScreenMode.Unknown));
    }
}
