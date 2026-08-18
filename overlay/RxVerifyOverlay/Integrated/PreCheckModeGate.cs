using RxVerifyOverlay.Uia;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Pure decision behind Will's 2026-08-18 ask, verbatim: "RxVerify verify
/// mode should only do checks when in Pre-Check mode (from title bar), not
/// when in other modes, like Edit Rx". The verify boxes/hover/right-click
/// hotspots layer (Integrated/IntegratedBoxesWindow.cs, driven from
/// IntegratedOverlayCoordinator.TickCore) previously activated on ANY of
/// the 3 attachable Pioneer screens (FieldMap.TargetWindowTitlePrefixes:
/// Pre-Check Rx / Edit Rx / New Rx) — this gate restricts it to Pre-Check
/// only, same "one named, tested decision" pattern as VerifyModeGate's own
/// Order/Verify exclusivity check right next to it in TickCore.
///
/// SCOPE (per the brief): this ONLY gates the Integrated overlay's boxes/
/// hover/right-click — it does NOT touch OrderAssist's own, entirely
/// separate window-detection/activation (OrderAssist/Windows/
/// OrderAssistWindowLocator.cs and OrderAssistWindowClassifier.cs never
/// reference PioneerRxWindow, FieldMap.TargetWindowTitlePrefixes, or this
/// gate at all), and does NOT touch the Separate window's own verdict
/// table or OverlayViewModel's background field-verdict computation —
/// only what's actually DRAWN over Pioneer in Integrated mode.
/// </summary>
public static class PreCheckModeGate
{
    /// <summary>
    /// True only for RxScreenMode.PreCheck and RxScreenMode.Unknown —
    /// see RxScreenMode's own doc for when Unknown happens (an
    /// unreadable/null/empty title, since TryAttach only ever attaches
    /// to a title that already starts with one of the 3 known prefixes,
    /// so a REAL classification mismatch on an attached window should not
    /// normally occur).
    ///
    /// DEFAULT-ACTIVE ON UNKNOWN is a deliberate choice, not an oversight:
    /// the alternative (default-suppress) would make an unreadable title —
    /// a transient UIA hiccup, not a real "this is Edit Rx" signal — look
    /// exactly like the app silently breaking on every Pre-Check screen,
    /// which is worse than occasionally showing checks one screen too many
    /// (the previous, pre-this-branch behavior, which already ran on all
    /// 3 screens with no complaints about the Pre-Check case itself — only
    /// about Edit Rx). There is no evidence in this codebase that the
    /// title is unreliable to read once TryAttach has already succeeded
    /// (RxNumber parsing, TitleStillMatches, GetScreenSignature all treat
    /// a successful attach's .Name as trustworthy) — if that ever proves
    /// false in the field, this is the one place to flip.
    /// </summary>
    public static bool ShouldRunVerifyChecks(RxScreenMode mode) =>
        mode is RxScreenMode.PreCheck or RxScreenMode.Unknown;
}
