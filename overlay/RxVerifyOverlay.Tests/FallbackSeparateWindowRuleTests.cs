using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for FallbackSeparateWindowRule (Integrated/
/// FallbackSeparateWindowRule.cs) — the pure decision behind the
/// "invisible-app trap" fallback-window bookkeeping in
/// IntegratedOverlayCoordinator.TickCore. Includes the exact regression
/// a re-review confirmed and traced: leaving Integrated mode from the
/// fallback-shown state must NOT raise Hide, or the pharmacist's own
/// "switch to Separate" click would have that very window immediately
/// re-hidden by the next tick with nothing left to ever show it again.
/// </summary>
public class FallbackSeparateWindowRuleTests
{
    [Fact]
    public void LeavingIntegratedModeFromFallbackShownClearsFlagWithoutRaisingHide()
    {
        // THE REGRESSION: Integrated mode + PioneerRx not attached ->
        // fallback has shown the separate window -> pharmacist clicks
        // "Separate window" IN that window -> DisplayMode is now
        // Separate. The very next tick must leave the window the
        // pharmacist just switched to alone.
        var decision = FallbackSeparateWindowRule.Decide(
            isIntegratedMode: false, isPioneerAttached: false, wasFallbackShown: true);

        Assert.False(decision.NewFallbackShown);
        Assert.False(decision.RaiseHide);
        Assert.False(decision.RaiseShow);
    }

    [Fact]
    public void LeavingIntegratedModeWhenFallbackWasNotShownIsAlsoInert()
    {
        var decision = FallbackSeparateWindowRule.Decide(
            isIntegratedMode: false, isPioneerAttached: false, wasFallbackShown: false);

        Assert.False(decision.NewFallbackShown);
        Assert.False(decision.RaiseHide);
        Assert.False(decision.RaiseShow);
    }

    [Fact]
    public void IntegratedModeWithPioneerNotAttachedShowsFallbackOnlyOnTheEdge()
    {
        var firstTick = FallbackSeparateWindowRule.Decide(
            isIntegratedMode: true, isPioneerAttached: false, wasFallbackShown: false);

        Assert.True(firstTick.NewFallbackShown);
        Assert.True(firstTick.RaiseShow);
        Assert.False(firstTick.RaiseHide);

        // Next tick, still not attached — already shown, must not raise
        // Show again (would fight a pharmacist who manually re-hid it).
        var secondTick = FallbackSeparateWindowRule.Decide(
            isIntegratedMode: true, isPioneerAttached: false, wasFallbackShown: true);

        Assert.True(secondTick.NewFallbackShown);
        Assert.False(secondTick.RaiseShow);
        Assert.False(secondTick.RaiseHide);
    }

    [Fact]
    public void IntegratedModeWithPioneerReattachedHidesTheFallback()
    {
        var decision = FallbackSeparateWindowRule.Decide(
            isIntegratedMode: true, isPioneerAttached: true, wasFallbackShown: true);

        Assert.False(decision.NewFallbackShown);
        Assert.True(decision.RaiseHide);
        Assert.False(decision.RaiseShow);
    }

    [Fact]
    public void IntegratedModeWithPioneerAttachedAndNoFallbackShownIsInert()
    {
        // The common case — never raises anything when there's nothing
        // to reconcile.
        var decision = FallbackSeparateWindowRule.Decide(
            isIntegratedMode: true, isPioneerAttached: true, wasFallbackShown: false);

        Assert.False(decision.NewFallbackShown);
        Assert.False(decision.RaiseHide);
        Assert.False(decision.RaiseShow);
    }
}
