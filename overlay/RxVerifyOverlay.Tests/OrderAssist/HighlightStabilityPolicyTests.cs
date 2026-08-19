using RxVerifyOverlay.OrderAssist;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Unit tests for HighlightStabilityPolicy (W-T85 bug 3 fix, Will verbatim:
/// "the items are flashing a bunch instead of staying solid") — the pure
/// hysteresis/debounce decision behind OrderAssistCoordinator.TickAsync.
/// Signatures below are synthetic placeholders ("A"/"B"/"") standing in
/// for whatever HighlightSignature.For* would have produced — this class
/// never inspects their content, only equality/emptiness, so plain letters
/// exercise the same code paths as real row-index signatures.
/// </summary>
public class HighlightStabilityPolicyTests
{
    [Fact]
    public void FirstEverNonEmptyResultDisplaysImmediatelyNoDebounce()
    {
        var decision = HighlightStabilityPolicy.Decide(newSignature: "A", displayedSignature: "", consecutiveEmptyTicksSoFar: 0, pendingChangeStreak: 0);

        Assert.Equal(HighlightStabilityPolicy.Decision.Display, decision);
    }

    [Fact]
    public void IdenticalResultToWhatsDisplayedIsKept()
    {
        var decision = HighlightStabilityPolicy.Decide(newSignature: "A", displayedSignature: "A", consecutiveEmptyTicksSoFar: 0, pendingChangeStreak: 0);

        Assert.Equal(HighlightStabilityPolicy.Decision.KeepDisplayed, decision);
    }

    [Fact]
    public void FirstTimeADifferentNonEmptyResultAppearsItIsHeldNotAdopted()
    {
        // pendingChangeStreak=0 means "B" hasn't been proposed before this
        // tick -- adopting it immediately would be exactly the one-tick
        // OCR-misread flash this policy exists to prevent.
        var decision = HighlightStabilityPolicy.Decide(newSignature: "B", displayedSignature: "A", consecutiveEmptyTicksSoFar: 0, pendingChangeStreak: 0);

        Assert.Equal(HighlightStabilityPolicy.Decision.KeepDisplayed, decision);
    }

    [Fact]
    public void ADifferentResultConfirmedOnASecondConsecutiveTickIsAdopted()
    {
        // pendingChangeStreak=1 means "B" was ALSO the proposal last tick
        // (caller already bumped the streak) -- this tick is the 2nd
        // consecutive agreement, meeting RequiredConsecutiveTicksToAdoptChange.
        var decision = HighlightStabilityPolicy.Decide(newSignature: "B", displayedSignature: "A", consecutiveEmptyTicksSoFar: 0, pendingChangeStreak: 1);

        Assert.Equal(HighlightStabilityPolicy.Decision.Display, decision);
    }

    [Fact]
    public void FirstEmptyTickWithNothingDisplayedIsANoOp()
    {
        var decision = HighlightStabilityPolicy.Decide(newSignature: "", displayedSignature: "", consecutiveEmptyTicksSoFar: 0, pendingChangeStreak: 0);

        Assert.Equal(HighlightStabilityPolicy.Decision.KeepDisplayed, decision);
    }

    [Fact]
    public void FirstEmptyTickWithSomethingDisplayedHoldsIt()
    {
        var decision = HighlightStabilityPolicy.Decide(newSignature: "", displayedSignature: "A", consecutiveEmptyTicksSoFar: 0, pendingChangeStreak: 0);

        Assert.Equal(HighlightStabilityPolicy.Decision.KeepDisplayed, decision);
    }

    [Fact]
    public void SecondConsecutiveEmptyTickStillHoldsBelowTheCap()
    {
        var decision = HighlightStabilityPolicy.Decide(newSignature: "", displayedSignature: "A", consecutiveEmptyTicksSoFar: 1, pendingChangeStreak: 0);

        Assert.Equal(HighlightStabilityPolicy.Decision.KeepDisplayed, decision);
    }

    [Fact]
    public void ThirdConsecutiveEmptyTickFinallyClears()
    {
        // MaxConsecutiveEmptyTicksBeforeClearing = 3 -- consecutiveEmptyTicksSoFar
        // of 2 (two ticks already empty) plus this one being the 3rd hits
        // the cap.
        var decision = HighlightStabilityPolicy.Decide(newSignature: "", displayedSignature: "A", consecutiveEmptyTicksSoFar: 2, pendingChangeStreak: 0);

        Assert.Equal(HighlightStabilityPolicy.Decision.Clear, decision);
    }

    [Fact]
    public void ConstantsMatchDocumentedValues()
    {
        // Pins the actual thresholds so a future accidental tuning change
        // shows up as a failing test, not silent drift.
        Assert.Equal(2, HighlightStabilityPolicy.RequiredConsecutiveTicksToAdoptChange);
        Assert.Equal(3, HighlightStabilityPolicy.MaxConsecutiveEmptyTicksBeforeClearing);
    }
}
