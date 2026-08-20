using RxVerifyOverlay.OrderAssist;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Unit tests for HighlightStabilityPolicy — round 3's CLEAR-then-confirm
/// redesign (Will verbatim: "It's ok to clear it quickly and add a
/// 'Processing' by the sorted by rebate notice if we're waiting on
/// analysis"), replacing round 2's HOLD-then-swap policy. Signatures below
/// are synthetic placeholders ("A"/"B"/"") standing in for whatever
/// HighlightSignature.For* would have produced — this class never inspects
/// their content, only equality/emptiness, so plain letters exercise the
/// same code paths as real row-index signatures.
/// </summary>
public class HighlightStabilityPolicyTests
{
    [Fact]
    public void FirstEverNonEmptyResultDisplaysImmediatelyNoDebounce()
    {
        var outcome = HighlightStabilityPolicy.Decide(newSignature: "A", displayedSignature: "", pendingSignature: "", pendingStreak: 0);

        Assert.Equal(HighlightStabilityPolicy.Decision.Display, outcome.Decision);
        Assert.Equal("", outcome.PendingSignature);
        Assert.Equal(0, outcome.PendingStreak);
    }

    [Fact]
    public void FirstEverEmptyResultWithNothingDisplayedIsANoOpClear()
    {
        var outcome = HighlightStabilityPolicy.Decide(newSignature: "", displayedSignature: "", pendingSignature: "", pendingStreak: 0);

        Assert.Equal(HighlightStabilityPolicy.Decision.Clear, outcome.Decision);
    }

    [Fact]
    public void IdenticalResultToWhatsDisplayedIsKeptAsDisplay()
    {
        var outcome = HighlightStabilityPolicy.Decide(newSignature: "A", displayedSignature: "A", pendingSignature: "", pendingStreak: 0);

        Assert.Equal(HighlightStabilityPolicy.Decision.Display, outcome.Decision);
        Assert.Equal("", outcome.PendingSignature);
        Assert.Equal(0, outcome.PendingStreak);
    }

    [Fact]
    public void IdenticalEmptyResultToWhatsDisplayedStaysClear()
    {
        // displayedSignature "" always means "nothing shown", regardless of
        // how it got there -- repeating "" stays a steady-state Clear.
        var outcome = HighlightStabilityPolicy.Decide(newSignature: "", displayedSignature: "", pendingSignature: "", pendingStreak: 0);

        Assert.Equal(HighlightStabilityPolicy.Decision.Clear, outcome.Decision);
    }

    [Fact]
    public void FirstTimeADifferentNonEmptyResultAppearsItGoesToProcessingNotDisplay()
    {
        // ROUND 3: unlike round 2's KeepDisplayed (which kept showing "A"),
        // this immediately stops showing "A" -- the caller clears the
        // overlay and shows "Processing" instead of a stale answer.
        var outcome = HighlightStabilityPolicy.Decide(newSignature: "B", displayedSignature: "A", pendingSignature: "", pendingStreak: 0);

        Assert.Equal(HighlightStabilityPolicy.Decision.Processing, outcome.Decision);
        Assert.Equal("B", outcome.PendingSignature);
        Assert.Equal(1, outcome.PendingStreak);
    }

    [Fact]
    public void ADifferentResultConfirmedOnASecondConsecutiveTickIsAdopted()
    {
        // pendingSignature/pendingStreak carry forward the PREVIOUS call's
        // own Outcome -- this is the 2nd consecutive tick proposing "B",
        // meeting RequiredConsecutiveTicksToAdoptChange.
        var outcome = HighlightStabilityPolicy.Decide(newSignature: "B", displayedSignature: "A", pendingSignature: "B", pendingStreak: 1);

        Assert.Equal(HighlightStabilityPolicy.Decision.Display, outcome.Decision);
        Assert.Equal("", outcome.PendingSignature);
        Assert.Equal(0, outcome.PendingStreak);
    }

    [Fact]
    public void ADifferentThirdCandidateResetsThePendingStreak()
    {
        // Tick N proposed "B" (streak 1). Tick N+1 proposes "C" instead --
        // a genuinely different candidate, so the streak restarts at 1 for
        // "C", not 2.
        var outcome = HighlightStabilityPolicy.Decide(newSignature: "C", displayedSignature: "A", pendingSignature: "B", pendingStreak: 1);

        Assert.Equal(HighlightStabilityPolicy.Decision.Processing, outcome.Decision);
        Assert.Equal("C", outcome.PendingSignature);
        Assert.Equal(1, outcome.PendingStreak);
    }

    [Fact]
    public void GoingFromSomethingDisplayedToEmptyGoesToProcessingFirst()
    {
        // Will: "ok to clear it quickly" -- but "quickly" still means one
        // Processing tick of confirmation, not an instant single-tick
        // blank (protects against a one-tick OCR hiccup reading as "order
        // changed" when nothing really did).
        var outcome = HighlightStabilityPolicy.Decide(newSignature: "", displayedSignature: "A", pendingSignature: "", pendingStreak: 0);

        Assert.Equal(HighlightStabilityPolicy.Decision.Processing, outcome.Decision);
        Assert.Equal("", outcome.PendingSignature);
        Assert.Equal(1, outcome.PendingStreak);
    }

    [Fact]
    public void GoingFromSomethingDisplayedToEmptyConfirmedOnSecondTickClears()
    {
        var outcome = HighlightStabilityPolicy.Decide(newSignature: "", displayedSignature: "A", pendingSignature: "", pendingStreak: 1);

        Assert.Equal(HighlightStabilityPolicy.Decision.Clear, outcome.Decision);
        Assert.Equal("", outcome.PendingSignature);
        Assert.Equal(0, outcome.PendingStreak);
    }

    [Fact]
    public void FlickerBackToTheOriginallyDisplayedSignatureReadoptsItImmediately()
    {
        // Mid-Processing (pending "B"), the tick's own fresh OCR flickers
        // back to "A" -- since "A" IS what's still logically displayed
        // (Processing never updated displayedSignature), this is just "the
        // steady state again", not a new candidate needing its own
        // confirmation.
        var outcome = HighlightStabilityPolicy.Decide(newSignature: "A", displayedSignature: "A", pendingSignature: "B", pendingStreak: 1);

        Assert.Equal(HighlightStabilityPolicy.Decision.Display, outcome.Decision);
        Assert.Equal("", outcome.PendingSignature);
        Assert.Equal(0, outcome.PendingStreak);
    }

    [Fact]
    public void ConstantsMatchDocumentedValues()
    {
        // Pins the actual threshold so a future accidental tuning change
        // shows up as a failing test, not silent drift.
        Assert.Equal(2, HighlightStabilityPolicy.RequiredConsecutiveTicksToAdoptChange);
    }
}
