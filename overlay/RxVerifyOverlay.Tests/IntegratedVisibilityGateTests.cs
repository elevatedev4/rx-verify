using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for IntegratedVisibilityGate (Integrated/
/// IntegratedVisibilityGate.cs) — the pure decision behind when the
/// integrated boxes layer / control box should be visible. Encodes the
/// owner's spec directly: boxes require attached + foreground + maximized
/// + something verified to show; the control box only requires attached +
/// foreground (it stays up, in its "maximize to use integrated view"
/// state, even when not maximized).
/// </summary>
public class IntegratedVisibilityGateTests
{
    [Fact]
    public void BoxesShowOnlyWhenEveryConditionIsTrue()
    {
        Assert.True(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: true, isMaximized: true, hasVerifiableContent: true,
            hasResolvableFieldRects: true, isHiddenByToggle: false));
    }

    [Fact]
    public void BoxesHideWhenNotAttached()
    {
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: false, isForeground: true, isMaximized: true, hasVerifiableContent: true,
            hasResolvableFieldRects: true, isHiddenByToggle: false));
    }

    [Fact]
    public void BoxesHideWhenPioneerIsNotForeground()
    {
        // The pharmacist switched to a different app — boxes must not
        // float over whatever that app is.
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: false, isMaximized: true, hasVerifiableContent: true,
            hasResolvableFieldRects: true, isHiddenByToggle: false));
    }

    [Fact]
    public void BoxesHideWhenPioneerIsNotMaximized()
    {
        // MAXIMIZED-ONLY per the owner's spec.
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: true, isMaximized: false, hasVerifiableContent: true,
            hasResolvableFieldRects: true, isHiddenByToggle: false));
    }

    [Fact]
    public void BoxesHideWhenNothingHasBeenVerifiedYet()
    {
        // Mirrors the existing non-escript/no-data blank-state signal —
        // no data to draw boxes for.
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: true, isMaximized: true, hasVerifiableContent: false,
            hasResolvableFieldRects: true, isHiddenByToggle: false));
    }

    [Fact]
    public void BoxesHideWhenFieldRectsArentResolvable()
    {
        // Round 4 addendum item 6 — best-effort proxy for "not on the
        // Common tab": the entered fields couldn't be resolved to
        // on-screen rects this tick, even though everything else checks
        // out.
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: true, isMaximized: true, hasVerifiableContent: true,
            hasResolvableFieldRects: false, isHiddenByToggle: false));
    }

    [Fact]
    public void BoxesHideWhenPharmacistHidThemViaToggle()
    {
        // Round 4 item 2 — the control box's "hide overlay" checkbox / `\` hotkey.
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: true, isMaximized: true, hasVerifiableContent: true,
            hasResolvableFieldRects: true, isHiddenByToggle: true));
    }

    [Fact]
    public void ControlBoxShowsWhenPioneerIsTheForegroundApp()
    {
        Assert.True(IntegratedVisibilityGate.ShouldShowControlBox(isPioneerForegroundApp: true));
    }

    [Fact]
    public void ControlBoxHidesWhenPioneerIsNotTheForegroundApp()
    {
        Assert.False(IntegratedVisibilityGate.ShouldShowControlBox(isPioneerForegroundApp: false));
    }

    [Fact]
    public void ControlBoxVisibilityIsIndependentOfBoxesVisibility()
    {
        // OWNER FEEDBACK (round 2, item 1): the control box must stay
        // anchored even when no specific Rx is attached/verifiable — only
        // ShouldShowBoxes is gated by that. Confirms the two functions
        // genuinely disagree in this scenario rather than one silently
        // mirroring the other.
        var controlBoxShown = IntegratedVisibilityGate.ShouldShowControlBox(isPioneerForegroundApp: true);
        var boxesShown = IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: false, isForeground: true, isMaximized: true, hasVerifiableContent: false,
            hasResolvableFieldRects: true, isHiddenByToggle: false);

        Assert.True(controlBoxShown);
        Assert.False(boxesShown);
    }
}
