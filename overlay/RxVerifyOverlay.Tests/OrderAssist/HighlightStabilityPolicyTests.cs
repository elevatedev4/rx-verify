using RxVerifyOverlay.OrderAssist;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Unit tests for HighlightStabilityPolicy — round 3's CLEAR-then-confirm
/// redesign (Will verbatim: "It's ok to clear it quickly and add a
/// 'Processing' by the sorted by rebate notice if we're waiting on
/// analysis"), replacing round 2's HOLD-then-swap policy, PLUS round 5's
/// asymmetric show=1/clear=2 threshold split (Will verbatim: "Needs to be
/// faster to read and display the calculation, needs to be more responsive
/// when the screen changes" — see HighlightStabilityPolicy's own class doc
/// for why only the SHOW direction's threshold dropped, not the CLEAR
/// direction's). Signatures below are synthetic placeholders ("A"/"B"/"")
/// standing in for whatever HighlightSignature.For* would have produced —
/// this class never inspects their content, only equality/emptiness, so
/// plain letters exercise the same code paths as real row-index signatures.
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
    public void ADifferentNonEmptyResultDisplaysOnTheFirstTickItAppears()
    {
        // ROUND 5 (was Processing in round 3 -- see FirstTimeADifferentNonEmptyResultStillNeedsProcessingBeforeRound5's
        // own doc below for the prior behavior this replaces): with
        // RequiredConsecutiveTicksToShow = 1, a candidate that differs from
        // what's displayed is adopted the SAME tick it's first proposed --
        // no more waiting a full confirmation cycle just to show a NEW
        // result. Will: "needs to be more responsive when the screen
        // changes."
        var outcome = HighlightStabilityPolicy.Decide(newSignature: "B", displayedSignature: "A", pendingSignature: "", pendingStreak: 0);

        Assert.Equal(HighlightStabilityPolicy.Decision.Display, outcome.Decision);
        Assert.Equal("", outcome.PendingSignature);
        Assert.Equal(0, outcome.PendingStreak);
    }

    [Fact]
    public void AThirdDistinctCandidateAlsoDisplaysImmediately()
    {
        // Even a candidate that itself replaces an ALREADY-pending
        // candidate (from a tick still mid-Processing under some other
        // path, e.g. a caller re-using Outcome state across an unrelated
        // gap) shows immediately once it's non-empty -- RequiredConsecutiveTicksToShow
        // is 1 regardless of pendingStreak's starting value.
        var outcome = HighlightStabilityPolicy.Decide(newSignature: "C", displayedSignature: "A", pendingSignature: "B", pendingStreak: 1);

        Assert.Equal(HighlightStabilityPolicy.Decision.Display, outcome.Decision);
        Assert.Equal("", outcome.PendingSignature);
        Assert.Equal(0, outcome.PendingStreak);
    }

    [Fact]
    public void GoingFromSomethingDisplayedToEmptyGoesToProcessingFirst()
    {
        // Will: "ok to clear it quickly" -- but "quickly" still means one
        // Processing tick of confirmation, not an instant single-tick
        // blank (protects against a one-tick OCR hiccup reading as "order
        // changed" when nothing really did). UNCHANGED by round 5 --
        // RequiredConsecutiveTicksToClear is still 2; only the SHOW
        // direction got faster.
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
        // Mid-Processing (pending "B" -- e.g. a clear that hasn't confirmed
        // yet), the tick's own fresh OCR flickers back to "A" -- since "A"
        // IS what's still logically displayed (Processing never updated
        // displayedSignature), this is just "the steady state again", not
        // a new candidate needing its own confirmation.
        var outcome = HighlightStabilityPolicy.Decide(newSignature: "A", displayedSignature: "A", pendingSignature: "B", pendingStreak: 1);

        Assert.Equal(HighlightStabilityPolicy.Decision.Display, outcome.Decision);
        Assert.Equal("", outcome.PendingSignature);
        Assert.Equal(0, outcome.PendingStreak);
    }

    [Fact]
    public void ConstantsMatchDocumentedValues()
    {
        // Pins the actual thresholds so a future accidental tuning change
        // shows up as a failing test, not silent drift. ROUND 5: these are
        // now DELIBERATELY asymmetric -- see class doc.
        Assert.Equal(1, HighlightStabilityPolicy.RequiredConsecutiveTicksToShow);
        Assert.Equal(2, HighlightStabilityPolicy.RequiredConsecutiveTicksToClear);
    }
}
