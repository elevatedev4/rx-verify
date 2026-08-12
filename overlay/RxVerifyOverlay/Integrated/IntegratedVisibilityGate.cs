using RxVerifyOverlay.Uia;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Pure decision logic for whether the integrated boxes layer / control
/// box should be visible right now. See IntegratedOverlayCoordinator,
/// the only production caller — kept here as a standalone boolean-in/
/// boolean-out class (no WPF/UIA dependency directly — CommonTabState is
/// a plain tri-state enum with no FlaUI/UIA type behind it) so it's
/// covered by fast xUnit tests, same pattern as Uia/AttachCacheDecision.cs
/// and Ocr/OcrSourceUsability.cs.
/// </summary>
public static class IntegratedVisibilityGate
{
    /// <summary>
    /// The boxes layer must be hidden whenever ANY of: PioneerRx isn't
    /// currently attached, PioneerRx isn't the OS foreground window
    /// (otherwise boxes would float over whatever app the pharmacist
    /// switched to), PioneerRx isn't maximized (integrated mode is
    /// MAXIMIZED-ONLY per the owner's spec), there's nothing verified yet
    /// to draw boxes for (no category has data yet, or the current screen
    /// isn't a parseable escript — mirrors OverlayViewModel's existing
    /// non-escript blank-state signal, see IntegratedOverlayCoordinator
    /// for how hasVerifiableContent is computed from OverlayViewModel.
    /// Categories/HasNonEscriptMessage), the outer Common tab is
    /// confirmed OFF (<paramref name="commonTabState"/> —
    /// CommonTabState.Off, see Uia/CommonTabGate.cs — this is the
    /// STRONGEST signal and short-circuits every other input, since the
    /// owner confirmed the older hasResolvableFieldRects proxy below does
    /// NOT actually catch this case: RxDetailsPanel's fields keep
    /// non-empty BoundingRectangles even on a different outer tab), the
    /// entered fields aren't currently resolvable to on-screen rects
    /// (round 4 addendum item 6 — the ORIGINAL best-effort proxy for
    /// "PioneerRx isn't on the Common tab right now", still used as-is
    /// whenever commonTabState is Unknown or On — see
    /// IntegratedOverlayCoordinator's doc), or the pharmacist has hidden
    /// the overlay themselves (round 4 item 2 — the control box's
    /// checkbox / the global `\` hotkey).
    /// </summary>
    public static bool ShouldShowBoxes(bool isAttached, bool isForeground, bool isMaximized, bool hasVerifiableContent, bool hasResolvableFieldRects, bool isHiddenByToggle, CommonTabState commonTabState)
    {
        if (commonTabState == CommonTabState.Off) return false;

        return isAttached && isForeground && isMaximized && hasVerifiableContent && hasResolvableFieldRects && !isHiddenByToggle;
    }

    /// <summary>
    /// OWNER FEEDBACK (round 2, item 1 — "leave it anchored in that top
    /// right corner space, even when it's not actively looking at a
    /// prescription"): the control box stays visible any time PioneerRx
    /// is the foreground application, REGARDLESS of maximized state (when
    /// not maximized it switches to the "maximize to use integrated view"
    /// note instead — see ControlBoxWindow.SetMaximizedGuardState) and
    /// REGARDLESS of whether a specific Pre-Check/Edit/New-Rx screen is
    /// open at all. Only the verdict BOXES layer (ShouldShowBoxes below)
    /// stays gated to an actual attached Rx screen with verified content —
    /// it needs real field rects to draw over, which only exist while a
    /// specific Rx is open. <paramref name="isPioneerForegroundApp"/> is
    /// the BROADER "is PioneerRx the app the pharmacist is currently
    /// looking at" signal (matched by owning PROCESS, not window title —
    /// see IntegratedOverlayCoordinator.TryGetForegroundPioneerRxWindow),
    /// deliberately looser than the narrow per-screen "isAttached" used
    /// for ShouldShowBoxes, so the control box stays anchored while the
    /// pharmacist is on PioneerRx's queue/search/dashboard between
    /// prescriptions, not just while actively editing one.
    /// </summary>
    public static bool ShouldShowControlBox(bool isPioneerForegroundApp)
    {
        return isPioneerForegroundApp;
    }
}
