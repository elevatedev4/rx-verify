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

    // ------------------------------------------------------------------
    // ROUND 3 FIX: the Decide() function itself is UNCHANGED — round 2
    // wired the WRONG signal into its isPioneerAttached parameter
    // (hasForegroundPioneerWindow, "is Pioneer in front right now"
    // instead of "does Pioneer exist at all" — see PioneerPresence's
    // doc). These tests exercise the SAME pure function under its
    // CORRECT semantics (isPioneerAttached now means "pioneerExists",
    // fed by PioneerPresence.Exists in IntegratedOverlayCoordinator),
    // covering the exact scenarios the owner's launch report and the
    // round-3 diagnosis called out.
    // ------------------------------------------------------------------

    [Fact]
    public void LaunchWithPioneerRunningButNotForegroundDoesNotShowTheFallback()
    {
        // At launch, whatever process started the app (PowerShell, a
        // shortcut, Explorer) is the OS foreground window, even though
        // PioneerRx is already open in the background. pioneerExists must
        // still be true here (PioneerPresence.Exists finds it via the
        // process-name check even with hasForegroundPioneerWindow false),
        // so this must NOT show the fallback separate window — that was
        // exactly the "still the old regular window" bug.
        var decision = FallbackSeparateWindowRule.Decide(
            isIntegratedMode: true, isPioneerAttached: true /* pioneerExists */, wasFallbackShown: false);

        Assert.False(decision.RaiseShow);
        Assert.False(decision.NewFallbackShown);
    }

    [Fact]
    public void AltTabAwayFromPioneerDoesNotShowTheFallback()
    {
        // Same pioneerExists=true input as the launch case above — an
        // alt-tab away from Pioneer to check something else is
        // indistinguishable from "not foreground yet at launch" as far as
        // this rule is concerned, and must be equally quiet: no fallback
        // pop just because Pioneer briefly isn't in front.
        var decision = FallbackSeparateWindowRule.Decide(
            isIntegratedMode: true, isPioneerAttached: true /* pioneerExists */, wasFallbackShown: false);

        Assert.False(decision.RaiseShow);
        Assert.False(decision.NewFallbackShown);
    }

    [Fact]
    public void PioneerGenuinelyAbsentShowsTheFallback()
    {
        // PioneerRx isn't running anywhere on the system at all — the
        // ORIGINAL round-1 escape hatch this rule exists for must still
        // fire so the app is never left completely invisible.
        var decision = FallbackSeparateWindowRule.Decide(
            isIntegratedMode: true, isPioneerAttached: false /* pioneerExists */, wasFallbackShown: false);

        Assert.True(decision.RaiseShow);
        Assert.True(decision.NewFallbackShown);
    }
}
