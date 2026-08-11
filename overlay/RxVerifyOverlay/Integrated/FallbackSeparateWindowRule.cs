namespace RxVerifyOverlay.Integrated;

/// <summary>
/// The new value for IntegratedOverlayCoordinator's
/// _fallbackSeparateWindowShown flag, plus whether to raise
/// ShowSeparateWindowRequested/HideSeparateWindowRequested — at most one
/// of the two is ever true for a single decision.
/// </summary>
public readonly record struct FallbackWindowDecision(bool NewFallbackShown, bool RaiseShow, bool RaiseHide);

/// <summary>
/// Pure decision behind the "invisible-app trap" fallback-window
/// bookkeeping in IntegratedOverlayCoordinator.TickCore — split out from
/// the coordinator (which owns the actual PioneerRxWindow attach/WPF
/// Show()/Hide() side effects) so this state machine is covered by fast
/// xUnit tests, same pattern as IntegratedVisibilityGate.
///
/// THE REGRESSION THIS ENCODES (confirmed by review, fixed here): leaving
/// Integrated mode must clear a stale "true" flag WITHOUT raising Hide.
/// Sequence that broke without this rule: Integrated mode + PioneerRx not
/// attached -> fallback shows MainWindow (flag becomes true) -> the
/// pharmacist clicks "Separate window" IN THAT WINDOW -> SetDisplayMode
/// shows it (DisplayMode is now Separate) -> the very next Tick() call
/// hits the non-Integrated path, sees the still-true flag, and (if it
/// were allowed to raise Hide here) immediately re-hides the window the
/// pharmacist just switched to — with DisplayMode now Separate, nothing
/// would ever show it again short of a process relaunch, i.e. an app
/// LESS recoverable than the invisible-app trap this fallback exists to
/// close in the first place. MainWindow's visibility for the MODE
/// TRANSITION itself is already owned by
/// IntegratedOverlayCoordinator.SetDisplayMode's own
/// Show/HideSeparateWindowRequested call — this rule only ever governs
/// the FALLBACK path (Integrated mode, PioneerRx not attached at all).
/// </summary>
public static class FallbackSeparateWindowRule
{
    public static FallbackWindowDecision Decide(bool isIntegratedMode, bool isPioneerAttached, bool wasFallbackShown)
    {
        if (!isIntegratedMode)
        {
            // See class doc regression: clear the bookkeeping so it's
            // accurate the next time Integrated mode needs it, but NEVER
            // raise Hide from this path.
            return new FallbackWindowDecision(NewFallbackShown: false, RaiseShow: false, RaiseHide: false);
        }

        if (!isPioneerAttached)
        {
            // Show the fallback, but only raise Show on the actual edge
            // (not already shown) — a pharmacist who manually re-hides
            // this window mid-detached-state must not be fought by the
            // next tick re-showing it.
            return new FallbackWindowDecision(NewFallbackShown: true, RaiseShow: !wasFallbackShown, RaiseHide: false);
        }

        // PioneerRx is back — hide the fallback only if THIS mechanism
        // was the one that showed it (never hides a window the
        // pharmacist opened themselves via "Open full view").
        return new FallbackWindowDecision(NewFallbackShown: false, RaiseShow: false, RaiseHide: wasFallbackShown);
    }
}
