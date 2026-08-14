namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Pure decision behind the owner's Order/Verify mode EXCLUSIVITY spec
/// (live pharmacy report, 2026-08-14: "activating 'Order mode' instead of
/// Verify mode ... make sure that the logic will work with the popup
/// window"): while Order Assist ("Order mode") is enabled, the
/// rx-verification boxes/hover layer must be suppressed entirely — never
/// drawn — and IntegratedOverlayCoordinator.TickCore skips the underlying
/// narrow Pre-Check/Edit/New-Rx UIA attach too, since its result could
/// never be shown anyway (see TickCore's own call site for the actual
/// early-out). Switching back to Verify mode resumes both immediately on
/// the next tick — there is no separate "resume" step, TickCore's normal
/// gates (IntegratedVisibilityGate.ShouldShowBoxes, etc.) just start
/// running again.
///
/// Reads ONLY the plain OverlaySettings.OrderAssistEnabled bool
/// IntegratedOverlayCoordinator already holds — see that class's own
/// OrderAssistToggleRequested doc for why a plain bool is the SOLE thing
/// allowed to cross from the OrderAssist module into this one. This gate
/// never references any OrderAssist.* type, keeping that boundary intact
/// (it lives in the Integrated namespace, not OrderAssist).
/// </summary>
public static class VerifyModeGate
{
    /// <summary>
    /// True whenever Order mode is active and the verify boxes/hover layer
    /// must be suppressed. Trivial today (a direct passthrough of the
    /// flag) — kept as its own named, tested decision rather than an
    /// inline check in TickCore so the EXCLUSIVITY rule has exactly one
    /// place to read, and one place a future addition (e.g. a third mode)
    /// would need to change.
    /// </summary>
    public static bool ShouldSuppressVerifyBoxes(bool orderAssistEnabled) => orderAssistEnabled;
}
