using System;
using RxVerifyOverlay.Diagnostics;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for the debounce/busy-screen/fill-word-tightening gates
/// added to AutoDiagnosticPolicy.ShouldReport (2026-09-16 field report: a
/// real auto-diagnostic report fired on a PreCheck screen while
/// Pioneer's "Please wait while the claim processes" popup was still up
/// — the only fill-shaped OCR text visible was "Filled:"/"Fill:" LABELS,
/// not real refill content, and that single scan burned the Rx's daily
/// report budget). See AutoDiagnosticPolicyTests.cs for the
/// pre-existing gate coverage (content gate, daily/hourly caps) — those
/// tests seed an already-satisfied streak so the NEW debounce gate isn't
/// a factor there; this file exercises the debounce/busy/fill-word gates
/// themselves. All Rx numbers/values below are synthetic.
/// </summary>
public class AutoDiagnosticPolicyDebounceTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    // ---- (a) consecutive-miss count gate ----

    [Fact]
    public void TwoMissesInTenSecondsDoNotReportButAThirdDoes()
    {
        const string contextKey = "RX-2001";
        AutoDiagnosticRateLimitState? state = null;

        var (firstReport, stateAfterFirst) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, state, Now, contextKey, contextKey);
        Assert.False(firstReport);

        var (secondReport, stateAfterSecond) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, stateAfterFirst, Now.AddSeconds(10), contextKey, contextKey);
        Assert.False(secondReport); // only 2 consecutive misses so far

        var (thirdReport, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, stateAfterSecond, Now.AddSeconds(12), contextKey, contextKey);
        Assert.True(thirdReport); // 3rd consecutive miss, already 12s since the first
    }

    // ---- (b) wall-clock persistence gate ----

    [Fact]
    public void ThreeMissesWithinTwoSecondsDoNotReportUntilFiveSecondsElapse()
    {
        const string contextKey = "RX-2002";
        AutoDiagnosticRateLimitState? state = null;

        var (r1, s1) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, state, Now, contextKey, contextKey);
        Assert.False(r1);

        var (r2, s2) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, s1, Now.AddSeconds(1), contextKey, contextKey);
        Assert.False(r2);

        // 3rd consecutive miss — count threshold met, but only 2s have
        // passed since the first miss, so the persistence gate still
        // blocks it.
        var (r3, s3) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, s2, Now.AddSeconds(2), contextKey, contextKey);
        Assert.False(r3);

        var (r4, s4) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, s3, Now.AddSeconds(4.9), contextKey, contextKey);
        Assert.False(r4); // still under 5s

        var (r5, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, s4, Now.AddSeconds(5), contextKey, contextKey);
        Assert.True(r5); // exactly 5s since the first miss, and well past 3 consecutive misses
    }

    // ---- (c) coloured result resets the streak ----

    [Fact]
    public void ColouredResultResetsAnInProgressStreak()
    {
        const string contextKey = "RX-3001";
        var seeded = new AutoDiagnosticRateLimitState
        {
            Streak = new AutoDiagnosticStreakState { ContextKey = contextKey, ConsecutiveMisses = 3, FirstMissUtc = Now.AddSeconds(-10) }
        };

        var (shouldReportOnGreen, afterGreen) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.Green, true, false, false, seeded, Now, contextKey, contextKey);

        Assert.False(shouldReportOnGreen);
        Assert.Null(afterGreen.Streak);

        // The very next uncoloured scan for the SAME context starts a
        // brand-new 1-miss streak — it must NOT immediately report just
        // because the old (now-cleared) streak had already satisfied the
        // debounce thresholds.
        var (shouldReportAfterReset, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, afterGreen, Now.AddSeconds(1), contextKey, contextKey);

        Assert.False(shouldReportAfterReset);
    }

    // ---- (d) busy-screen scan is pure no-information ----

    [Fact]
    public void BusyScreenScanNeitherResetsNorIncrementsTheStreak()
    {
        const string contextKey = "RX-4001";
        var firstMissUtc = Now.AddSeconds(-3);
        var seeded = new AutoDiagnosticRateLimitState
        {
            Streak = new AutoDiagnosticStreakState { ContextKey = contextKey, ConsecutiveMisses = 2, FirstMissUtc = firstMissUtc }
        };

        var (shouldReport, updated) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, true, seeded, Now, contextKey, contextKey);

        Assert.False(shouldReport);
        Assert.NotNull(updated.Streak);
        Assert.Equal(2, updated.Streak!.ConsecutiveMisses); // unchanged — not incremented
        Assert.Equal(contextKey, updated.Streak.ContextKey); // unchanged — not reset
        Assert.Equal(firstMissUtc, updated.Streak.FirstMissUtc); // unchanged
    }

    // ---- (e) gate 2 tightening: "Filled:"/"Fill:" labels don't count ----

    [Theory]
    [InlineData("Filled:", false)]
    [InlineData("Fill:", false)]
    [InlineData("Filled", false)]
    [InlineData("Fill", false)]
    [InlineData("TotalFi11s", true)]
    [InlineData("Refills", true)]
    [InlineData("refill", true)]
    public void IsFillWordDistinguishesFieldLabelsFromRealFillContent(string word, bool expected)
    {
        Assert.Equal(expected, AutoDiagnosticPolicy.IsFillWord(word));
    }

    // ---- (f) empty rxNumber keys the daily cap on the fallback context ----

    [Fact]
    public void EmptyRxNumberUsesTheFallbackContextKeyForTheDailyCapAcrossRealScans()
    {
        const string fallbackContext = "New Rx - (no Rx number)|NewRx";
        var t0 = Now;
        AutoDiagnosticRateLimitState? state = null;

        (_, state) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, state, t0, null, fallbackContext);
        (_, state) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, state, t0.AddSeconds(3), null, fallbackContext);

        var (thirdShouldReport, stateAfterThird) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, state, t0.AddSeconds(6), null, fallbackContext);
        Assert.True(thirdShouldReport);

        var (fourthShouldReport, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, stateAfterThird, t0.AddMinutes(1), null, fallbackContext);
        Assert.False(fourthShouldReport); // same fallback context, same rolling day -> suppressed by the daily cap
    }
}
