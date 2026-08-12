using RxVerifyOverlay.Integrated;
using RxVerifyOverlay.Uia;
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
///
/// TAB GATE (CommonTabState): every ShouldShowBoxes case below defaults
/// to CommonTabState.Unknown unless a test name says otherwise — Unknown
/// is defined to behave IDENTICALLY to how this gate behaved before
/// CommonTabState existed (see IntegratedVisibilityGate's own doc), so
/// every pre-existing scenario in this file is unchanged. The Off/On
/// cases are new, dedicated tests below.
/// </summary>
public class IntegratedVisibilityGateTests
{
    [Fact]
    public void BoxesShowOnlyWhenEveryConditionIsTrue()
    {
        Assert.True(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: true, isMaximized: true, hasVerifiableContent: true,
            hasResolvableFieldRects: true, isHiddenByToggle: false, commonTabState: CommonTabState.Unknown));
    }

    [Fact]
    public void BoxesHideWhenNotAttached()
    {
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: false, isForeground: true, isMaximized: true, hasVerifiableContent: true,
            hasResolvableFieldRects: true, isHiddenByToggle: false, commonTabState: CommonTabState.Unknown));
    }

    [Fact]
    public void BoxesHideWhenPioneerIsNotForeground()
    {
        // The pharmacist switched to a different app — boxes must not
        // float over whatever that app is.
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: false, isMaximized: true, hasVerifiableContent: true,
            hasResolvableFieldRects: true, isHiddenByToggle: false, commonTabState: CommonTabState.Unknown));
    }

    [Fact]
    public void BoxesHideWhenPioneerIsNotMaximized()
    {
        // MAXIMIZED-ONLY per the owner's spec.
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: true, isMaximized: false, hasVerifiableContent: true,
            hasResolvableFieldRects: true, isHiddenByToggle: false, commonTabState: CommonTabState.Unknown));
    }

    [Fact]
    public void BoxesHideWhenNothingHasBeenVerifiedYet()
    {
        // Mirrors the existing non-escript/no-data blank-state signal —
        // no data to draw boxes for.
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: true, isMaximized: true, hasVerifiableContent: false,
            hasResolvableFieldRects: true, isHiddenByToggle: false, commonTabState: CommonTabState.Unknown));
    }

    [Fact]
    public void BoxesHideWhenFieldRectsArentResolvable()
    {
        // Round 4 addendum item 6 — best-effort proxy for "not on the
        // Common tab": the entered fields couldn't be resolved to
        // on-screen rects this tick, even though everything else checks
        // out. Still the deciding factor when CommonTabState is Unknown.
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: true, isMaximized: true, hasVerifiableContent: true,
            hasResolvableFieldRects: false, isHiddenByToggle: false, commonTabState: CommonTabState.Unknown));
    }

    [Fact]
    public void BoxesHideWhenPharmacistHidThemViaToggle()
    {
        // Round 4 item 2 — the control box's "hide overlay" checkbox / `\` hotkey.
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: true, isMaximized: true, hasVerifiableContent: true,
            hasResolvableFieldRects: true, isHiddenByToggle: true, commonTabState: CommonTabState.Unknown));
    }

    [Fact]
    public void BoxesHideWhenCommonTabStateIsOffEvenIfEveryOtherConditionIsTrue()
    {
        // THE OWNER'S BUG FIX: hasResolvableFieldRects is deliberately
        // TRUE here — this is exactly the scenario the old proxy alone
        // got wrong (RxDetailsPanel's fields keep non-empty
        // BoundingRectangles even on a different outer tab). A confirmed
        // CommonTabState.Off must override it and force a hide.
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: true, isMaximized: true, hasVerifiableContent: true,
            hasResolvableFieldRects: true, isHiddenByToggle: false, commonTabState: CommonTabState.Off));
    }

    [Fact]
    public void BoxesShowWhenCommonTabStateIsOnAndEveryOtherConditionIsTrue()
    {
        Assert.True(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: true, isMaximized: true, hasVerifiableContent: true,
            hasResolvableFieldRects: true, isHiddenByToggle: false, commonTabState: CommonTabState.On));
    }

    [Fact]
    public void BoxesHideWhenCommonTabStateIsOnButAnotherConditionFails()
    {
        // On doesn't bypass the other gates — it's an additional
        // confirmed signal, not a master override in the show direction.
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: true, isMaximized: false, hasVerifiableContent: true,
            hasResolvableFieldRects: true, isHiddenByToggle: false, commonTabState: CommonTabState.On));
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
            hasResolvableFieldRects: true, isHiddenByToggle: false, commonTabState: CommonTabState.Unknown);

        Assert.True(controlBoxShown);
        Assert.False(boxesShown);
    }
}
