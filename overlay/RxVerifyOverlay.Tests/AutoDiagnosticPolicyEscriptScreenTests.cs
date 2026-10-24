using System;
using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Diagnostics;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Ocr;
using RxVerifyOverlay.Uia;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Screen-kind gate on AutoDiagnosticPolicy.ShouldReport (W-Q8, 2026-10-24:
/// "ignore screens that aren't prescriptions — notes, pricing"). The
/// auto-report for an unreadable refill count may only be filed when the
/// scan is an eScript screen (EscriptMarkerDetector.ContainsMarker); the
/// existing debounce/cap rules are covered by AutoDiagnosticPolicyTests /
/// AutoDiagnosticPolicyDebounceTests and are not re-tested here. All word
/// lists and Rx numbers below are synthetic.
/// </summary>
public class AutoDiagnosticPolicyEscriptScreenTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    private static OcrWord W(string text) => new() { Text = text };
    private static List<OcrWord> Words(params string[] texts) => texts.Select(W).ToList();

    private static AutoDiagnosticRateLimitState SatisfiedStreak(string contextKey) => new()
    {
        Streak = new AutoDiagnosticStreakState
        {
            ContextKey = contextKey,
            ConsecutiveMisses = AutoDiagnosticPolicy.MinConsecutiveMisses,
            FirstMissUtc = Now.AddSeconds(-AutoDiagnosticPolicy.MinPersistenceSeconds)
        }
    };

    // ---- detector signal on synthetic screens ----

    [Fact]
    public void EscriptTabScreenHasMarkerButNotesAndPricingLikeScreensDoNot()
    {
        var escript = Words("New", "Prescription", "Escript", "[3]", "Refills", "Qty");
        var notes = Words("Notes", "Add", "Note", "Patient", "called", "about", "delivery");
        var pricing = Words("Pricing", "Usual", "Customary", "Gross", "Amount", "Due", "Fills");

        Assert.True(EscriptMarkerDetector.ContainsMarker(escript));
        Assert.False(EscriptMarkerDetector.ContainsMarker(notes));
        Assert.False(EscriptMarkerDetector.ContainsMarker(pricing));
    }

    // ---- gate behaviour ----

    [Fact]
    public void EscriptScreenWithSatisfiedStreakIsFiled()
    {
        var words = Words("Refill", "Replace", "Escript", "[3]", "Refills");
        var (shouldReport, _, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, hasFillWords: true, isApproval: false, isBusyScreen: false,
            rateLimitState: SatisfiedStreak("RX-7001"), nowUtc: Now, rxNumber: "RX-7001", contextKey: "RX-7001",
            isEscriptScreen: EscriptMarkerDetector.ContainsMarker(words));

        Assert.True(shouldReport);
    }

    [Theory]
    [InlineData(RefillsBoxRenderState.NotProvided)]
    [InlineData(RefillsBoxRenderState.UnparseableQuantity)]
    [InlineData(RefillsBoxRenderState.NotOnScreen)]
    public void NotesOrPricingScreenIsNeverFiledEvenWithEveryOtherGateSatisfied(RefillsBoxRenderState state)
    {
        var words = Words("Pricing", "Total", "Fills", "Refill", "Approved");
        var (shouldReport, updated, _) = AutoDiagnosticPolicy.ShouldReport(
            state, hasFillWords: true, isApproval: true, isBusyScreen: false,
            rateLimitState: SatisfiedStreak("RX-7002"), nowUtc: Now, rxNumber: "RX-7002", contextKey: "RX-7002",
            isEscriptScreen: EscriptMarkerDetector.ContainsMarker(words));

        Assert.False(shouldReport);
        Assert.Empty(updated.RecentReportTimestampsUtc); // no budget consumed
        Assert.Empty(updated.LastReportedUtcByRx);
    }

    [Fact]
    public void NonEscriptScreenClearsTheStreakAndReportsStateChanged()
    {
        var (shouldReport, updated, stateChanged) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, SatisfiedStreak("RX-7003"), Now, "RX-7003", "RX-7003",
            isEscriptScreen: false);

        Assert.False(shouldReport);
        Assert.Null(updated.Streak);
        Assert.True(stateChanged); // the caller must persist the cleared streak
    }

    [Fact]
    public void NonEscriptScansCannotPreloadAStreakThatALaterEscriptScanCompletes()
    {
        const string key = "RX-7004";
        AutoDiagnosticRateLimitState? state = null;

        // Three well-spaced scans of a notes-like tab for the same Rx.
        for (var i = 0; i < 3; i++)
        {
            (_, state, _) = AutoDiagnosticPolicy.ShouldReport(
                RefillsBoxRenderState.NotProvided, true, false, false, state, Now.AddSeconds(i * 10), key, key, isEscriptScreen: false);
        }

        // Pharmacist switches to the eScript tab: the first eScript scan
        // starts a fresh 1-miss streak instead of reporting immediately.
        var (firstEscript, afterFirst, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, state, Now.AddSeconds(31), key, key, isEscriptScreen: true);
        Assert.False(firstEscript);
        Assert.Equal(1, afterFirst.Streak!.ConsecutiveMisses);
    }

    [Fact]
    public void EscriptDebounceStillRequiresThreeMissesAndFiveSeconds()
    {
        const string key = "RX-7005";
        AutoDiagnosticRateLimitState? state = null;
        bool r1, r2, r3;

        (r1, state, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, state, Now, key, key, isEscriptScreen: true);
        (r2, state, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, state, Now.AddSeconds(3), key, key, isEscriptScreen: true);
        (r3, _, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, state, Now.AddSeconds(6), key, key, isEscriptScreen: true);

        Assert.False(r1);
        Assert.False(r2);
        Assert.True(r3);
    }

    // ---- unaffected behaviour ----

    [Fact]
    public void ColouredResultOnAnEscriptScreenStillClearsTheStreakAndNeverReports()
    {
        var (shouldReport, updated, stateChanged) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.Green, true, false, false, SatisfiedStreak("RX-7006"), Now, "RX-7006", "RX-7006",
            isEscriptScreen: true);

        Assert.False(shouldReport);
        Assert.Null(updated.Streak);
        Assert.True(stateChanged);
    }

    [Fact]
    public void OmittedIsEscriptScreenDefaultsToEscriptSoPreExistingCallersAreUnchanged()
    {
        var (shouldReport, _, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, false, SatisfiedStreak("RX-7007"), Now, "RX-7007", "RX-7007");

        Assert.True(shouldReport);
    }

    [Fact]
    public void BusyScreenStillWinsAndLeavesTheStreakUntouchedEvenOnANonEscriptScan()
    {
        var seeded = SatisfiedStreak("RX-7008");
        var (shouldReport, updated, _) = AutoDiagnosticPolicy.ShouldReport(
            RefillsBoxRenderState.NotProvided, true, false, true, seeded, Now, "RX-7008", "RX-7008", isEscriptScreen: false);

        Assert.False(shouldReport);
        Assert.NotNull(updated.Streak); // busy branch runs first: no information, streak untouched
        Assert.Equal(AutoDiagnosticPolicy.MinConsecutiveMisses, updated.Streak!.ConsecutiveMisses);
    }

    [Fact]
    public void NoteBuilderAndDocumentClassificationAreUnaffected()
    {
        Assert.Equal("refill approval", AutoDiagnosticPolicy.ClassifyDocument(true, RxScreenMode.PreCheck));
        Assert.Equal("new Rx", AutoDiagnosticPolicy.ClassifyDocument(false, RxScreenMode.NewRx));
        Assert.Equal("unknown", AutoDiagnosticPolicy.ClassifyDocument(false, RxScreenMode.EditRx));
        Assert.True(AutoDiagnosticPolicy.IsFillWord("Refills"));
        Assert.False(AutoDiagnosticPolicy.IsFillWord("Filled:"));
    }
}
