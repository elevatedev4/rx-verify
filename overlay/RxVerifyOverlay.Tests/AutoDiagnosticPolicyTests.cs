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

    /// <summary>
    /// Most of the tests below exercise ShouldReport's OLDER gates
    /// (fill-word/approval content, daily/hourly caps) and were written
    /// before the debounce streak gate existed (see
    /// AutoDiagnosticPolicyDebounceTests.cs for that gate's own dedicated
    /// coverage). Rather than replaying 3 real ShouldReport calls 5+
    /// seconds apart in every one of them, this seeds a rate-limit state
    /// whose Streak has ALREADY satisfied MinConsecutiveMisses/
    /// MinPersistenceSeconds for `contextKey`, so a single ShouldReport
    /// call here exercises exactly the gate each test names, with the
    /// debounce gate already a non-factor.
    /// </summary>
    private static AutoDiagnosticRateLimitState SeedSatisfiedStreak(string contextKey, DateTime nowUtc) => new()
    {
        Streak = new AutoDiagnosticStreakState
        {
            ContextKey = contextKey,
            ConsecutiveMisses = AutoDiagnosticPolicy.MinConsecutiveMisses,
            FirstMissUtc = nowUtc.AddSeconds(-AutoDiagnosticPolicy.MinPersistenceSeconds)
        }
    };

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
        var (shouldReport, _, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false, isBusyScreen: false,
            rateLimitState: SeedSatisfiedStreak("RX-1001", Now), nowUtc: Now, rxNumber: "RX-1001", contextKey: "RX-1001");

        Assert.True(shouldReport);
    }

    [Fact]
    public void ColouredNeverReportsEvenWithFillWords()
    {
        var (shouldReport, _, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.Green, hasFillWords: true, isApproval: true, isBusyScreen: false,
            rateLimitState: SeedSatisfiedStreak("RX-1001", Now), nowUtc: Now, rxNumber: "RX-1001", contextKey: "RX-1001");

        Assert.False(shouldReport);
    }

    [Fact]
    public void NoFillWordsAndNotApprovalNeverReports()
    {
        var (shouldReport, _, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: false, isApproval: false, isBusyScreen: false,
            rateLimitState: SeedSatisfiedStreak("RX-1001", Now), nowUtc: Now, rxNumber: "RX-1001", contextKey: "RX-1001");

        Assert.False(shouldReport);
    }

    [Fact]
    public void ApprovalAloneWithoutFillWordsStillReports()
    {
        var (shouldReport, _, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.UnparseableQuantity, hasFillWords: false, isApproval: true, isBusyScreen: false,
            rateLimitState: SeedSatisfiedStreak("RX-1001", Now), nowUtc: Now, rxNumber: "RX-1001", contextKey: "RX-1001");

        Assert.True(shouldReport);
    }

    [Fact]
    public void SameRxTwiceInADayReportsOnlyOnce()
    {
        var (firstShouldReport, stateAfterFirst, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false, isBusyScreen: false,
            rateLimitState: SeedSatisfiedStreak("RX-1001", Now), nowUtc: Now, rxNumber: "RX-1001", contextKey: "RX-1001");

        var (secondShouldReport, _, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false, isBusyScreen: false,
            rateLimitState: stateAfterFirst, nowUtc: Now.AddHours(2), rxNumber: "RX-1001", contextKey: "RX-1001");

        Assert.True(firstShouldReport);
        Assert.False(secondShouldReport);
    }

    [Fact]
    public void SameRxAgainAfterTwentyFourHoursReportsAgain()
    {
        var (_, stateAfterFirst, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false, isBusyScreen: false,
            rateLimitState: SeedSatisfiedStreak("RX-1001", Now), nowUtc: Now, rxNumber: "RX-1001", contextKey: "RX-1001");

        var (shouldReportNextDay, _, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false, isBusyScreen: false,
            rateLimitState: stateAfterFirst, nowUtc: Now.AddHours(25), rxNumber: "RX-1001", contextKey: "RX-1001");

        Assert.True(shouldReportNextDay);
    }

    [Fact]
    public void DifferentRxOnTheSameDayIsNotSuppressedByTheFirstsCap()
    {
        var (_, stateAfterFirst, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false, isBusyScreen: false,
            rateLimitState: SeedSatisfiedStreak("RX-1001", Now), nowUtc: Now, rxNumber: "RX-1001", contextKey: "RX-1001");

        // RX-2002 is a DIFFERENT context, so it needs its own satisfied
        // streak seeded before this call — a fresh context always starts
        // its own debounce clock (see AutoDiagnosticPolicyDebounceTests
        // for that behavior's own dedicated coverage).
        stateAfterFirst.Streak = new AutoDiagnosticStreakState
        {
            ContextKey = "RX-2002",
            ConsecutiveMisses = AutoDiagnosticPolicy.MinConsecutiveMisses,
            FirstMissUtc = Now.AddMinutes(5).AddSeconds(-AutoDiagnosticPolicy.MinPersistenceSeconds)
        };

        var (shouldReportOther, _, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false, isBusyScreen: false,
            rateLimitState: stateAfterFirst, nowUtc: Now.AddMinutes(5), rxNumber: "RX-2002", contextKey: "RX-2002");

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
            var now = Now.AddMinutes(i);
            var seeded = state ?? new AutoDiagnosticRateLimitState();
            seeded.Streak = new AutoDiagnosticStreakState
            {
                ContextKey = rxNumber,
                ConsecutiveMisses = AutoDiagnosticPolicy.MinConsecutiveMisses,
                FirstMissUtc = now.AddSeconds(-AutoDiagnosticPolicy.MinPersistenceSeconds)
            };

            var (shouldReport, updated, _) = AutoDiagnosticPolicy.ShouldReport(
                RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false, isBusyScreen: false,
                rateLimitState: seeded, nowUtc: now, rxNumber: rxNumber, contextKey: rxNumber);
            state = updated;
            if (shouldReport) reportedCount++;
        }

        Assert.Equal(5, reportedCount);

        state!.Streak = new AutoDiagnosticStreakState
        {
            ContextKey = "RX-5",
            ConsecutiveMisses = AutoDiagnosticPolicy.MinConsecutiveMisses,
            FirstMissUtc = Now.AddMinutes(6).AddSeconds(-AutoDiagnosticPolicy.MinPersistenceSeconds)
        };

        var (sixthShouldReport, _, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false, isBusyScreen: false,
            rateLimitState: state, nowUtc: Now.AddMinutes(6), rxNumber: "RX-5", contextKey: "RX-5");

        Assert.False(sixthShouldReport);
    }

    [Fact]
    public void HourlyCapResetsAfterAnHourPasses()
    {
        AutoDiagnosticRateLimitState? state = null;
        for (var i = 0; i < 5; i++)
        {
            var rxNumber = $"RX-{i}";
            var now = Now.AddMinutes(i);
            var seeded = state ?? new AutoDiagnosticRateLimitState();
            seeded.Streak = new AutoDiagnosticStreakState
            {
                ContextKey = rxNumber,
                ConsecutiveMisses = AutoDiagnosticPolicy.MinConsecutiveMisses,
                FirstMissUtc = now.AddSeconds(-AutoDiagnosticPolicy.MinPersistenceSeconds)
            };

            var (_, updated, _) = AutoDiagnosticPolicy.ShouldReport(
                RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false, isBusyScreen: false,
                rateLimitState: seeded, nowUtc: now, rxNumber: rxNumber, contextKey: rxNumber);
            state = updated;
        }

        var later = Now.AddHours(1).AddMinutes(10);
        state!.Streak = new AutoDiagnosticStreakState
        {
            ContextKey = "RX-99",
            ConsecutiveMisses = AutoDiagnosticPolicy.MinConsecutiveMisses,
            FirstMissUtc = later.AddSeconds(-AutoDiagnosticPolicy.MinPersistenceSeconds)
        };

        var (shouldReportLater, _, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false, isBusyScreen: false,
            rateLimitState: state, nowUtc: later, rxNumber: "RX-99", contextKey: "RX-99");

        Assert.True(shouldReportLater);
    }

    [Fact]
    public void MissingRxNumberUsesTheFallbackContextKeyForTheDailyCap()
    {
        // 2026-09-16 (brief item 4): an unidentified Rx no longer skips
        // the daily cap outright — it keys the cap on the fallback
        // context (window title + screen mode) instead, so the SAME
        // fallback context reported once is suppressed for 24h just like
        // a real Rx number would be.
        const string fallbackContext = "Edit Rx - (no Rx number)|EditRx";

        var (first, stateAfterFirst, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false, isBusyScreen: false,
            rateLimitState: SeedSatisfiedStreak(fallbackContext, Now), nowUtc: Now, rxNumber: null, contextKey: fallbackContext);

        var (second, stateAfterSecond, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false, isBusyScreen: false,
            rateLimitState: stateAfterFirst, nowUtc: Now.AddMinutes(1), rxNumber: null, contextKey: fallbackContext);

        Assert.True(first);
        Assert.False(second); // same fallback context, same day -> suppressed
        Assert.Single(stateAfterSecond.RecentReportTimestampsUtc); // only the first actually consumed the hourly budget
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
